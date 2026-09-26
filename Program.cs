using System.Net;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;

/// <summary>Runs the command-line file-sharing server.</summary>
internal static class Program
{
    private const string StylesResourceName = "WebShare.Assets.site.css";

    /// <summary>Runs the application and returns an appropriate process exit code.</summary>
    private static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    /// <summary>Validates options, configures the server, and manages the share lifetime.</summary>
    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        if (!TryParseArguments(
            args,
            out var sharedArgument,
            out var port,
            out var timeInMinutes,
            out var openFirewall,
            out var error))
        {
            if (error.Length > 0)
            {
                Console.Error.WriteLine($"Error: {error}");
            }

            PrintUsage();
            return 1;
        }

        if (openFirewall && !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("--open-firewall is supported only on Windows and Linux.");
        }

        // Resolve the share path and validate it before starting the server.
        var sharedPath = Path.GetFullPath(sharedArgument);
        var isDirectory = Directory.Exists(sharedPath);
        if (!isDirectory && !File.Exists(sharedPath))
        {
            Console.Error.WriteLine($"Error: '{sharedArgument}' is not an existing file or directory.");
            return 1;
        }

        var sharedInfo = isDirectory
            ? (FileSystemInfo)new DirectoryInfo(sharedPath)
            : new FileInfo(sharedPath);
        if (sharedInfo.ResolveLinkTarget(returnFinalTarget: true) is { } linkTarget)
        {
            sharedPath = Path.GetFullPath(linkTarget.FullName);
        }

        var displayName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sharedPath));
        if (string.IsNullOrEmpty(displayName))
        {
            displayName = isDirectory ? "Shared folder" : "Shared file";
        }

        var styles = ReadEmbeddedStyles();
        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(port));

        await using var app = builder.Build();
        // Keep request failures generic and apply consistent security headers.
        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("An unexpected error occurred while processing the request.");
        }));
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await next(context);
        });

        // A directory uses a browsable listing; a single file gets a download page.
        if (isDirectory)
        {
            app.MapGet("/", () => CreateListingResult(sharedPath, displayName, string.Empty, styles));
            app.MapGet("/{**relativePath}", (string? relativePath) =>
                HandleDirectoryRequest(sharedPath, displayName, styles, relativePath));
        }
        else
        {
            app.MapGet("/", () => CreateFilePageResult(sharedPath, displayName, styles));
            app.MapGet("/download", () => Results.File(
                sharedPath,
                contentType: "application/octet-stream",
                fileDownloadName: displayName,
                enableRangeProcessing: true));
            app.MapGet("/{**relativePath}", () => Results.NotFound());
        }

        // Bind first so a dynamically assigned port is known for the firewall rule and URLs.
        await app.StartAsync();
        var listeningPort = GetListeningPort(app);
        await using var firewallException = openFirewall
            ? await TemporaryFirewallException.OpenAsync(listeningPort)
            : null;

        // Print share URLs directly because framework startup logs are filtered above.
        Console.WriteLine($"Sharing {(isDirectory ? "folder" : "file")}: {displayName}");
        Console.WriteLine(timeInMinutes == 0
            ? "Share duration: indefinite."
            : $"Share will stop after {timeInMinutes} minute(s).");
        Console.WriteLine("Open one of these URLs:");
        Console.WriteLine($"  http://localhost:{listeningPort}/");
        var networkAddresses = GetNetworkAddresses();
        foreach (var address in networkAddresses)
        {
            var host = address.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{address}]"
                : address.ToString();
            Console.WriteLine($"  http://{host}:{listeningPort}/");
        }

        if (networkAddresses.Count == 0)
        {
            Console.WriteLine("  No active non-loopback IPv4 address was found.");
        }

        Console.WriteLine(openFirewall
            ? $"Temporary firewall exception active for TCP port {listeningPort}; it will be removed on exit."
            : $"If other devices cannot connect, allow inbound TCP port {listeningPort} through the firewall.");
        Console.WriteLine("Anyone who can reach this machine on the network can access the shared content.");
        Console.WriteLine("Press Ctrl+C to stop sharing.");

        // Stop when either the requested duration expires or the host begins shutting down.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
        var timeoutTask = timeInMinutes == 0
            ? Task.CompletedTask
            : StopAfterAsync(app, timeInMinutes, timeoutCts.Token);
        await app.WaitForShutdownAsync();
        await timeoutTask;
        return 0;
    }

    /// <summary>Prints command-line usage and supported options.</summary>
    private static void PrintUsage()
    {
        Console.WriteLine("webshare");
        Console.WriteLine("--------");
        Console.WriteLine("Creates a temporary web server pointing to a specific file or directory to make sharing files");
        Console.WriteLine("across a network as quick and seamless as possible.");
        Console.WriteLine();
        Console.WriteLine("Usage: webshare [--port <port>] [--time <minutes>] [--open-firewall] <file-or-directory>");
        Console.WriteLine("       --port <port>     Port to listen on (default: dynamically assigned)");
        Console.WriteLine("       --time <minutes>  Stop after this many minutes; 0 runs indefinitely (default: 60)");
        Console.WriteLine("       --open-firewall   Temporarily allow inbound access (requires admin/root)");
        Console.WriteLine("       --help            Shows this help");
    }

    /// <summary>Parses options and validates the single file-or-directory argument.</summary>
    private static bool TryParseArguments(
        string[] args,
        out string sharedPath,
        out int port,
        out int timeInMinutes,
        out bool openFirewall,
        out string error)
    {
        sharedPath = string.Empty;
        port = 0;
        timeInMinutes = 60;
        openFirewall = false;
        error = string.Empty;
        var portSpecified = false;
        var timeSpecified = false;
        var openFirewallSpecified = false;

        // Options may appear before or after the positional share path.
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--open-firewall")
            {
                if (openFirewallSpecified)
                {
                    error = "'--open-firewall' may only be specified once.";
                    return false;
                }

                openFirewall = true;
                openFirewallSpecified = true;
                continue;
            }

            if (argument is "--port" or "--time" ||
                argument.StartsWith("--port=", StringComparison.Ordinal) ||
                argument.StartsWith("--time=", StringComparison.Ordinal))
            {
                var option = argument.StartsWith("--port", StringComparison.Ordinal) ? "--port" : "--time";
                var isPort = option == "--port";
                if (isPort ? portSpecified : timeSpecified)
                {
                    error = $"'{option}' may only be specified once.";
                    return false;
                }

                string value;
                var equalsIndex = argument.IndexOf('=');
                if (equalsIndex >= 0)
                {
                    value = argument[(equalsIndex + 1)..];
                }
                else if (index + 1 < args.Length)
                {
                    value = args[++index];
                }
                else
                {
                    error = $"'{option}' requires a value.";
                    return false;
                }

                // Parse each numeric option once, then enforce its supported range.
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedValue))
                {
                    error = $"'{option}' must be a whole number.";
                    return false;
                }

                if (isPort)
                {
                    if (parsedValue is < 1 or > 65535)
                    {
                        error = "'--port' must be between 1 and 65535.";
                        return false;
                    }

                    port = parsedValue;
                    portSpecified = true;
                }
                else
                {
                    if (parsedValue < 0)
                    {
                        error = "'--time' cannot be negative.";
                        return false;
                    }

                    timeInMinutes = parsedValue;
                    timeSpecified = true;
                }

                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unknown option '{argument}'.";
                return false;
            }

            if (sharedPath.Length > 0)
            {
                error = "Only one file or directory may be shared.";
                return false;
            }

            sharedPath = argument;
        }

        if (sharedPath.Length == 0)
        {
            error = "A file or directory to share is required.";
            return false;
        }

        return true;
    }

    /// <summary>Stops the server after the configured duration unless shutdown starts first.</summary>
    private static async Task StopAfterAsync(
        WebApplication app,
        int timeInMinutes,
        CancellationToken cancellationToken)
    {
        var remaining = TimeSpan.FromMinutes(timeInMinutes);
        while (remaining > TimeSpan.Zero)
        {
            var delay = remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining;
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            remaining -= Stopwatch.GetElapsedTime(startedAt);
        }

        app.Lifetime.StopApplication();
    }

    /// <summary>Loads the CSS resource embedded in the published executable.</summary>
    private static string ReadEmbeddedStyles()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(StylesResourceName)
            ?? throw new InvalidOperationException($"Embedded stylesheet '{StylesResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Gets the actual port assigned by Kestrel, including when port zero was requested.</summary>
    private static int GetListeningPort(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault(value => Uri.TryCreate(value, UriKind.Absolute, out _));
        if (address is null)
        {
            throw new InvalidOperationException("Kestrel did not report its listening address.");
        }

        return new Uri(address).Port;
    }

    /// <summary>Returns active non-loopback addresses that can be used to reach the server.</summary>
    private static IReadOnlyList<IPAddress> GetNetworkAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface =>
                networkInterface.OperationalStatus == OperationalStatus.Up &&
                networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(unicastAddress => unicastAddress.Address)
            .Where(address => address.AddressFamily switch
            {
                AddressFamily.InterNetwork => !IPAddress.IsLoopback(address),
                AddressFamily.InterNetworkV6 =>
                    !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal && !address.IsIPv6Multicast,
                _ => false
            })
            .Distinct()
            .OrderBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Serves a directory listing, a file download, or a not-found response.</summary>
    private static IResult HandleDirectoryRequest(
        string rootPath,
        string rootDisplayName,
        string styles,
        string? relativePath)
    {
        if (!TryResolvePath(rootPath, relativePath, out var targetPath))
        {
            return Results.NotFound();
        }

        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        if (normalizedRelativePath.Length > 0 && ContainsReparsePoint(rootPath, normalizedRelativePath))
        {
            return Results.NotFound();
        }

        if (Directory.Exists(targetPath))
        {
            return CreateListingResult(rootPath, rootDisplayName, normalizedRelativePath, styles);
        }

        if (File.Exists(targetPath))
        {
            return Results.File(
                targetPath,
                contentType: "application/octet-stream",
                fileDownloadName: Path.GetFileName(targetPath),
                enableRangeProcessing: true);
        }

        return Results.NotFound();
    }

    /// <summary>Resolves a relative request path without allowing it to escape the shared root.</summary>
    private static bool TryResolvePath(string rootPath, string? relativePath, out string targetPath)
    {
        var normalizedRelativePath = NormalizeRelativePath(relativePath)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var segments = normalizedRelativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);

        // Reject traversal segments and verify the canonical target remains under the root.
        if (segments.Any(segment => segment is "." or ".."))
        {
            targetPath = string.Empty;
            return false;
        }

        targetPath = Path.GetFullPath(Path.Combine(rootPath, Path.Combine(segments)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootPrefix = Path.EndsInDirectorySeparator(rootPath)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;

        return targetPath.Equals(rootPath, comparison) || targetPath.StartsWith(rootPrefix, comparison);
    }

    /// <summary>Trims URL separators from a relative path.</summary>
    private static string NormalizeRelativePath(string? relativePath)
    {
        return (relativePath ?? string.Empty).Trim('/');
    }

    /// <summary>Detects reparse points along a path so links are not followed while sharing.</summary>
    private static bool ContainsReparsePoint(string rootPath, string relativePath)
    {
        var currentPath = rootPath;
        var segments = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        // Check each existing component, not just the final target, for a link or junction.
        foreach (var segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                return false;
            }

            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Creates an HTML response for a directory listing.</summary>
    private static IResult CreateListingResult(
        string rootPath,
        string rootDisplayName,
        string relativePath,
        string styles)
    {
        return Results.Content(
            BuildListing(rootPath, rootDisplayName, relativePath, styles),
            "text/html; charset=utf-8",
            Encoding.UTF8);
    }

    /// <summary>Builds the styled download page for a shared file.</summary>
    private static IResult CreateFilePageResult(string filePath, string displayName, string styles)
    {
        var escapedName = HtmlEncoder.Default.Encode(displayName);
        var fileSize = FormatFileSize(new FileInfo(filePath).Length);
        var html = new StringBuilder();

        // Compose the page from escaped file metadata and the embedded stylesheet.
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>").Append(escapedName).AppendLine(" · webshare</title>");
        html.Append("<style>").Append(styles).AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<main class=\"page-shell\">");
        html.AppendLine("<header class=\"topbar\">");
        html.AppendLine("<a class=\"brand\" href=\"/\" aria-label=\"webshare home\">");
        html.AppendLine("<span class=\"brand-mark\" aria-hidden=\"true\"><span></span><span></span><span></span></span>");
        html.AppendLine("<span>webshare</span>");
        html.AppendLine("</a>");
        html.AppendLine("<span class=\"share-status\"><span class=\"status-dot\"></span>Temporary share</span>");
        html.AppendLine("</header>");
        html.AppendLine("<section class=\"intro\">");
        html.AppendLine("<p class=\"eyebrow\">SHARED FILE</p>");
        html.Append("<h1>").Append(escapedName).AppendLine("</h1>");
        html.AppendLine("<p class=\"intro-copy\">This file is ready to download.</p>");
        html.AppendLine("</section>");
        html.AppendLine("<section class=\"file-download-card\" aria-label=\"Shared file\">");
        html.AppendLine("<div class=\"file-summary\">");
        html.Append("<span class=\"file-icon document-icon\" aria-hidden=\"true\">").Append(FileIcon).AppendLine("</span>");
        html.AppendLine("<span class=\"file-details\">");
        html.Append("<strong>").Append(escapedName).AppendLine("</strong>");
        html.Append("<span>").Append(fileSize).AppendLine("</span>");
        html.AppendLine("</span>");
        html.AppendLine("</div>");
        html.AppendLine("<a class=\"download-button\" href=\"/download\">");
        html.AppendLine("<span>Download file</span>");
        html.AppendLine("<svg viewBox=\"0 0 20 20\" fill=\"none\" aria-hidden=\"true\"><path d=\"M10 2.75v9.5m0 0 3.5-3.5M10 12.25l-3.5-3.5M3.5 13.75v2.5h13v-2.5\" stroke=\"currentColor\" stroke-width=\"1.7\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>");
        html.AppendLine("</a>");
        html.AppendLine("<p class=\"download-note\">Your browser will save a copy of this file.</p>");
        html.AppendLine("</section>");
        html.AppendLine("<footer class=\"page-footer\">");
        html.AppendLine("<span>Available on this machine's network interfaces while this app is running.</span>");
        html.AppendLine("<span>Stop sharing with Ctrl+C in the terminal.</span>");
        html.AppendLine("</footer>");
        html.AppendLine("</main>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return Results.Content(html.ToString(), "text/html; charset=utf-8", Encoding.UTF8);
    }

    /// <summary>Builds the browsable HTML listing for a shared directory.</summary>
    private static string BuildListing(
        string rootPath,
        string rootDisplayName,
        string relativePath,
        string styles)
    {
        var directoryPath = relativePath.Length == 0
            ? rootPath
            : Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var entries = new List<ListingEntry>();

        // Exclude links and collect metadata before sorting entries for display.
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            var attributes = File.GetAttributes(entryPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var name = Path.GetFileName(entryPath);
            var modifiedUtc = isDirectory
                ? Directory.GetLastWriteTimeUtc(entryPath)
                : File.GetLastWriteTimeUtc(entryPath);
            long? size = isDirectory ? null : new FileInfo(entryPath).Length;
            var childPath = relativePath.Length == 0 ? name : $"{relativePath}/{name}";

            entries.Add(new ListingEntry(name, childPath, isDirectory, size, modifiedUtc));
        }

        entries.Sort((left, right) =>
        {
            var directoryOrder = right.IsDirectory.CompareTo(left.IsDirectory);
            if (directoryOrder != 0)
            {
                return directoryOrder;
            }

            var nameOrder = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            return nameOrder != 0 ? nameOrder : StringComparer.Ordinal.Compare(left.Name, right.Name);
        });

        var heading = relativePath.Length == 0
            ? rootDisplayName
            : Path.GetFileName(relativePath);
        var breadcrumbs = BuildBreadcrumbs(rootDisplayName, relativePath);
        var escapedHeading = HtmlEncoder.Default.Encode(heading);
        var itemLabel = entries.Count == 1 ? "1 item" : $"{entries.Count} items";
        var html = new StringBuilder();

        // Render the listing, including a distinct empty state for folders with no entries.
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>").Append(escapedHeading).AppendLine(" · webshare</title>");
        html.Append("<style>").Append(styles).AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<main class=\"page-shell\">");
        html.AppendLine("<header class=\"topbar\">");
        html.AppendLine("<a class=\"brand\" href=\"/\" aria-label=\"webshare home\">");
        html.AppendLine("<span class=\"brand-mark\" aria-hidden=\"true\"><span></span><span></span><span></span></span>");
        html.AppendLine("<span>webshare</span>");
        html.AppendLine("</a>");
        html.AppendLine("<span class=\"share-status\"><span class=\"status-dot\"></span>Temporary share</span>");
        html.AppendLine("</header>");
        html.AppendLine("<section class=\"intro\">");
        html.AppendLine("<p class=\"eyebrow\">SHARED FOLDER</p>");
        html.Append("<h1>").Append(escapedHeading).AppendLine("</h1>");
        html.AppendLine("<p class=\"intro-copy\">Browse the contents and select a file to download.</p>");
        html.AppendLine("</section>");
        html.Append("<nav class=\"breadcrumbs\" aria-label=\"Breadcrumb\">")
            .Append(breadcrumbs)
            .AppendLine("</nav>");
        html.AppendLine("<section class=\"listing-card\" aria-label=\"Folder contents\">");
        html.AppendLine("<div class=\"listing-toolbar\">");
        html.AppendLine("<span class=\"listing-title\">Contents</span>");
        html.Append("<span class=\"item-count\">").Append(itemLabel).AppendLine("</span>");
        html.AppendLine("</div>");

        if (entries.Count == 0)
        {
            html.AppendLine("<div class=\"empty-state\">");
            html.AppendLine("<span class=\"empty-icon\" aria-hidden=\"true\">");
            html.AppendLine("<svg viewBox=\"0 0 48 48\" fill=\"none\"><path d=\"M9 15.5A3.5 3.5 0 0 1 12.5 12h8l3 3.5h12A3.5 3.5 0 0 1 39 19v14.5a3.5 3.5 0 0 1-3.5 3.5h-23A3.5 3.5 0 0 1 9 33.5v-18Z\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linejoin=\"round\"/><path d=\"M9.5 21h29\" stroke=\"currentColor\" stroke-width=\"2\"/></svg>");
            html.AppendLine("</span>");
            html.AppendLine("<strong>This folder is empty</strong>");
            html.AppendLine("<span>Files added here will appear in this list.</span>");
            html.AppendLine("</div>");
        }
        else
        {
            html.AppendLine("<div class=\"table-head\"><span>Name</span><span>Type</span><span>Size</span><span>Modified</span></div>");
            html.AppendLine("<div class=\"entry-list\">");
            if (relativePath.Length > 0)
            {
                var parentPath = ParentPath(relativePath);
                AppendParentEntry(html, parentPath);
            }

            foreach (var entry in entries)
            {
                AppendEntry(html, entry);
            }

            html.AppendLine("</div>");
        }

        html.AppendLine("</section>");
        html.AppendLine("<footer class=\"page-footer\">");
        html.AppendLine("<span>Available on this machine's network interfaces while this app is running.</span>");
        html.AppendLine("<span>Stop sharing with Ctrl+C in the terminal.</span>");
        html.AppendLine("</footer>");
        html.AppendLine("</main>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    /// <summary>Builds escaped breadcrumb links for the current directory path.</summary>
    private static string BuildBreadcrumbs(string rootDisplayName, string relativePath)
    {
        var html = new StringBuilder();
        html.Append("<a href=\"/\">Home</a>");
        if (relativePath.Length == 0)
        {
            html.Append("<span class=\"crumb-divider\" aria-hidden=\"true\">/</span>");
            html.Append("<span class=\"crumb-current\">")
                .Append(HtmlEncoder.Default.Encode(rootDisplayName))
                .Append("</span>");
            return html.ToString();
        }

        html.Append("<span class=\"crumb-divider\" aria-hidden=\"true\">/</span>");
        html.Append("<a href=\"/\">")
            .Append(HtmlEncoder.Default.Encode(rootDisplayName))
            .Append("</a>");

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var accumulatedPath = new StringBuilder();
        // Link ancestor segments and render the final segment as the current location.
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            accumulatedPath.Append(segment).Append('/');
            var escapedSegment = HtmlEncoder.Default.Encode(segment);
            html.Append("<span class=\"crumb-divider\" aria-hidden=\"true\">/</span>");
            if (index == segments.Length - 1)
            {
                html.Append("<span class=\"crumb-current\">").Append(escapedSegment).Append("</span>");
            }
            else
            {
                var link = HtmlEncoder.Default.Encode(BuildLink(accumulatedPath.ToString().TrimEnd('/'), isDirectory: true));
                html.Append("<a href=\"").Append(link).Append("\">").Append(escapedSegment).Append("</a>");
            }
        }

        return html.ToString();
    }

    /// <summary>Adds the parent-directory row to a nested listing.</summary>
    private static void AppendParentEntry(StringBuilder html, string parentPath)
    {
        var link = HtmlEncoder.Default.Encode(BuildLink(parentPath, isDirectory: true));
        html.AppendLine("<div class=\"entry-row parent-row\">");
        html.Append("<a class=\"name-cell\" href=\"").Append(link).AppendLine("\">");
        html.Append("<span class=\"file-icon folder-icon\" aria-hidden=\"true\">").Append(FolderIcon).AppendLine("</span>");
        html.AppendLine("<span class=\"name-stack\"><span class=\"entry-name\">..</span><span class=\"entry-subtitle\">Parent folder</span></span>");
        html.AppendLine("</a>");
        html.AppendLine("<span class=\"type-cell\">Folder</span><span class=\"size-cell\">—</span><span class=\"modified-cell\">—</span>");
        html.AppendLine("</div>");
    }

    /// <summary>Adds a file or directory row to the listing.</summary>
    private static void AppendEntry(StringBuilder html, ListingEntry entry)
    {
        var link = HtmlEncoder.Default.Encode(BuildLink(entry.RelativePath, entry.IsDirectory));
        var name = HtmlEncoder.Default.Encode(entry.Name);
        var kind = entry.IsDirectory ? "Folder" : "File";
        var size = entry.Size is { } bytes ? FormatFileSize(bytes) : "—";
        var modified = entry.LastModifiedUtc.ToLocalTime().ToString("MMM d, yyyy, h:mm tt");
        var icon = entry.IsDirectory ? FolderIcon : FileIcon;
        var iconClass = entry.IsDirectory ? "folder-icon" : "document-icon";

        html.AppendLine("<div class=\"entry-row\">");
        html.Append("<a class=\"name-cell\" href=\"").Append(link).AppendLine("\">");
        html.Append("<span class=\"file-icon ").Append(iconClass).Append("\" aria-hidden=\"true\">")
            .Append(icon)
            .AppendLine("</span>");
        html.Append("<span class=\"entry-name\">").Append(name).AppendLine("</span>");
        html.AppendLine("</a>");
        html.Append("<span class=\"type-cell\">").Append(kind).AppendLine("</span>");
        html.Append("<span class=\"size-cell\">").Append(size).AppendLine("</span>");
        html.Append("<time class=\"modified-cell\" datetime=\"")
            .Append(entry.LastModifiedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))
            .Append("\">")
            .Append(HtmlEncoder.Default.Encode(modified))
            .AppendLine("</time>");
        html.AppendLine("</div>");
    }

    /// <summary>Builds a URL-safe link for a relative file or directory path.</summary>
    private static string BuildLink(string relativePath, bool isDirectory)
    {
        if (relativePath.Length == 0)
        {
            return "/";
        }

        var encodedPath = string.Join(
            "/",
            relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        return $"/{encodedPath}{(isDirectory ? "/" : string.Empty)}";
    }

    /// <summary>Returns the parent path of a slash-separated relative directory path.</summary>
    private static string ParentPath(string relativePath)
    {
        var separatorIndex = relativePath.LastIndexOf('/');
        return separatorIndex < 0 ? string.Empty : relativePath[..separatorIndex];
    }

    /// <summary>Formats a byte count using a human-readable unit.</summary>
    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var size = (double)bytes;
        var units = new[] { "KB", "MB", "GB", "TB" };
        var unitIndex = -1;
        do
        {
            size /= 1024;
            unitIndex++;
        }
        while (size >= 1024 && unitIndex < units.Length - 1);

        return $"{size.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    private const string FolderIcon =
        "<svg viewBox=\"0 0 24 24\" fill=\"none\"><path d=\"M3.5 7.5A2 2 0 0 1 5.5 5.5h4l2 2h7a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-10Z\" stroke=\"currentColor\" stroke-width=\"1.7\" stroke-linejoin=\"round\"/><path d=\"M3.8 10h16.4\" stroke=\"currentColor\" stroke-width=\"1.7\"/></svg>";

    private const string FileIcon =
        "<svg viewBox=\"0 0 24 24\" fill=\"none\"><path d=\"M6.5 3.5h7l4 4v13h-11v-17Z\" stroke=\"currentColor\" stroke-width=\"1.7\" stroke-linejoin=\"round\"/><path d=\"M13.5 3.8v4h4M9 12h6M9 15.5h6\" stroke=\"currentColor\" stroke-width=\"1.7\" stroke-linecap=\"round\"/></svg>";

    /// <summary>Stores the metadata needed to render one file or directory row.</summary>
    private sealed record ListingEntry(
        string Name,
        string RelativePath,
        bool IsDirectory,
        long? Size,
        DateTime LastModifiedUtc);
}
