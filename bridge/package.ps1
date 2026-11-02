# Local build of the installer: dist/releases/ gets the Velopack package and Crosscheck-win-Setup.exe.
# The GitHub release (.github/workflows/release.yml) does the same steps; use this to try a build on your PC.
# Needs once:  dotnet tool install -g vpk --version <the Velopack version in Crosscheck.Bridge.csproj>
# Run from anywhere:  powershell -File bridge\package.ps1
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$bridgeCsproj = Join-Path $PSScriptRoot 'Crosscheck.Bridge\Crosscheck.Bridge.csproj'
$coreCsproj = Join-Path $PSScriptRoot 'Crosscheck.Bridge.Core\Crosscheck.Bridge.Core.csproj'
$first = { param($path) ([xml](Get-Content $path)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$version = & $first $bridgeCsproj
if ($version -ne (& $first $coreCsproj)) { throw "Version differs: $bridgeCsproj says $version, $coreCsproj says $(& $first $coreCsproj). Both must match." }
$dist = Join-Path $repo 'dist'
$publish = Join-Path $dist 'publish'
$releases = Join-Path $dist 'releases'

Push-Location (Join-Path $repo 'ui')
try { npm run build; if ($LASTEXITCODE -ne 0) { throw 'UI build failed' } } finally { Pop-Location }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $PSScriptRoot 'Crosscheck.Bridge') -c Release -r win-x64 --self-contained true -o $publish -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item -Force

# Packages of older local builds stay in $releases, so Velopack can also make a delta package.
vpk pack --packId Crosscheck --packVersion $version --packTitle Crosscheck --packAuthors Crosscheck `
    --packDir $publish --mainExe crosscheck.exe --runtime win-x64 `
    --icon (Join-Path $repo 'assets\brand\crosscheck.ico') --shortcuts 'Desktop,StartMenuRoot' --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }
Write-Host "Built $releases (Crosscheck-win-Setup.exe is the installer)"
