# Mission Planner (Turbo)

[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/Jaaaky/TurboMissionPlanner)

A blazing-fast [ArduPilot/MissionPlanner](https://github.com/ArduPilot/MissionPlanner) fork with first-class Wine/Linux support — snappier UI, optimized algorithms, debloated install, dozens of upstream bugs fixed, and zero telemetry.
Same GCS, same MAVLink, same `.NET Framework 4.7.2` build. Tracks upstream `master` and stays cleanly rebaseable.

## What's different from upstream

**Privacy / telemetry — removed or off by default**

- Application Insights, Google Analytics, and the AltitudeAngel airspace pings are disabled. No outbound telemetry.
- Auto-updater (upstream binary channel) disabled — it won't overwrite this build.
- Anonymous stats opt-out, ADSB, and voice/Speech all default to **off**.

**Performance**

- `O(1)` MAVLink parameter lookup (was `O(n)` per call) — Full Parameter List and config tabs load fast.
- Config tab deep-fix: background `ParamDisplayCache` pre-warm, two-pass row build, cached page state. ConfigRawParams went 758 ms → sub-200 ms.
- HUD repaint invalidations coalesced; idle SerialReader busy-loop removed; `Settings.Save()` race fixed.
- Plugins and SITL default to **off** with an opt-in chooser; plugin DLLs loaded on a background thread; dependency-DLL whitelist skips ~150 non-plugin assemblies.
- Quieter logging and console (root log level raised, per-packet spam gated).

**Wine / Linux friendliness**

- Skips the Wine-hostile WMI `Win32_SerialPort` query and RAS enumeration.
- Embedded font registration so GDI+ name lookup resolves under Wine.
- Speech/SAPI **off by default** — a real performance win under Wine, where the COM voice-token enumeration is slow and noisy (hundreds of `fixme:sapi` calls) at startup.
- ~300 MB lighter install (debloat drops dead-arch natives, duplicate plugin tree, GDAL, PDBs).

Run under Wine by bootstrapping the prefix with the real Microsoft runtime (never wine-mono):

```bash
winetricks --unattended dotnet472 gdiplus windowscodecs
wine MissionPlanner.exe
```

**Other**

- **Fixes the upstream MAVLink signing-key loss bug** (ArduPilot/MissionPlanner#3694): keys were silently wiped when the NIC-MAC-derived AES key changed or a keyfile failed to decrypt. The fork derives the key from a stable machine id, refuses to overwrite a keyfile it couldn't load, and sets an un-decryptable file aside instead of destroying it — so your signing keys survive.
- MAVLink signing keys stored in `turbomp-authkeys.xml` (separate from upstream's `authkeys.xml`) so you can run this fork and official Mission Planner side by side without clobbering each other's keys.
- User data directory unchanged (`%ProgramData%\Mission Planner`, `Documents\Mission Planner`) — settings, maps, and logs are shared with upstream.

> Stays on `net472`. A `net48` bump was tried and reverted — it hangs on the splash screen under Wine.

---

Upstream website : http://ardupilot.org/planner/

Upstream forum : http://discuss.ardupilot.org/c/ground-control-software/mission-planner

Changelog : https://github.com/ArduPilot/MissionPlanner/blob/master/ChangeLog.txt

License : https://github.com/ArduPilot/MissionPlanner/blob/master/COPYING.txt (GPLv3, inherited from upstream)

## How to compile

### On Windows (Recommended)

#### 1. Install software

##### Main requirements

Currently, Mission Planner needs:

Visual Studio 2022

##### IDE

### Visual Studio Community

To compile Mission Planner, we recommend using Visual Studio. You can download Visual Studio Community from the [Visual Studio Download page](https://visualstudio.microsoft.com/downloads/ "Visual Studio Download page").

Visual Studio is a comprehensive suite with built-in Git support, but it can be overwhelming due to its complexity. To streamline the installation process, you can customize your installation by selecting the relevant "Workloads" and "Individual components" based on your software development needs.

To simplify this selection process, we have provided a configuration file that specifies the components required for MissionPlanner development. Here's how you can use it:

1. Go to "More" in the Visual Studio installer.
2. Select "Import configuration."
3. Use the following file: [vs2022.vsconfig](https://raw.githubusercontent.com/ArduPilot/MissionPlanner/master/vs2022.vsconfig "vs2022.vsconfig").

By following these steps, you'll have the necessary components installed and ready for Mission Planner development.

###### VSCode

Currently VSCode with C# plugin is able to parse the code but cannot build.

#### 2. Get the code

If you get Visual Studio Community, you should be able to use Git from the IDE.
Clone `https://github.com/ArduPilot/MissionPlanner.git` to get the full code.

In case you didn't install an IDE, you will need to manually install Git. Please follow instruction in https://ardupilot.org/dev/docs/where-to-get-the-code.html#downloading-the-code-using-git

Open a git bash terminal in the MissionPlanner directory and type, "git submodule update --init" to download all submodules

#### 3. Build

To build the code:

- Open MissionPlanner.sln with Visual Studio
- From the Build menu, select "Build MissionPlanner"

### On other systems

Building Mission Planner on other systems isn't support currently.

## Launching Mission Planner on other system

Mission Planner is available for Android via the Play Store. https://play.google.com/store/apps/details?id=com.michaeloborne.MissionPlanner
Mission Planner can be used with Mono on Linux systems. Be aware that not all functions are available on Linux.
Native MacOS and iOS support is experimental and not recommended for inexperienced users. https://github.com/ArduPilot/MissionPlanner/releases/tag/osxlatest
For MacOS users it is recommended to use Mission Planner for Windows via Boot Camp or Parallels (or equivalent).

### On Linux

#### Requirements

Those instructions were tested on Ubuntu 20.04.
Please install Mono, either :

- `sudo apt install mono-complete mono-runtime libmono-system-windows-forms4.0-cil libmono-system-core4.0-cil libmono-winforms4.0-cil libmono-corlib4.0-cil libmono-system-management4.0-cil libmono-system-xml-linq4.0-cil`

#### Launching

- Get the lastest zipped version of Mission Planner here : https://firmware.ardupilot.org/Tools/MissionPlanner/MissionPlanner-latest.zip
- Unzip in the directory you want
- Go into the directory
- run with `mono MissionPlanner.exe`

You can debug Mission Planner on Mono with `MONO_LOG_LEVEL=debug mono MissionPlanner.exe`

### External Services Used

| Source                            | Use                                                                                                                                      | How to disable                                                               | Custodian      |
| --------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------- | -------------- |
| https://firmware.oborne.me        | used as a global cdn for checking for MP update check - checked once per day at startup                                                  | edit missionplanner.exe.config                                               | Michael Oborne |
| https://firmware.ardupilot.org    | used for updates to stable, firmware metadata, firmware, user alerts, gstreamer, SRTM, SITL                                              | updates to stable (edit missionplanner.exe.config) - all others Not possible | Ardupilot Team |
| https://github.com/               | used for updates to beta                                                                                                                 | edit missionplanner.exe.config                                               | Michael Oborne |
| https://raw.githubusercontent.com | old param metadata, sitl config files                                                                                                    | Not possible                                                                 | Ardupilot Team |
| https://api.github.com/           | ardupilot preload param files                                                                                                            | Not possible                                                                 | Ardupilot Team |
| https://raw.oborne.me/            | used as glocal cdn for parameter metadata generator, no longer primary source                                                            | only used at user request to regenerate, edit missionplanner.exe.config      | Michael Oborne |
| https://maps.google.com           | used for elevation api - removed due to abuse                                                                                            | N/A                                                                          | N/A            |
| https://discuss.cubepilot.org/    | use for SB2 reporting - only on affected boards when user enters details                                                                 | only used at user request                                                    | CubePilot      |
| https://altitudeangel.com         | utm data - user enabled                                                                                                                  | only used at user request                                                    | Altitude Angel |
| https://autotest.ardupilot.org    | dataflash log meta data, parameter metadata                                                                                              | Not Possible                                                                 | Ardupilot Team |
| Many                              | your choice of map provider google/bing/openstreetmap/etc                                                                                | User selectable                                                              | User/Many      |
| https://www.cloudflare.com        | geo location provider - for NFZ selection                                                                                                | Not Possible                                                                 | Michael Oborne |
| https://esua.cad.gov.hk           | HK no fly zones - user enabled                                                                                                           | User selectable                                                              | HK Gov         |
| https://ssl.google-analytics.com  | Google Analytics Anonymous Stats - Screen Loads, Exceptions/Crashs, Events (Connect), Startup Timing, FW upload (FW Type and Board Type) | disable in Config > Planner > OptOut Anon Stats                              | Michael Oborne |
| https://api.dronelogbook.com      | logging - disabled                                                                                                                       | N/A                                                                          | N/A            |
| https://ardupilot.org             | help urls on many pages                                                                                                                  | User Initiated                                                               | ArduPilot Team |
| https://www.youtube.com           | help videos on many pages                                                                                                                | User Initiated                                                               | ArduPilot Team |
| https://files.rfdesign.com.au     | RFD firmwares                                                                                                                            | User Initiated                                                               | RFDesign       |
| https://teck.airmarket.io         | airmarket - disabled                                                                                                                     | N/A                                                                          | N/A            |

### Offline Use - No Internet

| Location                                         | Use                   | Transferable between pcs |
| ------------------------------------------------ | --------------------- | ------------------------ |
| C:\ProgramData\Mission Planner\gmapcache         | Map cache             | yes                      |
| C:\ProgramData\Mission Planner\srtm              | Elevation data cache  | yes                      |
| C:\ProgramData\Mission Planner\\\*.pdef.xml      | Parameter cache       | yes                      |
| C:\ProgramData\Mission Planner\LogMessages\*.xml | DF Log metadata cache | yes                      |

on linux this is in /home/<user>/.local/share/Mission Planner/

### Offline Data Supported

#### Elevation

- SRTM Cache
- GeoTiff's in WGS84/EGM96
- DTED

#### Images

- Map Cache
- WMS
- WMTS
- GDAL

### Paths used - Default

| Location                                    | Use                    |
| ------------------------------------------- | ---------------------- |
| C:\ProgramData\Mission Planner              | All cross user content |
| C:\Users\USERNAME\Documents\Mission Planner | All per user content   |

on linux this is in /home/<user>/.local/share/Mission Planner/

### CA Cert

A CA cert is installed to the root store and used to sign the windows serial port drivers, and is installed as part of the MSI install.

[![FlagCounter](https://s01.flagcounter.com/count2/A4bA/bg_FFFFFF/txt_000000/border_CCCCCC/columns_8/maxflags_40/viewers_0/labels_1/pageviews_0/flags_0/percent_0/)](https://info.flagcounter.com/A4bA)
