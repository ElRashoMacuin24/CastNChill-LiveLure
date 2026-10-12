# Builds a BepInEx IL2CPP plugin from mod-src\<Name>\*.cs straight with Roslyn's csc
# (no .NET SDK needed) and drops it in BepInEx\plugins\<Name>\.
#
#   .\mod-src\build.ps1 LiveLure
#   .\mod-src\build.ps1 LiveLure -Release    # a build to give to other people: see below
param(
    [Parameter(Mandatory, Position = 0)][string]$Name,
    [string[]]$ExtraCoreRefs = @(),
    # Same code, but nothing in the DLL or its symbols records where on this machine it was built
    # (an ordinary build embeds full paths such as C:\Users\<you>\...), and the same source always gives the same file.
    [switch]$Release
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $PSScriptRoot $Name
if (-not (Test-Path $src)) { throw "No source folder: $src" }

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -property installationPath
$csc = Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found under $vs" }

$interop = Join-Path $root 'BepInEx\interop'
if (-not (Test-Path (Join-Path $interop 'Game.Runtime.dll'))) {
    throw "BepInEx\interop is empty - run the game once so BepInEx generates the interop assemblies."
}

# The .NET 6 runtime BepInEx ships doubles as our reference set; skip its native DLLs.
$refs = @()
foreach ($f in Get-ChildItem (Join-Path $root 'dotnet') -Filter *.dll) {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($f.FullName); $refs += $f.FullName } catch { }
}
$core = @('BepInEx.Core', 'BepInEx.Unity.IL2CPP', 'BepInEx.Unity.Common', '0Harmony',
          'Il2CppInterop.Runtime', 'Il2CppInterop.Common') + $ExtraCoreRefs
foreach ($c in $core) { $refs += Join-Path $root "BepInEx\core\$c.dll" }
$refs += (Get-ChildItem $interop -Filter *.dll).FullName

$outDir = Join-Path $root "BepInEx\plugins\$Name"
New-Item -ItemType Directory -Force $outDir | Out-Null
$out = Join-Path $outDir "$Name.dll"

$rsp = Join-Path $env:TEMP "cnc-build-$Name.rsp"
$lines = @('/nologo', '/noconfig', '/nostdlib+', '/target:library', '/langversion:latest', '/unsafe+',
           '/optimize+', '/debug:portable', '/nowarn:CS1701,CS1702,CS8632', "/out:`"$out`"")
if ($Release) { $lines += '/deterministic+', "`"/pathmap:$root=.`"" }
$lines += $refs | ForEach-Object { "/r:`"$_`"" }
$lines += (Get-ChildItem $src -Filter *.cs -Recurse).FullName | ForEach-Object { "`"$_`"" }
Set-Content -Path $rsp -Value $lines -Encoding utf8

$log = & $csc "@$rsp" 2>&1 | ForEach-Object { "$_".Replace($root + '\', '') }
$log | Where-Object { $_ -match '\b(error|warning) CS' } | Select-Object -First 40 | Write-Host
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED"; exit 1 }
Write-Host "Built $out"
