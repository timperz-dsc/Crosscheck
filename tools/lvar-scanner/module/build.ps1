# Builds the Crosscheck LVar scanner WASM module with the LLVM toolchain that ships with the
# MSFS 2024 SDK (no Visual Studio needed) and lays out an MSFS package next to this folder.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# Output: ..\package\crosscheck-lvar-scanner\  (copy that folder into the Community folder)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$sdk = $env:MSFS2024_SDK
if (-not $sdk) { throw 'MSFS2024_SDK is not set.' }
$wasm = Join-Path $sdk 'WASM'
$clang = Join-Path $wasm 'llvm\bin\clang-cl.exe'
$ld = Join-Path $wasm 'llvm\bin\wasm-ld.exe'
$sysroot = Join-Path $wasm 'wasi-sysroot'
$libdir = Join-Path $sysroot 'lib\wasm32-wasi'
$source = Join-Path $here 'lvar_scanner.cpp'

# Read-only guard: refuse to build if the source calls anything that writes, registers or creates a variable.
$forbidden = 'fsVarsLVarSet', 'fsVarsRegisterLVar', 'fsVarsNamedVarSet', 'fsVarsRegisterNamedVar', 'fsVarsAVarSet',
             'fsVarsZVarSet', 'fsVarsIVarSet', 'fsVarsOVarSet', 'set_named_variable', 'register_named_variable',
             'execute_calculator_code', 'trigger_key_event', 'fsEventsTrigger', 'SetDataOnSimObject', 'TransmitClientEvent'
$code = Get-Content $source -Raw
$hits = $forbidden | Where-Object { $code -match [regex]::Escape($_) }
if ($hits) { throw "Read-only guard: lvar_scanner.cpp mentions $($hits -join ', ')" }

$build = Join-Path $here 'build'
New-Item -ItemType Directory -Force $build | Out-Null
$obj = Join-Path $build 'lvar_scanner.o'
$out = Join-Path $build 'crosscheck-lvar-scanner.wasm'

& $clang --driver-mode=gcc --target=wasm32-unknown-wasi "--sysroot=$sysroot" `
    -c -O2 -std=c++17 -fms-extensions -fno-exceptions -fno-rtti -fvisibility=hidden -mthread-model single `
    -D__wasi__ -D_MSFS_WASM=1 -D_LIBCPP_HAS_NO_THREADS -D_WINDLL `
    -Wno-ignored-attributes -Wno-unused-command-line-argument `
    "-I$(Join-Path $wasm 'include')" "-I$(Join-Path $sdk 'SimConnect SDK\include')" `
    $source -o $obj
if ($LASTEXITCODE -ne 0) { throw 'compile failed' }

& $ld --no-entry --allow-undefined --export-dynamic --export-table --gc-sections -O2 `
    --export=__wasm_call_ctors --export=malloc --export=free --export=mallinfo `
    --export=mchunkit_begin --export=mchunkit_next --export=get_pages_state --export=mark_decommit_pages `
    --export=GetSimConnectVersion --export=GetExtensionVersion --export=GetExtensionVersionBuffer `
    $obj `
    --whole-archive (Join-Path $wasm 'WasmVersions\MSFS_WasmVersions.a') --no-whole-archive `
    "-L$libdir" -lc (Join-Path $libdir 'libclang_rt.builtins-wasm32.a') `
    -o $out
if ($LASTEXITCODE -ne 0) { throw 'link failed' }

# Package: manifest.json + layout.json + modules\*.wasm
$pkg = Join-Path $here '..\package\crosscheck-lvar-scanner'
$pkg = [System.IO.Path]::GetFullPath($pkg)
if (Test-Path $pkg) { Remove-Item -Recurse -Force $pkg }
New-Item -ItemType Directory -Force (Join-Path $pkg 'modules') | Out-Null
Copy-Item $out (Join-Path $pkg 'modules\crosscheck-lvar-scanner.wasm')

$manifest = [ordered]@{
    dependencies         = @()
    content_type         = 'MISC'
    title                = 'Crosscheck LVar Scanner (read-only)'
    manufacturer         = ''
    creator              = 'Crosscheck'
    package_version      = '0.1.0'
    minimum_game_version = '1.37.0'
    release_notes        = @{ neutral = @{ LastUpdate = ''; OlderHistory = '' } }
}
function Write-Json($value, $path) {
    # UTF-8 without BOM; Windows PowerShell's -Encoding utf8 would add one.
    [System.IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false))
}
Write-Json $manifest (Join-Path $pkg 'manifest.json')

$content = Get-ChildItem $pkg -Recurse -File |
    Where-Object { $_.Name -notin 'layout.json', 'manifest.json' } |
    ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($pkg.Length + 1).Replace('\', '/')
            size = $_.Length
            date = $_.LastWriteTimeUtc.ToFileTimeUtc()
        }
    }
Write-Json @{ content = @($content) } (Join-Path $pkg 'layout.json')

Write-Host "Built $out ($((Get-Item $out).Length) bytes)"
Write-Host "Package: $pkg"
