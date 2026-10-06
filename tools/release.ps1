# Builds the plugin in Release and packs it the way Torch takes a plugin: a zip with the dll, its manifest and what it
# needs beside it at the root. Used by the release workflow (.github/workflows/release.yml) and by hand:
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version v1.2.3
# -SeRoot is the folder with Torch and DedicatedServer64 (the game's libraries the plugin is built against).
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$SeRoot = 'C:\SE',
    [string]$Plugin = 'SentisOptimisations',
    [string]$Out = 'dist'
)
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^v(\d+\.\d+\.\d+)([-.][0-9A-Za-z.-]+)?$') { throw "The version is to look like v1.2.3 (got '$Version')" }
$number = $Matches[1]
$repo = Split-Path -Parent $PSScriptRoot
foreach ($needed in "$SeRoot\Torch.dll", "$SeRoot\DedicatedServer64\Sandbox.Game.dll") {
    if (-not (Test-Path $needed)) { throw "Not found: $needed (SeRoot is '$SeRoot')" }
}

$bin = Join-Path $repo "$Plugin\bin\Release"
if (Test-Path $bin) { Remove-Item $bin -Recurse -Force }
dotnet build (Join-Path $repo "$Plugin\$Plugin.csproj") -c Release "-p:SeRoot=$SeRoot" "-p:Version=$number" "-p:FileVersion=$number.0" "-p:InformationalVersion=$Version" --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "The build failed ($LASTEXITCODE)" }

$stage = Join-Path $repo "$Out\$Plugin"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
# the plugin, the libraries it brings with it, the licence
Get-ChildItem $bin -File | Where-Object { $_.Extension -in '.dll', '.pdb' -or $_.Name -in 'LICENSE', 'NOTICE' } | Copy-Item -Destination $stage
# Harmony, which the plugin patches the game with: built against (lib\0Harmony.dll), not copied to the output, and
# needed beside the plugin on the server
Copy-Item (Join-Path $repo "$Plugin\lib\0Harmony.dll") -Destination $stage
# the manifest with the version of this release (Torch shows it in its list of plugins)
$manifest = Get-Content (Join-Path $bin 'manifest.xml') -Raw
if ($manifest -notmatch '<Version>[^<]*</Version>') { throw 'manifest.xml has no <Version>' }
$manifest = $manifest -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
[IO.File]::WriteAllText((Join-Path $stage 'manifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))
if (-not (Test-Path (Join-Path $stage "$Plugin.dll"))) { throw "$Plugin.dll is not in the build" }

$zip = Join-Path $repo "$Out\$Plugin-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "Packed: $zip"
Get-ChildItem $stage | ForEach-Object { Write-Host ("  {0,10}  {1}" -f $_.Length, $_.Name) }
if ($env:GITHUB_OUTPUT) { "zip=$zip" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
