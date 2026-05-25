<#
.SYNOPSIS
  Turbo fork: shrink a Mission Planner (Turbo) Release build output without
  breaking it on Windows OR Wine.

.DESCRIPTION
  Run AFTER a Release build, pointing at the output dir (default
  bin\Release\net472). Removes only content proven safe for our target:
  a 32-bit (Prefer32Bit AnyCPU) process that must run well on both 64-bit
  Windows (via WOW64) and Wine.

  Removals (all verified non-breaking):
    1. plugins\ duplicate tree  -- exact upstream build.bat/installer.bat
       dedup: delete any file under plugins\ that also exists at the same
       relative path in the root. Keeps the genuinely-unique plugin files.
    2. *.pdb                    -- debug symbols, never loaded at runtime.
    3. Foreign-arch native libs -- keep only -KeepArch (default x64). The exe
       is pure AnyCPU (CLR header ILONLY, no 32BIT flag) so it runs 64-bit on
       64-bit Windows AND the 64-bit Wine prefix, loading x64\libSkiaSharp.dll
       (verified via Wine +loaddll trace). SkiaSharp is the rendering backend
       for HUD, the GMap map control, and the MissionPlanner.Drawing layer, so
       dropping the live-arch native breaks every SKControl (e.g. the Quick
       tab) -- keep it. Drops arm\, arm64\, the other-bitness dir, every *.so
       (Linux ELF, useless under Wine), and libSkiaSharp.dylib (macOS).
    4. gdal\                    -- entire GDAL tree (x64+x86+data+share).
       SAFE: Program.cs only inits GDAL when Directory.Exists("gdal") and
       wraps it in Task.Run + try/catch, so absence is a clean no-op. SRTM
       elevation (srtm.cs, *.hgt) has ZERO GDAL dependency and keeps working.
       Loses only GeoTIFF/DTED elevation fallback + GDAL imagery import.
    5. lib\                     -- IronPython CPython-3.4 stdlib (.py). Keeps
       the IronPython.dll engine; only .py scripts that import stdlib modules
       are affected. All plugins/scripts default OFF, so no startup impact.

  KEEPS: MissionPlanner.exe + core DLLs, app.config, real plugins, the
  -KeepArch native dir, Drivers\, all locale satellite assemblies,
  airports.csv, NoFly\, ParameterMetaData XML, Scripts\.

.PARAMETER OutDir
  Build output directory. Default: bin\Release\net472 relative to this script.

.PARAMETER KeepArch
  Native arch dir to keep. Default x64 (the exe is AnyCPU and runs 64-bit on
  64-bit Windows + 64-bit Wine). Pass x86 only if the build is ever forced to
  a 32-bit process (Prefer32Bit) so the right libSkiaSharp.dll survives.

.PARAMETER WhatIf
  Report what would be deleted + projected savings, delete nothing.
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [ValidateSet('x86', 'x64')]
    [string]$KeepArch = 'x64',
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

if (-not $OutDir) {
    $OutDir = Join-Path $PSScriptRoot 'bin\Release\net472'
}
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

$exe = Join-Path $OutDir 'MissionPlanner.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "MissionPlanner.exe not found in '$OutDir' -- refusing to operate on a non-build dir."
}

function Get-DirMB {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    [math]::Round((Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum / 1MB, 1)
}

$before = Get-DirMB $OutDir
Write-Host "=== debloat: $OutDir ($before MB) | KeepArch=$KeepArch | WhatIf=$WhatIf ==="

# Tally of what we remove, for the report.
$removed = 0.0
function Remove-Target {
    param([string[]]$Paths, [string]$Label)
    $sz = 0.0
    $hits = @()
    foreach ($p in $Paths) {
        if (Test-Path -LiteralPath $p) {
            $hits += $p
            if ((Get-Item -LiteralPath $p) -is [System.IO.DirectoryInfo]) {
                $sz += Get-DirMB $p
            }
            else {
                $sz += [math]::Round((Get-Item -LiteralPath $p).Length / 1MB, 2)
            }
        }
    }
    if ($hits.Count -gt 0) {
        Write-Host ("  - {0,-28} {1,7} MB  ({2} item(s))" -f $Label, $sz, $hits.Count)
        if (-not $WhatIf) {
            foreach ($h in $hits) { Remove-Item -LiteralPath $h -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
    $script:removed += $sz
}

# 1. plugins\ dedup -- exact replication of upstream build.bat one-liner:
#    delete any file under plugins\ that also exists at the same relative
#    path one level up (the root). Leaves only the unique plugin payloads.
$pluginsDir = Join-Path $OutDir 'plugins'
if (Test-Path -LiteralPath $pluginsDir) {
    $dupBytes = 0L
    $dupCount = 0
    $dupes = @()
    Get-ChildItem -LiteralPath $pluginsDir -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
        $rootTwin = $_.FullName -replace '\\plugins\\', '\'
        if (Test-Path -LiteralPath $rootTwin -PathType Leaf) {
            $dupes += $_.FullName
            $dupBytes += $_.Length
            $dupCount++
        }
    }
    $dupMB = [math]::Round($dupBytes / 1MB, 1)
    Write-Host ("  - {0,-28} {1,7} MB  ({2} dup file(s))" -f 'plugins\ dedup', $dupMB, $dupCount)
    if (-not $WhatIf) {
        foreach ($d in $dupes) { Remove-Item -LiteralPath $d -Force -ErrorAction SilentlyContinue }
        # prune now-empty subdirs left behind by the dedup
        Get-ChildItem -LiteralPath $pluginsDir -Recurse -Directory -ErrorAction SilentlyContinue |
            Sort-Object { $_.FullName.Length } -Descending |
            Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Recurse -File -ErrorAction SilentlyContinue) } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }
    $script:removed += $dupMB
}

# 2. debug symbols
$pdbs = Get-ChildItem -LiteralPath $OutDir -Recurse -File -Filter *.pdb -ErrorAction SilentlyContinue
if ($pdbs) {
    $pdbMB = [math]::Round(($pdbs | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host ("  - {0,-28} {1,7} MB  ({2} file(s))" -f '*.pdb', $pdbMB, $pdbs.Count)
    if (-not $WhatIf) { $pdbs | Remove-Item -Force -ErrorAction SilentlyContinue }
    $script:removed += $pdbMB
}

# 3. foreign-arch native libs (keep only $KeepArch)
$archDirs = @('x86', 'x64', 'arm', 'arm64') | Where-Object { $_ -ne $KeepArch }
Remove-Target -Label "native arch dirs (drop)" -Paths ($archDirs | ForEach-Object { Join-Path $OutDir $_ })
# stray non-Windows natives at root
$soFiles = Get-ChildItem -LiteralPath $OutDir -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.so', '.dylib' -and $_.FullName -notmatch '\\plugins\\' }
if ($soFiles) {
    $soMB = [math]::Round(($soFiles | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host ("  - {0,-28} {1,7} MB  ({2} file(s))" -f '*.so/*.dylib (non-Win)', $soMB, $soFiles.Count)
    if (-not $WhatIf) { $soFiles | Remove-Item -Force -ErrorAction SilentlyContinue }
    $script:removed += $soMB
}

# 4. GDAL (whole tree) -- safe per Program.cs Directory.Exists guard; SRTM unaffected
Remove-Target -Label "gdal\ (GeoTIFF/GIS)" -Paths (Join-Path $OutDir 'gdal')

# 5. IronPython stdlib (keep IronPython.dll engine)
Remove-Target -Label "lib\ (IronPython stdlib)" -Paths (Join-Path $OutDir 'lib')

$after = if ($WhatIf) { $before - $script:removed } else { Get-DirMB $OutDir }
Write-Host ("=== {0}: {1} MB -> {2} MB  (saved ~{3} MB) ===" -f `
    ($(if ($WhatIf) { 'WOULD shrink' } else { 'shrunk' })), $before, $after, [math]::Round($before - $after, 1))
