# Copyright (c) SeasonEngine and contributors.
# Licensed under the MIT License.
# https://github.com/SeasonRealms/SeasonThreeD

<#
.SYNOPSIS
Adds the SeasonThreeD thin C ABI lane to a trellis.cpp checkout, so that build
produces trellis.dll next to trellis-cli.exe.

.DESCRIPTION
trellis.cpp ships trellis_core as a STATIC library and exposes no C header, so
the dll needs an entry point of our own (trellis_capi.cpp, next to this script).
That entry point has to be compiled by trellis.cpp's own CMake project - only
there does it inherit the GGML install dir, the CUDA runtime setting, the MSVC
runtime and CMAKE_RUNTIME_OUTPUT_DIRECTORY - and together with the sources
trellis_run()/parse_args() live in (src/trellis_cli.cpp, src/trellis_args.cpp),
which are part of the executables, not of trellis_core.

The upstream tree is therefore not edited by hand and the shim is not committed
to the fork: this script copies the shim into src/ and appends one target to
CMakeLists.txt. Keeping the shim here means the pinned trellis_ref stays a pure
trellis.cpp commit - the ABI contract and the code that implements it are
versioned together with the C# binding that consumes them - and following
upstream is a one-line ref bump instead of a fork merge.

The workflow step 'Add the C ABI lane to the trellis build' runs exactly this
script; a developer reproduces the packaged dll with:

    native/apply-capi-lane.ps1 -TrellisSource <path to a trellis.cpp checkout>

.PARAMETER TrellisSource
Path to a trellis.cpp checkout (the directory holding CMakeLists.txt).

.PARAMETER ShimSource
Override for trellis_capi.cpp; defaults to the copy next to this script.

.NOTES
Idempotent: running it twice refreshes the shim and appends nothing the second
time. It fails with a readable message when trellis.cpp's build layout changed,
rather than leaving an unresolved extern for the link step to discover.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TrellisSource,

    [string]$ShimSource
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not filled in yet while default parameter values are bound, so
# the shim path is resolved here instead of in the param block.
if (-not $ShimSource) {
    $ShimSource = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'trellis_capi.cpp'
}

$root = (Resolve-Path -LiteralPath $TrellisSource).Path
$cmakeLists = Join-Path $root 'CMakeLists.txt'
$shimTarget = Join-Path $root 'src/trellis_capi.cpp'

if (-not (Test-Path -LiteralPath $cmakeLists)) {
    throw "no CMakeLists.txt under $root - point -TrellisSource at a trellis.cpp checkout"
}
if (-not (Test-Path -LiteralPath $ShimSource)) {
    throw "the C ABI shim is missing: $ShimSource"
}
if (-not (Test-Path -LiteralPath (Split-Path -Parent $shimTarget))) {
    throw "$root does not look like trellis.cpp (no src/ directory)"
}

# The two upstream facts the appended target depends on. Both are asserted
# instead of assumed: if upstream renames the library or drops a file from the
# trellis-cli source list, the shim would still compile and then fail to link
# with an unresolved extern that says nothing about the real cause.
$coreTarget = Select-String -Path $cmakeLists -Pattern 'add_library\(trellis_core STATIC'
$cliTarget = Select-String -Path $cmakeLists -Pattern 'add_executable\(trellis-cli src/trellis_cli_main\.cpp src/trellis_cli\.cpp src/trellis_args\.cpp'
if (-not $coreTarget -or -not $cliTarget) {
    throw "trellis.cpp build layout changed: '$cmakeLists' no longer declares trellis_core / the trellis-cli source list. Re-check native/trellis_capi.cpp before shipping a dll without its entry point."
}

Copy-Item -LiteralPath $ShimSource -Destination $shimTarget -Force

if (Select-String -Path $cmakeLists -Pattern 'season_trellis' -Quiet) {
    Write-Host "season_trellis already declared in $cmakeLists; refreshed $shimTarget, appended nothing."
    return
}

# Single quotes on purpose: ${CMAKE_SOURCE_DIR} and ${GGML_RPATH} have to reach
# CMake verbatim.
$block = @(
    ''
    '# --- SeasonThreeD thin C ABI lane ----------------------------------------'
    '# Appended by SeasonThreeD/native/apply-capi-lane.ps1. trellis-cli.exe and'
    '# trellis-server.exe keep building unchanged; this target only adds a third'
    '# entry point into the same objects, exported as trellis.dll.'
    'add_library(season_trellis SHARED src/trellis_capi.cpp src/trellis_cli.cpp src/trellis_args.cpp)'
    'target_link_libraries(season_trellis PRIVATE trellis_core)'
    'target_include_directories(season_trellis PRIVATE ${CMAKE_SOURCE_DIR}/include ${CMAKE_SOURCE_DIR}/src)'
    'set_target_properties(season_trellis PROPERTIES OUTPUT_NAME trellis BUILD_RPATH "${GGML_RPATH}")'
    ''
)

Add-Content -Path $cmakeLists -Value $block -Encoding utf8

Write-Host "Appended the C ABI target to $cmakeLists :"
Get-Content $cmakeLists -Tail $block.Count | ForEach-Object { Write-Host "  $_" }
