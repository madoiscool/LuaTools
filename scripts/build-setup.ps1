<#
.SYNOPSIS
    Builds and packages LuaTools into an installer/setup executable and portable package using Velopack.

.PARAMETER Configuration
    The build configuration (default: "Release").

.PARAMETER Version
    The release version (default: parsed from LuaToolsGui.csproj).

.PARAMETER OutputDir
    The directory where release artifacts (Setup.exe, portable zip, nupkg, etc.) will be placed (default: "releases").

.PARAMETER PackId
    The Velopack package ID (default: "LuaTools").

.PARAMETER Runtime
    The target runtime framework to bundle/check (default: "net8-x64-desktop").
#>

param(
    [string]$Configuration = "Release",
    [string]$Version = "",
    [string]$OutputDir = "releases",
    [string]$PackId = "LuaTools",
    [string]$PackAuthors = "LuaTools",
    [string]$PackTitle = "LuaTools",
    [string]$Runtime = "net8-x64-desktop"
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
Set-Location $RootDir

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " LuaTools - Build & Package Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. Resolve Project and Version
$CsprojPath = Join-Path $RootDir "src\LuaToolsGui\LuaToolsGui.csproj"
if (-not (Test-Path $CsprojPath)) {
    Write-Error "Could not find project file at $CsprojPath"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $CsprojContent = Get-Content $CsprojPath -Raw
    if ($CsprojContent -match "<Version>([^<]+)</Version>") {
        $Version = $Matches[1].Trim()
        Write-Host "Detected version from csproj: $Version" -ForegroundColor Green
    } else {
        $Version = "1.0.0"
        Write-Warning "Could not detect version in csproj, defaulting to $Version"
    }
} else {
    Write-Host "Using specified version: $Version" -ForegroundColor Green
}

# 2. Ensure Output Directory
$FullOutputDir = Join-Path $RootDir $OutputDir
if (-not (Test-Path $FullOutputDir)) {
    New-Item -ItemType Directory -Path $FullOutputDir -Force | Out-Null
}

# 3. Publish Project (Framework-dependent Win-x64)
$PublishDir = Join-Path $RootDir "publish"
Write-Host "`nPublishing project ($Configuration)..." -ForegroundColor Yellow

dotnet publish $CsprojPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
}
Write-Host "Publish succeeded: $PublishDir" -ForegroundColor Green

# 4. Locate or Install Velopack CLI (vpk)
$VpkExe = $null
$ToolsDir = Join-Path $RootDir "tools"
$LocalVpk = Join-Path $ToolsDir "vpk.exe"

if (Test-Path $LocalVpk) {
    $VpkExe = $LocalVpk
} elseif (Get-Command vpk -ErrorAction SilentlyContinue) {
    $VpkExe = "vpk"
} else {
    Write-Host "`nVelopack CLI (vpk) not found in PATH. Checking/installing to ./tools..." -ForegroundColor Yellow
    if (-not (Test-Path $ToolsDir)) {
        New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
    }
    dotnet tool install --tool-path $ToolsDir vpk
    if (Test-Path $LocalVpk) {
        $VpkExe = $LocalVpk
    } else {
        Write-Error "Failed to locate or install vpk tool."
    }
}

Write-Host "Using Velopack CLI: $VpkExe" -ForegroundColor Green

# 5. Pack with Velopack
$IconPath = Join-Path $RootDir "src\LuaToolsGui\icon.ico"
$MainExe = "LuaTools.exe"

Write-Host "`nPackaging with Velopack..." -ForegroundColor Yellow

$VpkArgs = @(
    "pack",
    "--packId", $PackId,
    "--packVersion", $Version,
    "--packDir", $PublishDir,
    "--packAuthors", $PackAuthors,
    "--packTitle", $PackTitle,
    "--mainExe", $MainExe,
    "--outputDir", $FullOutputDir
)

if (Test-Path $IconPath) {
    $VpkArgs += @("--icon", $IconPath)
}

if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
    $VpkArgs += @("--framework", $Runtime)
}

Write-Host "Running: $VpkExe $($VpkArgs -join ' ')" -ForegroundColor Gray
& $VpkExe @VpkArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "Velopack packaging failed with exit code $LASTEXITCODE"
}

Write-Host "`n========================================" -ForegroundColor Green
Write-Host " Setup creation complete!" -ForegroundColor Green
Write-Host " Artifacts created in: $FullOutputDir" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green

Get-ChildItem $FullOutputDir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
