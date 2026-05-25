using log4net;
using MissionPlanner.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MissionPlanner.Controls;
using DroneCAN;
using System.Text.RegularExpressions;
using System.Linq.Expressions;
using System.Threading;

namespace MissionPlanner.Plugin
{
    public class PluginLoader
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        static PluginLoader()
        {

        }

        //List of disabled plugins (as dll file names)
        public static List<String> DisabledPluginNames = new List<String>();
        // Plugin enable/disable settings changed not loaded but enabled plugins will not shown
        public static bool bRestartRequired = false;

        public static List<Plugin> LoadingPlugins = new List<Plugin>();
        public static List<Plugin> Plugins = new List<Plugin>();

        // Phase 8 fix: filecache + ErrorInfo are static dictionaries accessed
        // from the AssemblyResolve callback (any thread the CLR happens to
        // be on) and from PluginLoader.LoadAll's Task.Run + InitPlugin paths.
        // Lock all reads/writes via _cacheLock to prevent torn dictionary
        // state during concurrent assembly resolution.
        // Phase 9 fork: known dependency-DLL prefixes that ship in the
        // plugins/ folder but are NOT plugin assemblies. Anything matching
        // is loaded lazily by the CLR's AssemblyResolve handler when an
        // actual consumer asks for it; explicit LoadFile is wasteful here.
        private static readonly string[] DepDllPrefixes =
        {
            "microsoft.", "system.", "accord", "alglibnet", "avifile",
            "baseclasses", "basclasses", "bitmiracle", "bouncycastle",
            "brutile", "bse.windows", "core.dll", "crc32",
            "csassortedwidgets", "csmatio", "deviceprogramming",
            "directshowlib", "dotnetzip", "dotspatial", "exiflibrary",
            "flurl", "gdal", "gdalconst", "gdal_csharp", "geoapi",
            "geoidheights", "geojson.net", "geoutility", "gmap.net",
            "icsharpcode", "interfaces.dll", "ironpython", "jetbrains",
            "kmlib", "libtessdotnet", "libusb", "libvlc",
            "managednativewifi", "mathparser", "mavlink.dll",
            "metadataextractor", "missionplanner.antenna",
            "missionplanner.ardupilot", "missionplanner.comms",
            "missionplanner.controls", "missionplanner.drawing",
            "missionplanner.grid", "missionplanner.gridv2",
            "missionplanner.hil", "missionplanner.maps",
            "missionplanner.strings", "missionplanner.utilities",
            "missionplanner.webapis", "mono.posix", "nefarius",
            "netdxf", "newtonsoft", "nettopologysuite", "objectlistview",
            "ogr_csharp", "onvif", "opentk", "osr_csharp",
            "projnet", "px4uploader", "renci", "restsharp",
            "sharpadbclient", "sharpcompress", "sharpdx", "sharpkml",
            "simpleble", "sixlabors", "skiasharp", "socketioclient",
            "solo.dll", "supersocket", "svgnet", "transitions",
            "usbserialforandroid", "webcamservice", "websocket4net",
            "xamarin", "zedgraph", "zeroconf", "zlib", "log4net",
            // Phase 10b fork: additional dep DLLs surfacing as bogus
            // plugin entries in user reports.
            "7zip", "arduino", "altitudeangelwings", "dronecan",
            "markdig", "nodatime", "polly", "sharpfont", "humanizer",
            "humanizer.core", "jsondiffpatch", "mavlinkkitlibrary",
            "rclcommandextract", "rocheltslibrary", "scintilla",
            "scintillanet", "wixtoolset", "uavcan", "tag"
        };

        public static bool IsDependencyDll(string lowercaseName)
        {
            // Explicit plugins always pass through (override prefix matches).
            if (lowercaseName.Contains("plugin") ||
                lowercaseName == "trackerhome.dll" ||
                lowercaseName == "facemap.dll" ||
                lowercaseName == "bulb.dll" ||
                lowercaseName == "osdconfigurator.dll" ||
                lowercaseName == "opendroneid.dll" ||
                lowercaseName == "shortcuts.dll" ||
                lowercaseName == "extguided.dll" ||
                lowercaseName == "missionplanner.stats.dll" ||
                lowercaseName == "missionplanner.simplegrid.dll" ||
                lowercaseName == "tlogthumbnailhandler.dll")
                return false;

            for (int i = 0; i < DepDllPrefixes.Length; i++)
                if (lowercaseName.StartsWith(DepDllPrefixes[i]))
                    return true;
            return false;
        }

        public static Dictionary<string, string[]> filecache = new Dictionary<string, string[]>();
        public static Dictionary<string, string> ErrorInfo = new Dictionary<string, string>();
        private static readonly object _cacheLock = new object();

        private static string[] GetCachedFiles(string folderPath)
        {
            lock (_cacheLock)
            {
                if (!filecache.TryGetValue(folderPath, out var files))
                {
                    files = Directory.GetFiles(folderPath, "*.dll", SearchOption.AllDirectories);
                    filecache[folderPath] = files;
                }
                return files;
            }
        }

        static Assembly LoadFromSameFolder(object sender, ResolveEventArgs args)
        {
            if (args.RequestingAssembly == null)
                return null;

            // check install folder
            string folderPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var files = GetCachedFiles(folderPath);
            var needle = new AssemblyName(args.Name).Name.ToLower() + ".dll";

            foreach (var file in files.Where(a => a.ToLower().Contains(needle)))
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(file);
                    if (assembly.FullName == args.Name)
                        return assembly;
                }
                catch { }
            }

            // check local directory
            folderPath = Path.GetDirectoryName(args.RequestingAssembly.Location);
            files = GetCachedFiles(folderPath);

            foreach (var file in files.Where(a => a.ToLower().Contains(needle)))
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(file);
                    if (assembly.FullName == args.Name)
                        return assembly;
                }
                catch { }
            }

            log.Info("LoadFromSameFolder " + args.RequestingAssembly + "-> " + args.Name);

            return null;
        }

        public static void Load(String file)
        {
            if (!File.Exists(file) || !file.EndsWith(".dll", true, null))
                return;

            // Phase 9 fork: tightened skip list. The plugins/ folder contains
            // ~150 dependency DLLs (ZedGraph, SkiaSharp, IronPython, Xamarin,
            // BouncyCastle, GMap, Newtonsoft, etc.) that the CLR loads via
            // AssemblyResolve on demand -- LoadFile()-ing them here wastes
            // tens of ms per file, spams the log, and triggers Wine's
            // amsi:AmsiScanBuffer fixme storm. Skip them.
            var name = Path.GetFileName(file).ToLower();
            if (IsDependencyDll(name))
                return;

            //Check if it is disabled (moved out from the previous IF, to make it loggable)
            if (DisabledPluginNames.Contains(Path.GetFileName(file).ToLower()))
            {
                log.InfoFormat("Plugin {0} is disabled in config.xml", Path.GetFileName(file));
                return;
            }

            // file exists in the install directory, so skip trying to load it as a plugin
            if (File.Exists(file) && File.Exists(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) +
                                                 Path.DirectorySeparatorChar + Path.GetFileName(file)))
                return;

            AppDomain currentDomain = AppDomain.CurrentDomain;
            currentDomain.AssemblyResolve += new ResolveEventHandler(LoadFromSameFolder);

            Assembly asm = null;

            DateTime startDateTime = DateTime.Now;

            try
            {
                asm = Assembly.LoadFile(file);
                log.Info("Plugin Load " + file);
            }
            catch (Exception)
            {
                // unable to load
                return;
            }

            InitPlugin(asm, file);

            log.InfoFormat("Plugin Load {0} time {1} s", file, (DateTime.Now - startDateTime).TotalSeconds);
        }

        public static void InitPlugin(Assembly asm, string pluginfilename)
        {
            if (asm == null)
                return;

            try
            {
                Type[] types = asm.GetTypes();
                Type type = typeof(MissionPlanner.Plugin.Plugin);
                foreach (var t in types)
                {
                    if (type == t)
                        continue;

                    if (type.IsAssignableFrom((Type)t))
                    {
                        Type pluginInfo = t;
                        if (pluginInfo != null)
                        {
                            try
                            {
                                //pluginInfo.GetConstructor(Type.EmptyTypes);
                                Object o = Expression.Lambda<Func<object>>(Expression.New(pluginInfo)).Compile()();
                                //Object o = Activator.CreateInstance(pluginInfo, BindingFlags.Default, null, null, CultureInfo.CurrentUICulture);
                                Plugin plugin = (Plugin)o;

                                plugin.Assembly = asm;

                                plugin.Host = new PluginHost();
                                plugin.FileName = Path.GetFileName(pluginfilename);

                                if (plugin.Init())
                                {
                                    log.InfoFormat("Plugin Init {0} {1} by {2}", plugin.Name, plugin.Version, plugin.Author);
                                    lock (LoadingPlugins)
                                    {
                                        LoadingPlugins.Add(plugin);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                log.Error("Failed to load plugin " + asm.FullName, ex);
                            }
                        }
                    }
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                log.Error("Failed to load plugin " + asm.FullName, ex);
                log.Error("Failed to load plugin " + asm.FullName, ex.LoaderExceptions.FirstOrDefault());
            }
            catch (Exception ex)
            {
                log.Error("Failed to load plugin " + asm.FullName, ex);
            }
        }

        public static void LoadAll()
        {
            string path = Settings.GetRunningDirectory() + "plugins" +
                          Path.DirectorySeparatorChar;

            log.Info("Plugin path: "+path);

            if (!Directory.Exists(path))
                return;

            // cs plugins are background compiled, and loaded in the ui thread
            Task.Run(() =>
            {
                String[] csFiles = Directory.GetFiles(path, "*.cs");

                foreach (var csFile in csFiles)
                {
                    log.Info("Plugin: " + csFile);
                    //Check if it is disabled (moved out from the previous IF, to make it loggable)
                    if (DisabledPluginNames.Contains(Path.GetFileName(csFile).ToLower()))
                    { 
                        log.InfoFormat("Plugin {0} is disabled in config.xml", Path.GetFileName(csFile));
                        continue;
                    }

                    //loadassembly: MissionPlanner.WebAPIs
                    var content = File.ReadAllText(csFile);

                    var matches = Regex.Matches(content, @"^\/\/loadassembly: (.*)$", RegexOptions.Multiline);
                    foreach (Match m in matches)
                    {
                        try
                        {
                            log.Info("Try load " + m.Groups[1].Value.Trim());
                            Assembly.Load(m.Groups[1].Value.Trim());
                        }
                        catch (Exception ex)
                        {
                            log.Error(ex);
                        }
                    }

                    try
                    {
                        // csharp 8
                        var ans = CodeGenRoslyn.BuildCode(csFile);

                        if (CodeGenRoslyn.lasterror != "")
                            lock(ErrorInfo)
                                ErrorInfo[csFile] = CodeGenRoslyn.lasterror;

                        InitPlugin(ans, Path.GetFileName(csFile));

                        log.Info("CodeGenRoslyn: " + csFile);
                        if (Program.MONO)
                            Thread.Sleep(2000);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex);
                    }


                    try
                    {
                        //csharp 5 max

                        // create a compiler
                        var compiler = CodeGen.CreateCompiler();
                        // get all the compiler parameters
                        var parms = CodeGen.CreateCompilerParameters();
                        // compile the code into an assembly
                        var results = CodeGen.CompileCodeFile(compiler, parms, csFile);

                        if (CodeGenRoslyn.lasterror != "")
                            lock (ErrorInfo)
                                ErrorInfo[csFile] = CodeGen.lasterror;

                        InitPlugin(results?.CompiledAssembly, Path.GetFileName(csFile));

                        if (results?.CompiledAssembly != null)
                        {
                            log.Info("CodeGen: " + csFile);
                            if (Program.MONO)
                                Thread.Sleep(2000);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex);
                    }
                }

                // Fork patch: .dll loading + self-reflection were running
                // synchronously on the UI thread after the .cs Task.Run
                // completed. Each Assembly.LoadFile + GetTypes() walk is slow
                // on Wine. Move them into the same background task; bubble the
                // final PluginInit() to UI exactly once.
                String[] dllFiles = Directory.GetFiles(path, "*.dll");
                foreach (var s in dllFiles)
                {
                    try { Load(Path.Combine(Environment.CurrentDirectory, s)); }
                    catch (Exception ex) { log.Error("Plugin DLL load failed: " + s, ex); }
                }

                try { InitPlugin(Assembly.GetAssembly(typeof(PluginLoader)), "self"); }
                catch (Exception ex) { log.Error("Plugin self-init failed", ex); }

                MainV2.instance.BeginInvokeIfRequired(() =>
                {
                    PluginInit();
                });
            });
        }

        private static void PluginInit()
        {
            List<Plugin> LoadingSnapshot;

            lock (LoadingPlugins)
            {
                LoadingSnapshot = LoadingPlugins.ToList();
                LoadingPlugins.Clear();
            }

            foreach (var p in LoadingSnapshot)
            {
                try
                {
                    if (p.Loaded())
                    {
                        lock (Plugins)
                        {
                            Plugins.Add(p);
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                }
            }
        }
    }
}