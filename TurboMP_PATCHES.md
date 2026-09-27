# Turbo patches vs upstream master

Tracker for the downstream patches maintained against
[`ArduPilot/MissionPlanner`](https://github.com/ArduPilot/MissionPlanner).
Update after every upstream sync. The **Conflict risk** column flags which
patches are most likely to clash on a future rebase, so the next sync session
can scan them quickly.

Branch layout:

- `upstream/master` — read-only mirror of ArduPilot/MissionPlanner.
- `master` — the Turbo branch; all patches live here. Default branch on the fork.

Last sync: rebased onto upstream `8cdd00fe2` (2026-09-27). 28 of 29 patches
re-applied byte-identically (`git range-diff` all `=`). Row 12 conflicted as
predicted: upstream shipped the same `TryGetValue` Do-Action fix in `efb080190`,
so our code half was dropped and only its tracker note survives. The other new
upstream commits (MAVFtp `ListDirectoryWithTime`, `Debugger.Break` guard, mass
storage reboot action) touch no code our patches modify.

v0.3.0 (2026-09-27, released as `turbo-v0.3.0`): rows 19-55 from the deep review
(plan `.claude/tasks/2026-09-27-deep-review-plan.md` in the GCSs workspace).
Row 19 reverts most of the Phase 10h persistence work, so row 15's BackstageView
prewarm half no longer exists; its ConfigRawParams half remains.

v0.3.1 (2026-09-28): row 56 completes the translations (satellite `.resx` only; no
English base `.resx` touched) and adds Persian (`fa`) and Uyghur (`ug`). It edits about 990 upstream
satellite files that upstream refreshes from Crowdin, so expect conflicts there on every sync.

v0.3.2 (2026-09-28): rows 57-59 make Arabic-script and CJK UI languages render under Wine
(plan `.claude/tasks/2026-09-28-script-fonts-plan.md` in the GCSs workspace). Wine shapes Arabic only
with the selected font, so those languages switch the UI font to a bundled Turbo Sans font.

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
| 12    | ~~fix: guard Do-Action custom-action lookup (upstream #3744)~~     | **DROPPED**   | n/a                                                                                                                                                          | Upstream fixed it identically in `efb080190` (2026-09-17). Code half dropped at the 2026-09-27 rebase; the commit now only carries the 2026-08 sync note in this file. |
| 13    | fix: speech opt-in works without an app restart                    | **LOW**       | `GCSViews/ConfigurationView/ConfigPlanner.cs`                                                                                                                | Repairs row 11-era Phase 10p deferral: `speechEnable`'s setter is a no-op while `speechEngine` is null, so the checkbox did nothing until relaunch. Builds + attaches the engine on the enable transition.                            |
| 14    | fix: keep the param metadata index in step with the document       | **LOW**       | `ExtLibs/Utilities/ParameterMetaDataRepositoryAPMpdef.cs`                                                                                                    | Repairs the Phase 9 `_paramIndex` patch: `Reset()` left the index populated and `GetMetaDataVersioned()` re-pointed only the XDocument, so lookups kept serving pre-refresh metadata.                                                 |
| 15    | perf: yield to the message pump between deferred batches           | **MED**       | `ExtLibs/Controls/BackstageView/BackstageView.cs` (+`.Designer.cs`), `GCSViews/ConfigurationView/ConfigRawParams.cs` (+`.Designer.cs`)                       | Repairs rows 5/11-era schedulers: `BeginInvoke` re-armed from inside its own callback does not yield (WinForms drains the queue in a loop), so prewarm and enrichment ran as one blocking batch. Re-arm via one-shot `Timer`.        |
| 16    | perf: hash photo-marker tags instead of rescanning the overlay     | **LOW**       | `GCSViews/FlightData.cs`                                                                                                                                     | O(n²) boxed membership test per 0.3 s map update; visible stall on long camera/survey missions. Touches the same map-update region as row 3, so expect it near a rebase conflict there.                                              |
| 17    | fix(wine): no `Win32_SerialPort` WMI in board detection            | **LOW**       | `Utilities/BoardDetect.cs`                                                                                                                                   | `DetectBoard` branches on Mono, not Wine, so native .NET under Wine hit a class Wine's `wbemprox` does not implement. Same query row 6 already guards in `Program.cs`.                                                               |
| 18    | docs: correct upstream `CLAUDE.md` for this fork                   | **MED**       | `CLAUDE.md`                                                                                                                                                  | Upstream added `CLAUDE.md` in `9515c8804` documenting `bin\Release\net461`, the android/mac workflows and a `beta` release tag — all wrong here. It is agent-facing, so a stale copy actively misdirects. Upstream will keep editing this file; re-apply the three corrections by hand. |
| 19    | fix: rebuild Setup/Config screens fresh on every visit | **MED** | `MainV2.cs`, `GCSViews/InitialSetup.cs`, `GCSViews/SoftwareConfig.cs`, `ConfigParamLoading.cs`, `ConfigFlightModes.cs`, `ConfigUserDefined.cs`; restores upstream `ConfigSerial.cs`, `MainSwitcher.cs`, `BackstageView.Designer.cs` | Reverts the Phase 10h persistent hosts, preload and whole-page prewarm (and the BackstageView page cache). Net effect shrinks the fork delta. Remaining hunks: `gotAllParams` rep>0 in 3 files, Loading timer stop, FlightModes `Tick -=`. |
| 20    | fix: startup port-list refresh keeps the saved baud | **MED** | `MainV2.cs` | Ctor port-list block plus `PopulateSerialportList(string[])` overload and `RefreshSerialportList`; MainV2 churns upstream. |
| 21    | fix: serial reader loop waits 5 ms, not 50 ms | **LOW** | `MainV2.cs` | One line in `SerialReader`. |
| 22    | fix: thread-safe metadata map, index published first | **LOW** | `ExtLibs/Utilities/ParameterMetaDataRepositoryAPMpdef.cs` | Builds on row 14; `ConcurrentDictionary`, publication and `Reset` under one lock with a reset generation. |
| 23    | fix: clear metadata caches when the metadata changes | **LOW** | `ParameterMetaDataRepository.cs`, `ParamDisplayCache.cs`, `ParameterMetaDataRepositoryAPMpdef.cs` | `ClearCache()` swaps the answer-cache instance; fork-owned cache files. |
| 24    | fix(release): keep IronPython stdlib and arm64 Skia | **LOW** | `debloat.ps1` | Fork-only file. |
| 25    | fix: hide SIMULATION when simulation is disabled | **LOW** | `MainV2.cs` | One line in `updateLayout`. |
| 26    | fix: Gridv2 plugin loads again, default off | **LOW** | `Plugin/PluginLoader.cs`, `MainV2.cs` | Pass-through line plus a one-shot `PluginsForkGridv2Off_v1` migration after the v4 seed. |
| 27    | fix: Full Parameter List enrichment survives sorting | **LOW** | `GCSViews/ConfigurationView/ConfigRawParams.cs` | Fork-owned enrichment code (row 15). |
| 28    | fix: recover signing keys from official MP | **LOW** | `ExtLibs/ArduPilot/Mavlink/MAVAuthKeys.cs`, `ExtLibs/Utilities/Crypto.cs` | Both files already fork-modified; upstream has not touched them since 2020. |
| 29    | fix: firmware page board detection after re-visit | **LOW** | `GCSViews/ConfigurationView/ConfigFirmware.cs` | 3 lines in `Activate`. |
| 30    | fix: Motor Test does not stack buttons | **LOW** | `GCSViews/ConfigurationView/ConfigMotorTest.cs` | List of dynamic controls in `Activate`. |
| 31    | fix: Radio Calibration labels do not grow | **LOW** | `GCSViews/ConfigurationView/ConfigRadioInput.cs` | Base-label array in `Activate`. |
| 32    | fix: joystick preview resumes after re-visit | **LOW** | `Joystick/JoystickSetup.cs`, `.Designer.cs` | Designer `timer1.Enabled` line removed; new `Activate`. |
| 33    | fix: Verify Height keeps passed altitudes, no 0 m tiles | **MED** | `GCSViews/FlightPlanner.cs` | `setfromMap` Verify Height branches; FlightPlanner churns upstream. Uses `System.TimeSpan` (SharpKml also defines `TimeSpan`). |
| 34    | fix: Survey (Grid) altitudes in the altitude unit | **LOW** | `Grid/GridUI.cs` | Five `multiplierdist` to `multiplieralt` swaps. |
| 35    | fix: Log Browser keeps the vehicle's live params | **LOW** | `Log/LogBrowse.cs` | Private `_logParams`, 5 read sites; Show Params always opens a log-owned read-only viewer (with save to .param). |
| 36    | fix: re-downloaded logs get a unique name | **LOW** | `Log/LogDownloadMavLink.cs` | Two moves plus `UniqueFileName`; file last changed upstream 2023. |
| 37    | fix: MAVLink log download truncation/stall/cleanup | **MED** | `ExtLibs/ArduPilot/Mavlink/MAVLinkInterface.cs` (`GetLog`), `Log/LogDownloadMavLink.cs` | `GetLog` body rewritten (HashSet, cursor, 30 s stall, finally); MAVLinkInterface churns upstream. |
| 38    | fix: Messages tab shows every new message | **LOW** | `GCSViews/FlightData.cs` | `Messagetabtimer_Tick` compare. |
| 39    | fix: TCP Host mirror no longer crashes | **LOW** | `Controls/SerialOutputPass.cs` | Async-state tuple, callback try/catch, listener stop. |
| 40    | fix: joystick sender pacing; MANUAL_CONTROL target | **MED** | `MainV2.cs` | `joysticksend` loop; MainV2 churns. |
| 41    | fix: tile HttpClient timeout 10 s | **LOW** | `ExtLibs/GMap.NET.Core/.../GMapProvider.cs` | One field initializer. |
| 42    | fix: Bing init off the UI thread | **LOW** | `ExtLibs/GMap.NET.Core/.../Bing/BingMapProvider.cs` | `OnInitialized` wrapper; old body renamed `InitVersionAndKey`. |
| 43    | fix: camera/gimbal probes respect link ownership | **MED** | `MAVLinkInterface.cs` (`doCommandAsync`, `doCommandIntAsync`), `CurrentState.cs`, `CameraProtocol.cs` | Two removed flag clears in a churned file; the camera info request is now sent without an ACK wait (never takes the link). |
| 44    | fix: MAVFtp uploads complete only when every chunk is acked | **LOW** | `ExtLibs/ArduPilot/Mavlink/MAVFtp.cs` | `UploadFile` and `kCmdWriteFile` (ACKs matched to this transfer's sequence numbers); low churn. |
| 45    | fix: sysid switch deactivates before the swap | **LOW** | `Controls/ConnectionControl.cs` | `CMB_sysid_SelectedIndexChanged` (non-persistent screens only, `current` kept set). |
| 46    | fix: tlog CSV invariant culture | **LOW** | `ExtLibs/ArduPilot/Mavlink/MAVLinkInterface.cs` | One line in `DebugPacket`. |
| 47    | fix: map prefetch caches every layer | **LOW** | `ExtLibs/GMap.NET.WindowsForms/.../TilePrefetcher.cs` | Two `return true` to `continue`. |
| 48    | feat(wine): run-wine.sh launcher + one-time notice | **LOW** | `run-wine.sh` (new), `.gitattributes` (new), `.github/workflows/main.yml`, `README.md`, `MainV2.cs` | New files are fork-only; MainV2 gets a helper and one call in `doConnect`. |
| 49    | fix(wine): no /dev ports, no GC per click | **LOW** | `ExtLibs/Comms/CommsSerialPort.cs` | One condition (row 4 also touches this file). |
| 50    | fix: speech voice probe, sticky failure, late attach | **MED** | `Utilities/Speech.cs`, `ExtLibs/Utilities/Warnings/WarningEngine.cs`, `MainV2.cs` | Speech ctor/IsReady, WarningEngine spin, MainV2 deferred-init task. |
| 51    | fix(wine): GStreamer lookup ignores host Linux libs | **LOW** | `ExtLibs/Utilities/GStreamer.cs` | `LookForGstreamer` only; moderate churn elsewhere in the file. |
| 52    | fix(wine): vario loop elapsed-time pacing | **LOW** | `ExtLibs/Utilities/Vario.cs` | `mainloop` only. |
| 53    | perf: log graph presets skip the dead IronPython pass | **LOW** | `Log/LogBrowse.cs` | Call site only; `TestPython` itself is left in place (unused). |
| 54    | fix: log expressions: per-record args, TYPE[n], lowpass | **LOW** | `ExtLibs/Utilities/DFLogScript.cs` | `ProcessExpression` loop and the `lowpass` class. |
| 55    | ci: post-debloat artifact checks | **LOW** | `.github/workflows/main.yml` | Fork-only workflow. |
| 56    | feat: complete translations and add Persian and Uyghur | **HIGH** | ~990 modified + 812 new satellite `*.<culture>.resx`; `GCSViews/ConfigurationView/ConfigPlanner.cs` (language list) | Upstream rewrites satellite `.resx` files from Crowdin. On conflict, prefer upstream's file and re-run the translation fill for that file; our new `fa`/`ug` files and the two list entries in `ConfigPlanner.cs` rarely clash. |
| 57    | fix(l10n): Traditional Chinese strings that were in Simplified | **LOW** | `GCSViews/FlightPlanner.zh-Hant.resx`, `GCSViews/FlightData.zh-TW.resx` | 12 values; a Crowdin refresh may bring the Simplified text back (Turbo Sans TC then shows boxes for those characters). |
| 58    | feat: Turbo Sans script fonts (IBM Plex derivatives) | **LOW** | `Fonts/TurboSans*.ttf`, `Fonts/tools/build_fonts.py`, `Fonts/README.txt`, `MissionPlanner.csproj`, `.github/workflows/main.yml` | Fork-only files; csproj lines sit next to our Plex block. Rerun `uv run Fonts/tools/build_fonts.py` after translation updates so the CJK subsets cover new characters. |
| 59    | fix: script UI font for Arabic-script and CJK languages | **MED** | `Utilities/AppFonts.cs`, `Utilities/ThemeManager.cs` (`ApplyThemeTo`), `MainV2.cs` (after `changelanguage`), `ExtLibs/Controls/CustomMessageBox.cs` | `MainV2` constructor and `CustomMessageBox.Show` change upstream now and then; the ThemeManager hook is one line. Wine-only registry writes: FontLink\SystemLink (IBM Plex Sans, Tahoma) and HKCU\Software\Wine\Uniscribe\Fallback. |

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
