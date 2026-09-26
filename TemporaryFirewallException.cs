using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

/// <summary>Owns firewall rules that are removed when a share stops.</summary>
internal sealed class TemporaryFirewallException : IAsyncDisposable
{
    private readonly IReadOnlyList<FirewallRule> rules;
    private bool disposed;

    /// <summary>Creates a scope for the firewall rules installed by one app run.</summary>
    private TemporaryFirewallException(IReadOnlyList<FirewallRule> rules)
    {
        this.rules = rules;
    }

    /// <summary>Opens the selected TCP port to local subnets using the host firewall.</summary>
    public static async Task<TemporaryFirewallException> OpenAsync(int port)
    {
        if (OperatingSystem.IsWindows())
        {
            return await OpenWindowsAsync(port);
        }

        if (OperatingSystem.IsLinux())
        {
            return await OpenLinuxAsync(port);
        }

        throw new PlatformNotSupportedException("--open-firewall is supported only on Windows and Linux.");
    }

    /// <summary>Removes all rules created for this share and reports cleanup failures.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        // Attempt every removal so one failed command does not leave other rules behind.
        var errors = await RemoveRulesAsync(rules);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Failed to remove the temporary firewall exception. Firewall state may still be changed:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => error.Message)),
                new AggregateException(errors));
        }

        Console.WriteLine("Temporary firewall exception removed.");
    }

    /// <summary>Adds a Windows Firewall rule scoped to this executable and local subnet.</summary>
    private static async Task<TemporaryFirewallException> OpenWindowsAsync(int port)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) ||
            string.Equals(
                Path.GetFileNameWithoutExtension(executablePath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "--open-firewall on Windows requires running the published webshare executable.");
        }

        // A unique name ensures cleanup never removes a rule owned by another run.
        var ruleName = $"webshare-{Guid.NewGuid():N}";
        var rule = new FirewallRule(
            "netsh",
            ruleName,
            [
                "advfirewall", "firewall", "add", "rule",
                $"name={ruleName}", "dir=in", "action=allow", $"program={executablePath}",
                "protocol=TCP", $"localport={port}", "remoteip=LocalSubnet", "profile=private"
            ],
            [
                "advfirewall", "firewall", "delete", "rule",
                $"name={ruleName}"
            ]);

        try
        {
            await RunCommandAsync(rule.Command, rule.AddArguments);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not add the Windows Firewall exception. Run from an elevated terminal. {exception.Message}",
                exception);
        }

        Console.WriteLine($"Temporarily allowed TCP port {port} in Windows Firewall for the local subnet.");
        return new TemporaryFirewallException([rule]);
    }

    /// <summary>Adds iptables rules for the active local IPv4 and IPv6 subnets.</summary>
    private static async Task<TemporaryFirewallException> OpenLinuxAsync(int port)
    {
        var subnets = GetLocalSubnets();
        if (subnets.Count == 0)
        {
            throw new InvalidOperationException(
                "Could not determine an active local subnet for the temporary firewall exception.");
        }

        var ruleName = $"webshare-{Guid.NewGuid():N}";
        // Create one port-specific rule per local subnet and roll back partial setup.
        var rules = subnets
            .Select(subnet => CreateLinuxRule(subnet, port, ruleName))
            .ToArray();
        var addedRules = new List<FirewallRule>();

        try
        {
            foreach (var rule in rules)
            {
                await RunCommandAsync(rule.Command, rule.AddArguments);
                addedRules.Add(rule);
            }
        }
        catch (Exception exception)
        {
            var cleanupErrors = await RemoveRulesAsync(addedRules);
            var message =
                $"Could not add the temporary Linux firewall exception. Run with sudo and ensure iptables and ip6tables are available. {exception.Message}";
            if (cleanupErrors.Count > 0)
            {
                message += Environment.NewLine
                    + "Some partially added rules could not be removed:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, cleanupErrors.Select(error => error.Message));
                throw new InvalidOperationException(
                    message,
                    new AggregateException(new[] { exception }.Concat(cleanupErrors)));
            }

            throw new InvalidOperationException(message, exception);
        }

        Console.WriteLine(
            $"Temporarily allowed TCP port {port} from local subnet(s): "
            + string.Join(", ", subnets.Select(subnet => subnet.Prefix)));
        return new TemporaryFirewallException(addedRules);
    }

    /// <summary>Builds matching add and delete commands for one Linux subnet.</summary>
    private static FirewallRule CreateLinuxRule(LocalSubnet subnet, int port, string ruleName)
    {
        var command = subnet.AddressFamily == AddressFamily.InterNetwork ? "iptables" : "ip6tables";
        var matchArguments = new[]
        {
            "-p", "tcp",
            "-s", subnet.Prefix,
            "--dport", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-m", "comment",
            "--comment", ruleName,
            "-j", "ACCEPT"
        };
        var addArguments = new[] { "-w", "-I", "INPUT", "1" }.Concat(matchArguments).ToArray();
        var removeArguments = new[] { "-w", "-D", "INPUT" }.Concat(matchArguments).ToArray();

        return new FirewallRule(command, ruleName, addArguments, removeArguments);
    }

    /// <summary>Finds active non-loopback unicast network prefixes for rule source filters.</summary>
    private static IReadOnlyList<LocalSubnet> GetLocalSubnets()
    {
        var subnets = new Dictionary<string, LocalSubnet>(StringComparer.Ordinal);
        // Derive source ranges only from addresses on active network interfaces.
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicastAddress in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var address = unicastAddress.Address;
                if (!IsUsableAddress(address))
                {
                    continue;
                }

                var prefixLength = unicastAddress.PrefixLength;
                var addressLength = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                if (prefixLength is <= 0 || prefixLength > addressLength)
                {
                    continue;
                }

                var prefix = GetNetworkPrefix(address, prefixLength);
                var subnet = new LocalSubnet(prefix, address.AddressFamily);
                subnets.TryAdd($"{address.AddressFamily}:{prefix}", subnet);
            }
        }

        return subnets.Values
            .OrderBy(subnet => subnet.AddressFamily)
            .ThenBy(subnet => subnet.Prefix, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Checks whether an address can identify a local unicast network.</summary>
    private static bool IsUsableAddress(IPAddress address)
    {
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !IPAddress.IsLoopback(address),
            AddressFamily.InterNetworkV6 =>
                !IPAddress.IsLoopback(address) &&
                !address.IsIPv6LinkLocal &&
                !address.IsIPv6Multicast &&
                !address.IsIPv4MappedToIPv6,
            _ => false
        };
    }

    /// <summary>Clears host bits from an address to produce its CIDR network prefix.</summary>
    private static string GetNetworkPrefix(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        // Mask the partial byte, then clear the remaining host bytes.
        if (remainingBits > 0)
        {
            bytes[wholeBytes] &= (byte)(byte.MaxValue << (8 - remainingBits));
            wholeBytes++;
        }

        Array.Clear(bytes, wholeBytes, bytes.Length - wholeBytes);
        return $"{new IPAddress(bytes)}/{prefixLength}";
    }

    /// <summary>Attempts every rule removal and returns any failures for explicit reporting.</summary>
    private static async Task<IReadOnlyList<Exception>> RemoveRulesAsync(IEnumerable<FirewallRule> rules)
    {
        var errors = new List<Exception>();
        // Continue after failures so one bad rule does not block cleanup of the others.
        foreach (var rule in rules.Reverse())
        {
            try
            {
                await RunCommandAsync(rule.Command, rule.RemoveArguments);
            }
            catch (Exception exception)
            {
                errors.Add(new InvalidOperationException(
                    $"Could not remove firewall rule '{rule.Name}' with {rule.Command}: {exception.Message}",
                    exception));
            }
        }

        return errors;
    }

    /// <summary>Runs a firewall tool without a shell and throws when the command fails.</summary>
    private static async Task RunCommandAsync(string command, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Pass each argument directly to avoid shell parsing and quoting ambiguities.
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start firewall command '{command}'.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        if (process.ExitCode != 0)
        {
            var details = string.Join(
                Environment.NewLine,
                new[] { standardError.Trim(), standardOutput.Trim() }.Where(value => value.Length > 0));
            throw new InvalidOperationException(
                $"Firewall command '{command}' exited with code {process.ExitCode}."
                + (details.Length > 0 ? $"{Environment.NewLine}{details}" : string.Empty));
        }
    }

    /// <summary>Stores the command data needed to add and remove one firewall rule.</summary>
    private sealed record FirewallRule(
        string Command,
        string Name,
        IReadOnlyList<string> AddArguments,
        IReadOnlyList<string> RemoveArguments);

    /// <summary>Represents a local CIDR prefix and its IP address family.</summary>
    private sealed record LocalSubnet(string Prefix, AddressFamily AddressFamily);
}
