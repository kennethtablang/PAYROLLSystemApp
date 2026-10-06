<#
.SYNOPSIS
    Builds the Windows installer: PAYROLLSystemApp\Installer\PayrollSystemSetup-<version>.exe

.DESCRIPTION
    1. Optionally sets a new version number (installer script and project file).
    2. Publishes a self-contained Release build of the app into Export\app.
    3. Refreshes the installer's icon from the app's generated icon.
    4. Compiles the installer with Inno Setup.

.PARAMETER Version
    The new version, e.g. 1.1. Leave it out to rebuild the current version.

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -Version 1.1
#>
param(
    [ValidatePattern('^\d+\.\d+(\.\d+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
$project   = Join-Path $root 'PAYROLLSystemApp\PAYROLLSystemApp.csproj'
$issFile   = Join-Path $root 'installer\PayrollSystem.iss'
$appOut    = Join-Path $root 'Export\app'
$framework = 'net10.0-windows10.0.19041.0'
$iconCache = Join-Path $root "PAYROLLSystemApp\obj\Release\$framework\win-x64\resizetizer"

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Fail([string]$text) { Write-Host "`nFAILED: $text" -ForegroundColor Red; exit 1 }

# Rewrites a file keeping its byte-order mark as it was, so the project file
# keeps its BOM and the installer script stays plain.
function Update-File([string]$path, [string]$pattern, [string]$replacement) {
    $bytes  = [IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text   = [IO.File]::ReadAllText($path)
    if ($text -notmatch $pattern) { Fail "Could not find the version line in $path" }
    $text = [regex]::Replace($text, $pattern, $replacement)
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($hasBom)))
}

# ---------------------------------------------------------------- checks

Step 'Checking prerequisites'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail 'The .NET SDK (dotnet) is not installed.'
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Fail 'Inno Setup 6 is not installed. Install it with: winget install --id JRSoftware.InnoSetup -e'
}

# A copy started from Export\app locks the files the build has to replace.
# (The copy Visual Studio runs from bin\ does not matter.)
$running = Get-Process -Name 'PAYROLLSystemApp' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($appOut, [StringComparison]::OrdinalIgnoreCase) }

if ($running) {
    Fail "Payroll Management System is running from $appOut. Close it and run this again."
}

# ---------------------------------------------------------------- version

if ($Version) {
    Step "Setting version $Version"

    Update-File $issFile '#define AppVersion "[^"]*"' "#define AppVersion `"$Version`""
    Update-File $project '<ApplicationDisplayVersion>[^<]*</ApplicationDisplayVersion>' `
        "<ApplicationDisplayVersion>$Version</ApplicationDisplayVersion>"

    # The build number has to go up with every release for Windows to treat it as newer.
    $projectText = [IO.File]::ReadAllText($project)
    $build = [int]([regex]::Match($projectText, '<ApplicationVersion>(\d+)</ApplicationVersion>').Groups[1].Value) + 1
    Update-File $project '<ApplicationVersion>\d+</ApplicationVersion>' "<ApplicationVersion>$build</ApplicationVersion>"

    Write-Host "Version $Version (build $build) written to the installer script and the project."
}

$currentVersion = [regex]::Match([IO.File]::ReadAllText($issFile), '#define AppVersion "([^"]*)"').Groups[1].Value

# ---------------------------------------------------------------- publish

Step "Building the app (Release, self-contained) - this takes a few minutes"

# Cleared every time: the icon generator otherwise keeps an old logo.
if (Test-Path $iconCache) { Remove-Item $iconCache -Recurse -Force }

# RuntimeIdentifierOverride, not -r win-x64: the project also targets Android
# and iOS, and -r makes restore look for a runtime pack that does not exist.
& dotnet publish $project `
    -f $framework `
    -c Release `
    -p:RuntimeIdentifierOverride=win-x64 `
    -p:SelfContained=true `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -o $appOut `
    -nologo

if ($LASTEXITCODE -ne 0) { Fail 'The app did not build. Read the errors above.' }

# ---------------------------------------------------------------- icon

Step 'Refreshing the installer icon'

$generatedIcon = Join-Path $iconCache 'r\appicon.ico'
if (Test-Path $generatedIcon) {
    Copy-Item $generatedIcon (Join-Path $root 'installer\appicon.ico') -Force
    Write-Host 'installer\appicon.ico updated from the app icon.'
} else {
    Write-Host 'Generated icon not found; keeping the existing installer\appicon.ico.' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- installer

Step 'Compiling the installer'

& $iscc /Q $issFile
if ($LASTEXITCODE -ne 0) { Fail 'Inno Setup could not compile the installer. Read the errors above.' }

$setup = Join-Path $root "PAYROLLSystemApp\Installer\PayrollSystemSetup-$currentVersion.exe"
if (-not (Test-Path $setup)) { Fail "Expected $setup but it is not there." }

# The in-app updater refuses an installer whose SHA-256 does not match this file.
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$setupName = Split-Path $setup -Leaf
[IO.File]::WriteAllText("$setup.sha256", "$hash  $setupName`n", (New-Object Text.UTF8Encoding($false)))

# Signed with the private key kept outside the repo, so the updater can tell
# this release from one uploaded by anyone else (tools/release-signing.cs).
Step 'Signing the release checksum'
& dotnet run (Join-Path $root 'tools\release-signing.cs') -- sign "$setup.sha256"
if ($LASTEXITCODE -ne 0) { Fail 'The checksum could not be signed. Read the message above.' }

$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)

Write-Host "`nDONE  Installer for version $currentVersion ($sizeMb MB):" -ForegroundColor Green
Write-Host "      $setup" -ForegroundColor Green

# Show the file in Explorer, selected, ready to copy.
Start-Process explorer.exe "/select,`"$setup`""
