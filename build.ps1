<#
.SYNOPSIS
    Builds, publishes, and creates the WindowMover installer.
.DESCRIPTION
    1. Auto-increments the patch version number
    2. Publishes the app as a self-contained x64 binary
    3. Compiles the Inno Setup installer script
    Outputs: dist\WindowMover-Setup-<version>.exe
.PARAMETER Configuration
    Build configuration (default: Release)
.PARAMETER SkipPublish
    Skip the dotnet publish step (use existing publish\ output)
.PARAMETER NoBump
    Skip version auto-increment
.PARAMETER RetainOldVersions
    Keep previous installer EXEs in dist\ (by default they are removed)
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipPublish,
    [switch]$NoBump,
    [switch]$RetainOldVersions
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$PropsFile = "$Root\Directory.Build.props"

# --- Version management ---
function Get-Version {
    $xml = [xml](Get-Content $PropsFile)
    return $xml.Project.PropertyGroup.VersionPrefix
}

function Set-Version([string]$newVersion) {
    $content = Get-Content $PropsFile -Raw
    $content = $content -replace '<VersionPrefix>[^<]+</VersionPrefix>', "<VersionPrefix>$newVersion</VersionPrefix>"
    Set-Content $PropsFile -Value $content -NoNewline
}

$currentVersion = Get-Version
$parts = $currentVersion.Split('.')
$major = [int]$parts[0]
$minor = [int]$parts[1]
$patch = [int]$parts[2]

if (-not $NoBump) {
    $patch++
    $newVersion = "$major.$minor.$patch"
    Set-Version $newVersion
    Write-Host "=== Version: $currentVersion -> $newVersion ===" -ForegroundColor Magenta
}
else {
    $newVersion = $currentVersion
    Write-Host "=== Version: $newVersion (no bump) ===" -ForegroundColor Magenta
}

# --- Locate Inno Setup ---
$IsccPaths = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$Iscc = $IsccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $Iscc) {
    Write-Error "Inno Setup 6 not found. Install from https://jrsoftware.org/isdl.php"
    exit 1
}

# --- Step 1: Publish ---
if (-not $SkipPublish) {
    Write-Host "=== Publishing WindowMover ($Configuration, win-x64, self-contained) ===" -ForegroundColor Cyan
    dotnet restore "$Root\src\WindowMover" -r win-x64
    dotnet publish "$Root\src\WindowMover" -c $Configuration -r win-x64 --self-contained --no-restore -o "$Root\publish" /p:VersionPrefix=$newVersion
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed"
        exit 1
    }
}
else {
    Write-Host "=== Skipping publish (using existing output) ===" -ForegroundColor Yellow
}

# --- Step 2: Build installer ---
Write-Host "=== Building installer ===" -ForegroundColor Cyan
& $Iscc "/DMyAppVersion=$newVersion" "$Root\installer\WindowMover.iss"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed"
    exit 1
}

$Installer = Get-ChildItem "$Root\dist\WindowMover-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

# --- Clean up old installers ---
if (-not $RetainOldVersions) {
    $oldInstallers = Get-ChildItem "$Root\dist\WindowMover-Setup-*.exe" | Where-Object { $_.FullName -ne $Installer.FullName }
    if ($oldInstallers) {
        foreach ($old in $oldInstallers) {
            Remove-Item $old.FullName -Force
            Write-Host "  Removed old installer: $($old.Name)" -ForegroundColor DarkGray
        }
    }
}

$SizeMB = [math]::Round($Installer.Length / 1MB, 1)
Write-Host "=== Done! v$newVersion - $($Installer.FullName) ($SizeMB MB) ===" -ForegroundColor Green
