# Turbo patches vs upstream master

Tracker for the downstream patches maintained against
[`ArduPilot/MissionPlanner`](https://github.com/ArduPilot/MissionPlanner).
Update after every upstream sync. The **Conflict risk** column flags which
patches are most likely to clash on a future rebase, so the next sync session
can scan them quickly.

Branch layout:

- `upstream/master` — read-only mirror of ArduPilot/MissionPlanner.
- `master` — the Turbo branch; all patches live here. Default branch on the fork.

Last sync: rebased onto upstream `a2fcd74d6` (2026-08, auto-sync); all patches
re-applied clean (`git range-diff` all `=`), reviewed for logical conflicts.

| Order | Subject                                                            | Conflict risk | Files touched (key)                                                                                                                                          | Why it can clash                                                                                                                                                                     |
| ----- | ------------------------------------------------------------------ | ------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| 1     | cfg: silence default logging + drop AI/System.Net trace            | **LOW**       | `app.config`                                                                                                                                                 | Single XML file. Easy 3-way merge.                                                                                                                                                   |
| 2     | feat: kill outbound telemetry + disable auto-updater               | **MED**       | `ExtLibs/Utilities/Tracking.cs`, `MainV2.cs`, `MissionPlanner.sln`, `app.config`                                                                             | `.sln` and `MainV2.cs` change often upstream. `.sln` is the riskiest — line-positional GUID block deletion.                                                                          |
| 3     | perf: coalesce HUD invalidates + slow SerialReader loop            | **MED**       | `ExtLibs/Controls/HUD.cs`, `MainV2.cs`                                                                                                                       | HUD.cs setter pattern is uniform; upstream property additions are usually appended after our timer init.                                                                             |
| 4     | perf+chore: cut log/Console spam + Settings.Save race + debounce   | **MED**       | `Program.cs`, `ExtLibs/Utilities/Settings.cs`, `GCSViews/ConfigurationView/ConfigRawParams.cs`, `ExtLibs/Comms/CommsSerialPort.cs`, `GCSViews/FlightData.cs` | Five files, all in churned areas. Settings.Save() wrap may snag if upstream refactors it.                                                                                            |
| 5     | perf: load plugin DLLs + self-reflect on background thread         | **MED**       | `Plugin/PluginLoader.cs`                                                                                                                                     | Replaces a sync block with a Task.Run continuation — moderate structural delta.                                                                                                      |
| 6     | fix: skip WMI Win32_SerialPort query under Wine                    | **LOW**       | `Program.cs`                                                                                                                                                 | New `IsRunningOnWine` helper + 2-line guard. Additive.                                                                                                                               |
| 7     | ci: tag-triggered Release-only workflow + scheduled upstream sync  | **LOW**       | `.github/workflows/main.yml`, `.github/workflows/sync-upstream.yml`, deletes `appveyor.yml`/`azure-pipelines.yml`/`android.yml`/`mac.yml`                    | Workflow YAML lives under `.github/workflows/`. Upstream rarely touches the appveyor/azure files. If upstream adds a new mobile workflow we may need to delete it again post-rebase. |
| 8     | feat(11a): rebrand user-facing labels to 'Mission Planner (Turbo)' | **LOW**       | `Properties/AssemblyInfo.cs`, `Splash.Designer.cs`, `Program.cs`, `MainV2.resx`, `Properties/Resources.resx`, `wix/Program.cs`                               | String-only edits. `MainV2.resx`/`Program.cs` churn upstream but the hits are isolated literals; re-apply by hand if they move.                                                      |
| 9     | build(11b): rename output dir net461 → net472                      | **LOW**       | `MissionPlanner.csproj`, plugin/ExtLibs `.csproj` OutputPath, `build.bat`, `Msi/installer.bat`, `.github/workflows/main.yml`                                 | Pure path swaps in `<OutputPath>`. Upstream still uses `net461` dir name — expect this to reconflict every sync; re-run the sed.                                                     |
| 10    | build(11d): debloat.ps1 + DebugType=none (Release)                 | **LOW**       | `debloat.ps1` (new), `MissionPlanner.csproj` Release PropertyGroup                                                                                           | `debloat.ps1` is fork-only (no upstream file). Only clash surface is the Release `<PropertyGroup>` DebugType line in the csproj.                                                     |
| 10.1  | fix(11d): debloat KeepArch x86 → x64 (SKControl render broke)      | **LOW**       | `debloat.ps1`                                                                                                                                                | Fork-only. exe is AnyCPU→64-bit so the live SkiaSharp native is `x64\libSkiaSharp.dll`; keeping x86 broke HUD/Map/Quick-tab rendering.                                               |
| 11    | perf(11e): background pre-warm SkiaSharp (first-paint stutter)     | **LOW**       | `Program.cs`                                                                                                                                                 | Additive `Task.Run` block beside the GDAL/proxy bg probes. Warms native+font+shaping so first HUD/Quick-tab paint is instant. Clashes only if upstream restructures Program startup. |
| 12    | fix: guard Do-Action custom-action lookup (upstream #3744)         | **LOW**       | `GCSViews/FlightData.cs`                                                                                                                                     | Upstream #3744 indexes the empty `CustomActions` dictionary → `KeyNotFoundException` on every stock Do-Action click. `TryGetValue` guard. **DROP this commit when upstream ships its own fix** — it will conflict right at the guard.  |
| 13    | fix: speech opt-in works without an app restart                    | **LOW**       | `GCSViews/ConfigurationView/ConfigPlanner.cs`                                                                                                                | Repairs row 11-era Phase 10p deferral: `speechEnable`'s setter is a no-op while `speechEngine` is null, so the checkbox did nothing until relaunch. Builds + attaches the engine on the enable transition.                            |
| 14    | fix: keep the param metadata index in step with the document       | **LOW**       | `ExtLibs/Utilities/ParameterMetaDataRepositoryAPMpdef.cs`                                                                                                    | Repairs the Phase 9 `_paramIndex` patch: `Reset()` left the index populated and `GetMetaDataVersioned()` re-pointed only the XDocument, so lookups kept serving pre-refresh metadata.                                                 |
| 15    | perf: yield to the message pump between deferred batches           | **MED**       | `ExtLibs/Controls/BackstageView/BackstageView.cs` (+`.Designer.cs`), `GCSViews/ConfigurationView/ConfigRawParams.cs` (+`.Designer.cs`)                       | Repairs rows 5/11-era schedulers: `BeginInvoke` re-armed from inside its own callback does not yield (WinForms drains the queue in a loop), so prewarm and enrichment ran as one blocking batch. Re-arm via one-shot `Timer`.        |
| 16    | perf: hash photo-marker tags instead of rescanning the overlay     | **LOW**       | `GCSViews/FlightData.cs`                                                                                                                                     | O(n²) boxed membership test per 0.3 s map update; visible stall on long camera/survey missions. Touches the same map-update region as row 3, so expect it near a rebase conflict there.                                              |
| 17    | fix(wine): no `Win32_SerialPort` WMI in board detection            | **LOW**       | `Utilities/BoardDetect.cs`                                                                                                                                   | `DetectBoard` branches on Mono, not Wine, so native .NET under Wine hit a class Wine's `wbemprox` does not implement. Same query row 6 already guards in `Program.cs`.                                                               |

> **net48 was tried (11c + 11c.1) and reverted** — it hangs on the splash
> screen under Wine. Stay on `net472`. Table rows 8-11 are the live Phase 11
> patches; the Phase 8-10 perf/Wine commits between rows 7 and 8 are not yet
> itemised here (backfill on next sync).

## Manual sync workflow

```bash
git fetch upstream
git checkout master
git rebase upstream/master   # resolve using the table above
git push --force-with-lease origin master
```

If a rebase conflict hits, prefer to **drop the local commit and re-apply
manually** rather than resolve in-place when upstream has structurally
changed the surrounding code. The patches are intentionally small and
self-contained so dropping + re-applying is cheap.

## Build notes

Windows build host with **VS 2022** (the `Microsoft.VisualStudio.Workload.ManagedDesktop`
workload), the **.NET Framework 4.7.2 Developer Pack**, **.NET SDK 8**, Git, and 7-Zip.

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
             -find 'MSBuild\Current\Bin\amd64\MSBuild.exe' | Select-Object -First 1
$sdk = Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory |
       Where-Object { $_.Name -match '^8\.' } | Sort-Object Name -Descending | Select-Object -First 1
$env:MSBuildSDKsPath  = Join-Path $sdk.FullName 'Sdks'
$env:DOTNET_HOST_PATH = 'C:\Program Files\dotnet\dotnet.exe'
$env:PATH             = 'C:\Program Files\dotnet;' + (Split-Path $msbuild) + ';' + $env:PATH
& $msbuild -v:m -t:Restore -p:Configuration=Release MissionPlanner.sln
& $msbuild -v:m -t:Build   -p:Configuration=Release -m MissionPlanner.sln
.\debloat.ps1 -OutDir .\bin\Release\net472   # ~430 MB -> ~114 MB
```

Clean build ≈ 90 s; incremental ≈ 30 s. Output at `bin\Release\net472\MissionPlanner.exe`
(~8.5 MB, upstream v1.3.83 + Turbo patches). Output dir is `net472` since Phase 11b
(was `net461`). The same `debloat.ps1` runs in CI before packaging, so released zips
are already trimmed.

## Telemetry verification

After patches 1 and 2, the build should make **zero** outbound connections to:

- `dc.services.visualstudio.com` (Application Insights)
- `ssl.google-analytics.com` (Tracking.cs)
- `*.altitudeangel.com` (AltitudeAngel plugin dropped from `.sln`)
- `firmware.ardupilot.org/MissionPlanner/upgrade/` (Update.cs URLs emptied)
- `github.com/.../betarelease/` (BetaUpdateLocation\* emptied)

Verify with a packet capture (e.g. `tcpdump`/Wireshark) on those hosts while
Mission Planner is running.
