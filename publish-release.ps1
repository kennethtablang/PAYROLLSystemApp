<#
.SYNOPSIS
    Publishes the built installer as a GitHub Release, so every installed copy
    offers it under Help & Updates on the next sign-in.

.DESCRIPTION
    Run after build-installer.ps1 -Version x.y, after testing the installer,
    and after committing and pushing. It:
      1. Reads the version from installer\PayrollSystem.iss.
      2. Checks the installer and its .sha256 exist and still match.
      3. Takes the release notes from the "## x.y" section of CHANGELOG.md.
      4. Checks the code is committed and pushed, so the release points at
         exactly the code it was built from.
      5. Creates the release v<x.y> with the installer and checksum attached.

    One-time setup on this PC:  gh auth login

.PARAMETER Draft
    Publish as a draft: visible to you on GitHub, invisible to the installed
    apps. Publish it from the GitHub page when ready.

.EXAMPLE
    .\publish-release.ps1
    .\publish-release.ps1 -Draft
#>
param(
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
$issFile   = Join-Path $root 'installer\PayrollSystem.iss'
$changelog = Join-Path $root 'CHANGELOG.md'

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Fail([string]$text) { Write-Host "`nFAILED: $text" -ForegroundColor Red; exit 1 }

# Runs a git or gh command quietly and returns its exit code. Windows
# PowerShell 5.1 turns a native command's error output into a terminating
# error under ErrorActionPreference=Stop, so it is relaxed for the call.
function Quiet([scriptblock]$command) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $command *> $null; return $LASTEXITCODE }
    finally { $ErrorActionPreference = $saved }
}

# ---------------------------------------------------------------- version

$version = [regex]::Match([IO.File]::ReadAllText($issFile), '#define AppVersion "([^"]*)"').Groups[1].Value
if (-not $version) { Fail "No AppVersion in $issFile" }

$tag   = "v$version"
$setup = Join-Path $root "PAYROLLSystemApp\Installer\PayrollSystemSetup-$version.exe"
$sum   = "$setup.sha256"
$sig   = "$sum.sig"

Step "Publishing version $version as $tag"

# ---------------------------------------------------------------- files

if (-not (Test-Path $setup)) { Fail "$setup is missing. Run build-installer.ps1 first." }
if (-not (Test-Path $sum))   { Fail "$sum is missing. Run build-installer.ps1 again (it writes the checksum)." }
if (-not (Test-Path $sig))   { Fail "$sig is missing. The installed apps refuse unsigned releases. Run build-installer.ps1 again." }

$expected = ([IO.File]::ReadAllText($sum).Trim() -split '\s+')[0]
$actual   = (Get-FileHash $setup -Algorithm SHA256).Hash
if ($expected -ne $actual) { Fail 'The installer changed after its checksum was written. Run build-installer.ps1 again.' }

# ---------------------------------------------------------------- notes

if (-not (Test-Path $changelog)) { Fail 'CHANGELOG.md is missing.' }

# Everything under "## <version>" up to the next "## " heading.
$lines = [IO.File]::ReadAllLines($changelog)
$notes = New-Object System.Collections.Generic.List[string]
$inside = $false

foreach ($line in $lines) {
    if ($line -match '^##\s+(\S+)') {
        if ($inside) { break }
        $inside = ($Matches[1] -eq $version)
        continue
    }
    if ($inside) { $notes.Add($line) }
}

$notesText = ($notes -join "`n").Trim()
if (-not $notesText) {
    Fail "CHANGELOG.md has no '## $version' section. Write what changed (and the REQ- references it answers) first."
}

# ---------------------------------------------------------------- git

Step 'Checking the code is committed and pushed'

Push-Location $root
try {
    $dirty = git status --porcelain
    if ($dirty) { Fail "There are uncommitted changes. Commit and push them first:`n$($dirty -join "`n")" }

    if ((Quiet { git fetch --quiet }) -ne 0) { Fail 'Could not reach GitHub (git fetch failed).' }

    $head = (git rev-parse HEAD).Trim()
    if ((Quiet { git merge-base --is-ancestor HEAD '@{u}' }) -ne 0) { Fail 'This commit is not pushed. Run: git push' }
}
finally {
    Pop-Location
}

# ---------------------------------------------------------------- github

Step 'Creating the GitHub release'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail 'GitHub CLI (gh) is not installed. Install it with: winget install --id GitHub.cli -e'
}

if ((Quiet { gh auth status }) -ne 0) { Fail 'GitHub CLI is not signed in. Run: gh auth login' }

if ((Quiet { gh release view $tag }) -eq 0) { Fail "Release $tag already exists. Bump the version: .\build-installer.ps1 -Version x.y" }

$notesFile = Join-Path ([IO.Path]::GetTempPath()) "payroll-release-$version.md"
[IO.File]::WriteAllText($notesFile, $notesText, (New-Object Text.UTF8Encoding($false)))

$arguments = @('release', 'create', $tag, $setup, $sum, $sig,
    '--title', "Version $version",
    '--notes-file', $notesFile,
    '--target', $head)
if ($Draft) { $arguments += '--draft' }

& gh @arguments
if ($LASTEXITCODE -ne 0) { Fail 'gh could not create the release. Read the error above.' }

Remove-Item $notesFile -ErrorAction SilentlyContinue

if ($Draft) {
    Write-Host "`nDONE  Draft $tag created. Installed apps will not see it until you publish it on GitHub." -ForegroundColor Green
} else {
    Write-Host "`nDONE  $tag is live. Installed apps will offer it the next time someone signs in." -ForegroundColor Green
}
