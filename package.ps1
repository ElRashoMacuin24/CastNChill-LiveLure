# Makes the zip that goes to players: a release build of the plugin, its symbols and the players' readme, laid out
# so it unzips straight into a game folder. Nothing from the game goes in it.
#
#   .\mod-src\package.ps1          ->  release\LiveLure-<version>.zip  (next to mod-src, in the game folder)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$version = [regex]::Match([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'LiveLure\Plugin.cs')), 'Version\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $version) { throw 'Could not read the version from LiveLure\Plugin.cs' }

& (Join-Path $PSScriptRoot 'build.ps1') LiveLure -Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$plugin = Join-Path $root 'BepInEx\plugins\LiveLure'
$files = @(
    @((Join-Path $plugin 'LiveLure.dll'),                 'BepInEx/plugins/LiveLure/LiveLure.dll'),
    @((Join-Path $plugin 'LiveLure.pdb'),                 'BepInEx/plugins/LiveLure/LiveLure.pdb'),
    @((Join-Path $PSScriptRoot 'player-readme.txt'),      'BepInEx/plugins/LiveLure/README.txt')
)

# A release must not say where it was built.
foreach ($f in $files[0..1]) {
    $bytes = [IO.File]::ReadAllBytes($f[0])
    foreach ($enc in [Text.Encoding]::UTF8, [Text.Encoding]::Unicode) {
        if ($enc.GetString($bytes) -match '[A-Za-z]:\\Users\\') { throw "$($f[0]) contains a local path; was it built with -Release?" }
    }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$outDir = Join-Path $root 'release'
New-Item -ItemType Directory -Force $outDir | Out-Null
$zipPath = Join-Path $outDir "LiveLure-$version.zip"
if (Test-Path -LiteralPath $zipPath) { [IO.File]::Delete($zipPath) }
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in $files) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f[0], $f[1], [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $zip.Dispose() }

$read = [IO.Compression.ZipFile]::OpenRead($zipPath)
try { $read.Entries | ForEach-Object { '{0,9:N0}  {1}' -f $_.Length, $_.FullName } }
finally { $read.Dispose() }
Write-Host "Wrote $zipPath"
