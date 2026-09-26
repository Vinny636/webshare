# webshare
This tool is meant to make sharing a single file or folder as simple as possible. It accepts
a single parameter which is either a file or a folder, and then a web server is started with
the file or folder being the root directory of what's being served.

The server is HTTP-only, uses an available ephemeral port by default, and writes the URL to
access the content to the console. The `--port` option selects a specific port. Sharing lasts
60 minutes by default; `--time` sets the duration in minutes, with `0` meaning indefinitely.
The server listens on all network interfaces for LAN sharing and displays the active
non-loopback IP addresses. The server has no authentication, so anyone who can reach the
machine and port can access the shared content.

When sharing a folder, visitors see a custom, responsive file listing with links to download
files and browse subfolders. When sharing a single file, visitors see a styled landing page
with a download link. The page's CSS is embedded in the application. Symbolic links inside a
shared folder are skipped.

# Technology
The tool is written in .NET 10, uses the Kestrel server, and can be published as a Native AOT
binary for the target platform.
