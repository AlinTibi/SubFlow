param([string]$ArtifactDirectory = (Join-Path $PSScriptRoot '..\artifacts'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version = $props.Project.PropertyGroup.Version
$zip = Join-Path $ArtifactDirectory "ALMARFELD-SubFlow-$version-win-x64.zip"
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $required = @('SubFlow.exe','README.md','LICENSE','SECURITY.md','CONTRIBUTING.md','CODE_OF_CONDUCT.md','CHANGELOG.md','THIRD-PARTY-NOTICES.md','docs/USER_GUIDE.md','docs/TROUBLESHOOTING.md','docs/images/logo.png','tools/README.txt')
    $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
    foreach ($name in $required) { if ($name -notin $names) { throw "Missing package file: $name" } }
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\','/')
        if ($name.EndsWith('/')) { continue }
        if ($name -match '(^|/)(bin|obj|node_modules|tests|fixtures|logs|work|roaming|local)/|\.\.|\.(mkv|mp4|avi|mov|webm|m4v|srt|ass|ssa|vtt|sup|db|sqlite|pdb|pfx|key)$|(^|/)(settings|glossary)\.json$') { throw "Disallowed distribution data: $name" }
        if ($name -notin $required -and $name -notmatch '^docs/(images/logo\.png|screenshots/[a-z-]+\.png)$') { throw "Unexpected distribution file: $name" }
        if ($name -match '\.(md|txt)$') {
            $reader = [System.IO.StreamReader]::new($entry.Open(), [System.Text.UTF8Encoding]::new($false,$true))
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($text -match '[\w.+-]+@(?:gmail|hotmail|outlook|yahoo)\.com|[A-Z]:\\Users\\|[A-Z]:\\Codex_personal|github\.com/OWNER/REPO') { throw "Private or placeholder content: $name" }
        }
    }
} finally { $archive.Dispose() }
$sums = Join-Path $ArtifactDirectory "ALMARFELD-SubFlow-$version-SHA256SUMS.txt"
foreach ($line in Get-Content $sums) {
    if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9.-]+)$') { throw 'Invalid checksum line.' }
    $expected = $Matches[1]; $filename = $Matches[2]
    if ((Get-FileHash (Join-Path $ArtifactDirectory $filename) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "Checksum mismatch: $filename" }
}
Write-Host "Package validation PASS: $version; no videos, subtitle samples, profiles, cache or history."
