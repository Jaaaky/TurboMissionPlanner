using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using log4net;
using Microsoft.Win32;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Phase 9 fork: bundle IBM Plex Sans (SIL OFL 1.1) so the GUI gets a
    /// modern, consistent appearance on both native Windows and Wine,
    /// without requiring the user to install a font on their system.
    ///
    /// Both registration paths are used:
    ///   PrivateFontCollection.AddMemoryFont -- GDI+ (Graphics.DrawString).
    ///   AddFontMemResourceEx                -- GDI  (TextRenderer.DrawText,
    ///                                          which is what WinForms uses
    ///                                          by default since .NET 2.0).
    /// Without the GDI registration, TextBox / ComboBox / DataGridView
    /// silently fall back to the system font.
    ///
    /// Wine GDI+ requires TRUE TrueType outlines (no CFF/OTF, no variable
    /// fonts) -- IBM Plex Sans static TTFs satisfy this.
    /// </summary>
    public static class AppFonts
    {
        private static readonly ILog log =
            LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern IntPtr AddFontMemResourceEx(
            IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern bool RemoveFontMemResourceEx(IntPtr fh);

        // Phase 9 fork: file-based registration. AddFontMemResourceEx alone
        // is not visible to GDI+ name lookup under Wine; extracting the
        // TTFs to disk and registering via AddFontResourceEx with the
        // FR_PRIVATE flag (0x10) makes them visible to BOTH GDI and GDI+
        // name resolution in the current process on Windows and Wine.
        private const uint FR_PRIVATE = 0x10;
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int AddFontResourceExW(string name, uint fl, IntPtr pdv);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RemoveFontResourceExW(string name, uint fl, IntPtr pdv);

        public static PrivateFontCollection Collection { get; } = new PrivateFontCollection();
        public static FontFamily PlexSans { get; private set; }
        public static bool Loaded { get; private set; }

        private static readonly List<IntPtr> _gdiHandles = new List<IntPtr>();
        private static readonly List<IntPtr> _unmanagedBuffers = new List<IntPtr>();
        private static readonly List<string> _registeredPaths = new List<string>();
        private static bool _wineFontsWereWritten;
        private static string _wineHostFontDirLinux;  // /home/<user>/.local/share/fonts/MissionPlanner
        private static string _wineHostFontDirWine;   // Z:\home\<user>\.local\share\fonts\MissionPlanner

        // Embedded resource names. EmbeddedResource paths in MissionPlanner.csproj
        // turn "Fonts\IBMPlexSans-Regular.ttf" into this dotted resource name.
        private static readonly string[] FontResources =
        {
            "MissionPlanner.Fonts.IBMPlexSans-Regular.ttf",
            "MissionPlanner.Fonts.IBMPlexSans-Medium.ttf",
            "MissionPlanner.Fonts.IBMPlexSans-Bold.ttf",
        };

        public static void Load()
        {
            if (Loaded) return;
            try
            {
                var asm = Assembly.GetExecutingAssembly();

                // Debug: enumerate embedded resources so we can see if our
                // resource names match expectations.
                var allRes = asm.GetManifestResourceNames();
                Console.WriteLine("[AppFonts] {0} embedded resources in {1}", allRes.Length, asm.GetName().Name);
                foreach (var r in allRes)
                    if (r.IndexOf("plex", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        r.IndexOf("font", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        r.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine("[AppFonts] resource: {0}", r);

                foreach (var resName in FontResources)
                {
                    try
                    {
                        using (Stream s = asm.GetManifestResourceStream(resName))
                        {
                            if (s == null)
                            {
                                Console.WriteLine("[AppFonts] MISSING resource: {0}", resName);
                                log.Warn("AppFonts: embedded resource not found: " + resName);
                                continue;
                            }

                            byte[] buf = new byte[s.Length];
                            int read = 0, off = 0;
                            while ((read = s.Read(buf, off, buf.Length - off)) > 0) off += read;

                            IntPtr ptr = Marshal.AllocCoTaskMem(buf.Length);
                            Marshal.Copy(buf, 0, ptr, buf.Length);
                            int beforeCount = Collection.Families.Length;
                            try { Collection.AddMemoryFont(ptr, buf.Length); }
                            catch (Exception exAdd) { Console.WriteLine("[AppFonts] AddMemoryFont failed for {0}: {1}", resName, exAdd.Message); }
                            int afterCount = Collection.Families.Length;
                            _unmanagedBuffers.Add(ptr);

                            uint count = 0;
                            IntPtr h = IntPtr.Zero;
                            try { h = AddFontMemResourceEx(ptr, (uint) buf.Length, IntPtr.Zero, ref count); }
                            catch (Exception exGdi) { Console.WriteLine("[AppFonts] AddFontMemResourceEx threw for {0}: {1}", resName, exGdi.Message); }
                            if (h != IntPtr.Zero) _gdiHandles.Add(h);

                            // Phase 9 fork: install into the user's Windows
                            // Fonts directory. Wine GDI+ only resolves font
                            // NAMES against the system-installed font table;
                            // AddFontMemResourceEx and AddFontResourceEx
                            // FR_PRIVATE are both invisible to it (verified).
                            // Writing to %windir%\Fonts makes GDI+ find the
                            // font by name. Per-user install scope (no admin
                            // rights needed); persists across app restarts.
                            int frCount = 0;
                            try
                            {
                                // %windir%\Fonts ; works on Windows AND inside
                                // a Wine prefix.
                                var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                                if (!string.IsNullOrEmpty(fontsDir))
                                {
                                    var basename = resName;
                                    var lastDot = basename.LastIndexOf('.');
                                    var secondLastDot = basename.LastIndexOf('.', lastDot - 1);
                                    if (secondLastDot >= 0) basename = basename.Substring(secondLastDot + 1);
                                    var ttfPath = Path.Combine(fontsDir, basename);
                                    bool wrote = false;
                                    try
                                    {
                                        if (!File.Exists(ttfPath) || new FileInfo(ttfPath).Length != buf.Length)
                                        {
                                            File.WriteAllBytes(ttfPath, buf);
                                            wrote = true;
                                        }
                                    }
                                    catch (Exception exWrite)
                                    {
                                        Console.WriteLine("[AppFonts] write to {0} failed: {1}; trying temp dir", ttfPath, exWrite.Message);
                                        var tmpDir = Path.Combine(Path.GetTempPath(), "MissionPlannerFonts");
                                        Directory.CreateDirectory(tmpDir);
                                        ttfPath = Path.Combine(tmpDir, basename);
                                        File.WriteAllBytes(ttfPath, buf);
                                        wrote = true;
                                    }
                                    // Register without FR_PRIVATE so the system
                                    // font table sees it process-wide.
                                    frCount = AddFontResourceExW(ttfPath, 0u, IntPtr.Zero);
                                    if (frCount > 0) _registeredPaths.Add(ttfPath);
                                    Console.WriteLine("[AppFonts] {0} ({1}) AddFontResourceExW -> {2}", ttfPath, wrote ? "wrote" : "exists", frCount);

                                    // Phase 9g: AddFontResourceExW does NOT
                                    // write the font registry, but Microsoft
                                    // GDI+ (installed under Wine via
                                    // winetricks gdiplus) enumerates font
                                    // family NAMES by reading the registry.
                                    // Without these entries, new Font("Family
                                    // Name", size) silently falls back to
                                    // Tahoma even though AddFontResourceExW
                                    // succeeded. Verified empirically.
                                    RegisterFontInWindowsRegistry(basename);
                                }
                            }
                            catch (Exception exFr) { Console.WriteLine("[AppFonts] file-register failed for {0}: {1}", resName, exFr.Message); }

                            // Phase 9g fork: under Wine, GDI+ ignores both
                            // AddFontMemResourceEx and AddFontResourceExW and
                            // does NOT scan %windir%\Fonts for name lookup.
                            // Wine's GDI+ font name resolution goes through
                            // FontConfig on the Linux host. Install the TTF
                            // into the host user's font dir + run fc-cache.
                            // Effect is process-wide once Wine re-queries
                            // FontConfig (typically on next launch — first
                            // run primes the cache, second run resolves).
                            bool wroteHostFont = false;
                            try
                            {
                                if (MissionPlanner.Program.IsRunningOnWine)
                                {
                                    var basename2 = resName;
                                    var lastDot2 = basename2.LastIndexOf('.');
                                    var secondLastDot2 = basename2.LastIndexOf('.', lastDot2 - 1);
                                    if (secondLastDot2 >= 0) basename2 = basename2.Substring(secondLastDot2 + 1);
                                    wroteHostFont = InstallToWineHostFontDir(buf, basename2);
                                    if (wroteHostFont) _wineFontsWereWritten = true;
                                }
                            }
                            catch (Exception exHost) { Console.WriteLine("[AppFonts] wine-host install failed for {0}: {1}", resName, exHost.Message); }

                            Console.WriteLine("[AppFonts] {0}: {1} bytes  GDI+fams +{2}={3}  GDIMem h={4}  GDIFile {5}  WineHost {6}",
                                resName, buf.Length, afterCount - beforeCount, afterCount, h, frCount, wroteHostFont ? "wrote" : "skip");
                        }
                    }
                    catch (Exception exRes)
                    {
                        Console.WriteLine("[AppFonts] EXC loading {0}: {1}", resName, exRes.Message);
                        log.Error("AppFonts: failed to load " + resName, exRes);
                    }
                }

                // Phase 9g: if we just wrote fonts into the Wine host's
                // ~/.local/share/fonts/MissionPlanner/, prod FontConfig so
                // Wine GDI+ sees them on its next enumeration. The current
                // process likely cached FcConfig at startup, so this primes
                // the cache for the NEXT launch.
                if (_wineFontsWereWritten)
                {
                    TriggerFcCacheRebuild();
                }

                Console.WriteLine("[AppFonts] PrivateFontCollection now contains {0} families:", Collection.Families.Length);
                for (int i = 0; i < Collection.Families.Length; i++)
                    Console.WriteLine("[AppFonts]   [{0}] '{1}'", i, Collection.Families[i].Name);

                if (Collection.Families.Length > 0)
                {
                    foreach (var f in Collection.Families)
                    {
                        if (f.Name.IndexOf("Plex", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            PlexSans = f;
                            break;
                        }
                    }
                    if (PlexSans == null) PlexSans = Collection.Families[0];
                    Loaded = true;
                    Console.WriteLine("[AppFonts] LOADED  PlexSans -> '{0}'", PlexSans.Name);
                    log.Info("AppFonts: loaded family '" + PlexSans.Name + "' (" + Collection.Families.Length + " face(s))");

                    // Verification: does new Font("IBM Plex Sans", 9f) resolve
                    // to the embedded font or silently fall back?
                    try
                    {
                        var ftest = new Font("IBM Plex Sans", 9f);
                        Console.WriteLine("[AppFonts] VERIFY: new Font(\"IBM Plex Sans\", 9f) -> .Name='{0}'  .OriginalFontName='{1}'",
                            ftest.Name, ftest.OriginalFontName ?? "(null)");
                        ftest.Dispose();
                    }
                    catch (Exception exVerify) { Console.WriteLine("[AppFonts] VERIFY exception: " + exVerify.Message); }

                    // Same test via the explicit FontFamily route (always works
                    // if PrivateFontCollection registration succeeded).
                    try
                    {
                        var ffam = new Font(PlexSans, 9f);
                        Console.WriteLine("[AppFonts] VERIFY: new Font(PlexSans, 9f) -> .Name='{0}'", ffam.Name);
                        ffam.Dispose();
                    }
                    catch (Exception exVerify) { Console.WriteLine("[AppFonts] VERIFY family exception: " + exVerify.Message); }
                }
                else
                {
                    Console.WriteLine("[AppFonts] NO FAMILIES REGISTERED");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[AppFonts] LOAD EXCEPTION: " + ex);
                log.Error("AppFonts.Load failed", ex);
            }
        }

        /// <summary>
        /// Convenience factory. Falls back to the WinForms default if the
        /// embedded font failed to load (e.g. on a stripped build).
        /// </summary>
        public static Font Make(float emSize, FontStyle style = FontStyle.Regular)
        {
            if (PlexSans != null)
            {
                // Some styles (e.g. Italic) may not exist if we didn't bundle
                // those weights; clamp to Regular when not available.
                if (!PlexSans.IsStyleAvailable(style))
                    style = FontStyle.Regular;
                try { return new Font(PlexSans, emSize, style, GraphicsUnit.Point); }
                catch { }
            }
            return new Font("Microsoft Sans Serif", emSize, style, GraphicsUnit.Point);
        }

        // Phase 9g: register a TTF in the Windows font registry. GDI+ name
        // lookup walks HKLM\Software\Microsoft\Windows NT\CurrentVersion\Fonts
        // for system-installed fonts; AddFontResourceExW alone (which only
        // adds a process-private/session table entry, no registry write) is
        // invisible to GDI+'s family-name resolver. Per-user HKCU mirror
        // exists in some Windows builds for non-admin installs; try both.
        private static void RegisterFontInWindowsRegistry(string basename)
        {
            string displayName;
            string baseLower = basename.ToLowerInvariant();
            if (baseLower.Contains("medium"))      displayName = "IBM Plex Sans Medium";
            else if (baseLower.Contains("bold"))   displayName = "IBM Plex Sans Bold";
            else if (baseLower.Contains("italic")) displayName = "IBM Plex Sans Italic";
            else                                   displayName = "IBM Plex Sans";
            string valueName = displayName + " (TrueType)";

            foreach (var hive in new[] { "HKLM", "HKCU" })
            {
                try
                {
                    var root = (hive == "HKLM") ? Registry.LocalMachine : Registry.CurrentUser;
                    using (var key = root.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts"))
                    {
                        if (key == null)
                        {
                            Console.WriteLine("[AppFonts] registry {0}: CreateSubKey returned null", hive);
                            continue;
                        }
                        key.SetValue(valueName, basename, RegistryValueKind.String);
                        Console.WriteLine("[AppFonts] registry {0}: '{1}' = '{2}'", hive, valueName, basename);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[AppFonts] registry {0} failed: {1}", hive, ex.Message);
                }
            }
        }

        // Phase 9g: write a font into the Linux host's user font dir as seen
        // from inside Wine. Wine maps "/" to the Z: drive, and HOME is
        // inherited from the calling shell (it survives into the Wine process
        // environment). We read HOME directly and write via Z:\<HOME>\...
        // Returns true iff a new file was created (or an existing file was
        // overwritten because the size differed).
        private static bool InstallToWineHostFontDir(byte[] buf, string basename)
        {
            if (_wineHostFontDirWine == null)
            {
                string linuxHome = ResolveLinuxHome();
                if (string.IsNullOrEmpty(linuxHome))
                {
                    Console.WriteLine("[AppFonts] wine-host: could not resolve Linux $HOME, skipping");
                    return false;
                }
                _wineHostFontDirLinux = linuxHome.TrimEnd('/') + "/.local/share/fonts/MissionPlanner";
                _wineHostFontDirWine  = "Z:" + _wineHostFontDirLinux.Replace('/', '\\');
                try { Directory.CreateDirectory(_wineHostFontDirWine); }
                catch (Exception ex)
                {
                    Console.WriteLine("[AppFonts] wine-host: CreateDirectory {0} failed: {1}", _wineHostFontDirWine, ex.Message);
                    _wineHostFontDirWine = null;
                    return false;
                }
                Console.WriteLine("[AppFonts] wine-host: dir = {0}  (linux: {1})", _wineHostFontDirWine, _wineHostFontDirLinux);
            }

            string path = Path.Combine(_wineHostFontDirWine, basename);
            bool exists = File.Exists(path);
            bool sameSize = exists && new FileInfo(path).Length == buf.Length;
            if (exists && sameSize)
            {
                Console.WriteLine("[AppFonts] wine-host: {0} already present ({1} bytes)", path, buf.Length);
                return false;
            }

            try
            {
                File.WriteAllBytes(path, buf);
                Console.WriteLine("[AppFonts] wine-host: WROTE {0} ({1} bytes)", path, buf.Length);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[AppFonts] wine-host: write {0} failed: {1}", path, ex.Message);
                return false;
            }
        }

        // Wine does NOT forward the Linux HOME env var into the Win32
        // environment block accessible by .NET. Probe via several routes.
        private static string ResolveLinuxHome()
        {
            // 1. HOME env var (works on native Linux .NET, NOT under Wine).
            var h = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(h) && h.StartsWith("/"))
            {
                Console.WriteLine("[AppFonts] ResolveLinuxHome: HOME env -> {0}", h);
                return h;
            }

            // 2. Shell out to printenv on the Linux side. Wine forwards
            //    Process.Start FileName="/usr/bin/printenv" through to the
            //    host's exec(). The child sees Linux env, so HOME is present.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/printenv",
                    Arguments = "HOME",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        string s = p.StandardOutput.ReadToEnd().Trim();
                        p.WaitForExit(2000);
                        if (!string.IsNullOrEmpty(s) && s.StartsWith("/"))
                        {
                            Console.WriteLine("[AppFonts] ResolveLinuxHome: printenv -> {0}", s);
                            return s;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[AppFonts] ResolveLinuxHome: printenv failed: {0}", ex.Message);
            }

            // 3. Fallback: /home/<UserName>. UserName under Wine matches the
            //    Linux account that launched the prefix. root is special.
            string u = Environment.UserName;
            if (string.IsNullOrEmpty(u))
            {
                Console.WriteLine("[AppFonts] ResolveLinuxHome: no UserName, giving up");
                return null;
            }
            string guess = u == "root" ? "/root" : ("/home/" + u);
            // Validate by checking the Z: side exists.
            try
            {
                var wineSide = "Z:" + guess.Replace('/', '\\');
                if (Directory.Exists(wineSide))
                {
                    Console.WriteLine("[AppFonts] ResolveLinuxHome: guess {0} -> exists, using", guess);
                    return guess;
                }
                Console.WriteLine("[AppFonts] ResolveLinuxHome: guess {0} not present on Z: drive", guess);
            }
            catch { }
            return null;
        }

        // Phase 9g: invoke Linux fc-cache from inside Wine. Wine accepts
        // /usr/bin/<binary> as a FileName for ProcessStartInfo and forwards
        // it to the Linux exec path. Tries the most reliable form first.
        private static void TriggerFcCacheRebuild()
        {
            // The user may not have fc-cache (extremely rare on a desktop
            // Linux with FontConfig installed). Probe Z:\usr\bin\fc-cache
            // first so we can log a useful message if it's missing.
            string[] candidates =
            {
                "/usr/bin/fc-cache",
                "/usr/local/bin/fc-cache",
                "fc-cache",
            };

            foreach (var fc in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = fc,
                        Arguments = _wineHostFontDirLinux != null ? ("-f \"" + _wineHostFontDirLinux + "\"") : "-f",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p == null)
                        {
                            Console.WriteLine("[AppFonts] fc-cache: Start({0}) returned null", fc);
                            continue;
                        }
                        bool exited = p.WaitForExit(8000);
                        string sout = p.StandardOutput.ReadToEnd();
                        string serr = p.StandardError.ReadToEnd();
                        if (!exited)
                        {
                            try { p.Kill(); } catch { }
                            Console.WriteLine("[AppFonts] fc-cache: timed out");
                            return;
                        }
                        Console.WriteLine("[AppFonts] fc-cache: {0} exit={1} out='{2}' err='{3}'",
                            fc, p.ExitCode, sout.Trim(), serr.Trim());
                        if (p.ExitCode == 0) return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[AppFonts] fc-cache: Start({0}) threw {1}", fc, ex.Message);
                }
            }

            Console.WriteLine("[AppFonts] fc-cache: all candidates failed -- fonts may not be visible until you run 'fc-cache -f' manually and restart Mission Planner");
        }

        public static void Unload()
        {
            foreach (string p in _registeredPaths)
            {
                try { RemoveFontResourceExW(p, 0u, IntPtr.Zero); } catch { }
            }
            _registeredPaths.Clear();

            foreach (IntPtr h in _gdiHandles)
            {
                try { RemoveFontMemResourceEx(h); } catch { }
            }
            _gdiHandles.Clear();

            try { Collection.Dispose(); } catch { }

            foreach (IntPtr p in _unmanagedBuffers)
            {
                try { Marshal.FreeCoTaskMem(p); } catch { }
            }
            _unmanagedBuffers.Clear();

            Loaded = false;
        }
    }
}
