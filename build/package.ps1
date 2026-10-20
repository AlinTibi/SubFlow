# Builds a release of ALMARFELD SubFlow:
#   - a self-contained, single-file SubFlow.exe (no .NET installation needed)
#   - a portable ZIP
#   - a Windows installer (when Inno Setup 6 is installed)
#   - SHA256SUMS.txt
#
#   powershell -ExecutionPolicy Bypass -File build\package.ps1 [-SkipTests] [-Version 1.2.0] [-RequireInstaller]
param(
    [switch]$SkipTests,
    [string]$Version,
    [switch]$RequireInstaller
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }

$name = "ALMARFELD-SubFlow-$Version-win-x64"
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish\SubFlow'
$stage = Join-Path $artifacts $name
$zip = Join-Path $artifacts "$name.zip"
foreach ($target in @($publish, $stage, $zip)) {
    if (-not [System.IO.Path]::GetFullPath($target).StartsWith([System.IO.Path]::GetFullPath($artifacts) + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Packaging path escaped artifacts directory.' }
}
$versionProps = @("-p:Version=$Version", "-p:AssemblyVersion=$Version.0", "-p:FileVersion=$Version.0", "-p:InformationalVersion=$Version")

if (-not $SkipTests) {
    dotnet test ALMARFELD.SubFlow.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish src/ALMARFELD.SubFlow.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none @versionProps -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# Staging folder = contents of the ZIP and of the installer.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage, (Join-Path $stage 'docs'), (Join-Path $stage 'tools') | Out-Null
Copy-Item (Join-Path $publish 'SubFlow.exe') $stage
Copy-Item README.md, LICENSE, THIRD-PARTY-NOTICES.md, CHANGELOG.md, SECURITY.md, CONTRIBUTING.md, CODE_OF_CONDUCT.md $stage
Copy-Item docs\images (Join-Path $stage 'docs') -Recurse
Copy-Item docs\USER_GUIDE.md, docs\TROUBLESHOOTING.md (Join-Path $stage 'docs')
Copy-Item docs\screenshots (Join-Path $stage 'docs') -Recurse
Set-Content -Path (Join-Path $stage 'tools\README.txt') -Encoding UTF8 -Value @"
Optional: place ffmpeg.exe and ffprobe.exe (and mkvmerge.exe) here to use them without installing.
They are not included with SubFlow. Get FFmpeg from https://ffmpeg.org/download.html
or install it with:  winget install Gyan.FFmpeg
"@

$outputs = @()
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$outputs += $zip

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$stage" "/O$artifacts" (Join-Path $root 'installer\SubFlow.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    $outputs += Join-Path $artifacts "ALMARFELD-SubFlow-$Version-setup-win-x64.exe"
}
elseif ($RequireInstaller) { throw 'Inno Setup 6 (ISCC.exe) was not found.' }
else { Write-Warning 'Inno Setup 6 not found: installer skipped (winget install JRSoftware.InnoSetup).' }

$sums = Join-Path $artifacts "ALMARFELD-SubFlow-$Version-SHA256SUMS.txt"
$outputs | ForEach-Object { "$((Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $_ -Leaf)" } |
    Set-Content -Path $sums -Encoding ASCII
Remove-Item "$zip.sha256" -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "Release $Version"
$outputs + $sums | ForEach-Object { Write-Host "  $_" }
