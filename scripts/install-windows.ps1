param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
    throw "Published executable not found: $Source"
}

$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$installPath = [IO.Path]::GetFullPath($InstallDirectory)
$executablePath = Join-Path $installPath "webshare.exe"

New-Item -ItemType Directory -Path $installPath -Force | Out-Null
Copy-Item -LiteralPath $sourcePath -Destination $executablePath -Force

Write-Host "Installed webshare to $executablePath"
