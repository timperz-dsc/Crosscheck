# Builds dist/crosscheck-<version>-win-x64.zip: the UI, the bridge (self-contained, no .NET
# install needed), the Fenix profile and defaults.json (default server). Nothing from tools/.
# Run from anywhere:  powershell -File bridge\package.ps1
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$csproj = Join-Path $PSScriptRoot 'Crosscheck.Bridge\Crosscheck.Bridge.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "crosscheck-$version-win-x64"
$dist = Join-Path $repo 'dist'
$stage = Join-Path $dist $name

Push-Location (Join-Path $repo 'ui')
try { npm run build; if ($LASTEXITCODE -ne 0) { throw 'UI build failed' } } finally { Pop-Location }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
dotnet publish (Join-Path $PSScriptRoot 'Crosscheck.Bridge') -c Release -r win-x64 --self-contained true -o $stage -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force

@"
Crosscheck $version - shared cockpit for MSFS 2024 (Fenix A320)

1. Unpack this zip into a folder of your own (for example C:\Crosscheck). Do not run it from inside the zip.
2. Start MSFS 2024 with the Fenix A320, then run crosscheck.exe.
3. Windows SmartScreen may warn because the program is not signed.
   Click "More info", then "Run anyway".
4. In the Crosscheck window: create a session and send the code to the other pilot,
   or join with the code you were given.

You need MSFS 2024 and the same Fenix version as the other pilot. Crosscheck compares
this before you join and tells you if it differs.

The server address is preset (defaults.json next to crosscheck.exe). You can change it
in the window under "Session server".

If something goes wrong, send the newest file from the logs folder to the developer:
  %LOCALAPPDATA%\Crosscheck\logs
(paste that line into the Windows Explorer address bar)
"@ | Set-Content (Join-Path $stage 'README.txt') -Encoding utf8

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
