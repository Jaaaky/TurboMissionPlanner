using System;
using System.Configuration;
using System.IO;
using System.Xml.Linq;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Threading.Tasks;
using log4net;
using SharpCompress.Compressors.Xz;

namespace MissionPlanner.Utilities
{
    public static class ParameterMetaDataRepositoryAPMpdef
    {
        private static readonly ILog log =
            LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static Dictionary<string,XDocument> _parameterMetaDataXML = new Dictionary<string, XDocument>();

        // Phase 9 fork: per-vehicle index of name -> XElement. Built ONCE at
        // Reload time; subsequent GetParameterMetaData calls are O(1) dict
        // lookups instead of the O(N) XDocument walk through ~1500 params
        // per call. Each cache miss in ParameterMetaDataRepository used to
        // cost 1-5ms; with this index it's ~0.01ms. ConfigRawParams
        // first-open paid this 6000+ times.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, XElement>>
            _paramIndex = new System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, XElement>>();

        private static string[] vehicles = new[]
        {
             "SITL", "AP_Periph", "ArduSub", "Rover", "ArduCopter",
            "ArduPlane", "AntennaTracker", "Blimp", "Heli"      
        };

        private static string[] vehicles_versioned = new[] 
        {
            "Copter", "Plane", "Rover", "Sub", "Tracker"
        };

        static string url = "https://autotest.ardupilot.org/Parameters/{0}/apm.pdef.xml.gz";

        static string urlversioned = "https://autotest.ardupilot.org/Parameters/versioned/{0}/stable-{1}/apm.pdef.xml";

        static ParameterMetaDataRepositoryAPMpdef()
        {
            GetMetaData();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ParameterMetaDataRepository"/> class.
        /// </summary>
        public static void CheckLoad(string vehicle = "")
        {
            if (!_parameterMetaDataXML.ContainsKey(vehicle))
                Reload(vehicle);
        }

        public static async Task GetMetaDataVersioned(Version version)
        {
            List<Task> tlist = new List<Task>();

            vehicles_versioned.ForEach(a =>
            {
                try
                {
                    var newurl = String.Format(urlversioned, a, version.ToString());
                    var file = Path.Combine(Settings.GetDataDirectory(), a + version.ToString() + ".apm.pdef.xml");
                    if (File.Exists(file))
                        if (new FileInfo(file).LastWriteTime.AddDays(7) > DateTime.Now)
                            return;
                    var dltask = Download.getFilefromNetAsync(newurl, file);
                    tlist.Add(dltask);
                }
                catch (Exception ex) { log.Error(ex); }
            });

            await Task.WhenAll(tlist);

            vehicles_versioned.ForEach(a =>
            {
                try
                {
                    Reload(a + version.ToString());

                    var veh = vehicles.First(b => b.Contains(a));

                    if(_parameterMetaDataXML.ContainsKey(a + version.ToString()))
                        _parameterMetaDataXML[veh] = _parameterMetaDataXML[a + version.ToString()];
                }
                catch (Exception ex) { log.Error(ex); }
            });
        }

        public static async Task GetMetaData(bool force = false)
        {
            List<Task> tlist = new List<Task>();

            vehicles.ForEach(a =>
            {
                try
                {
                    var newurl = String.Format(url, a);
                    // try the gzipped version first
                    var file = Path.Combine(Settings.GetDataDirectory(), a + ".apm.pdef.xml.gz");
                    if(File.Exists(file))
                        if (new FileInfo(file).LastWriteTime.AddDays(7) > DateTime.Now && !force)
                            return;
                    // try just the xml
                    var file2 = Path.Combine(Settings.GetDataDirectory(), a + ".apm.pdef.xml");
                    if (File.Exists(file2))
                        if (new FileInfo(file2).LastWriteTime.AddDays(7) > DateTime.Now && !force)
                            return;
                    var dltask = Download.getFilefromNetAsync(newurl, file);
                    tlist.Add(dltask);
                }
                catch (Exception ex) { log.Error(ex); }
            });

            await Task.WhenAll(tlist);

            vehicles.ForEach(a =>
            {
                try
                {
                    var fileout = Path.Combine(Settings.GetDataDirectory(), a + ".apm.pdef.xml");
                    var fileouttemp = Path.Combine(Path.GetTempFileName());
                    var file = Path.Combine(Settings.GetDataDirectory(), a + ".apm.pdef.xml.gz");
                    if (File.Exists(file))
                    {
                        // drop out to prevent unnessary fileio at startup
                        if (File.Exists(fileout) && new FileInfo(fileout).LastWriteTime.AddDays(7) > DateTime.Now && !force)
                            return;
                        using (var read = File.OpenRead(file))
                        {
                            //if (XZStream.IsXZStream(read))
                            {
                                read.Position = 0;
                                var stream = new GZipStream(read, CompressionMode.Decompress);
                                //var stream = new XZStream(read);
                                using (var outst = File.Open(fileouttemp, FileMode.Create))
                                {
                                    stream.CopyTo(outst);
                                }
                                // move after good decompress
                                File.Delete(fileout);
                                File.Move(fileouttemp, fileout);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                }
            });

            Reset();
        }

        public static void Reset()
        {
            _parameterMetaDataXML.Clear();
        }

        private static void BuildParamIndex(string vehicle, XDocument doc)
        {
            try
            {
                var idx = new Dictionary<string, XElement>(2048, StringComparer.Ordinal);
                var paramfile = doc.Element("paramfile");
                if (paramfile != null)
                {
                    foreach (var parameters in paramfile.Elements())
                    {
                        foreach (var ps in parameters.Elements())
                        {
                            if (!ps.HasAttributes) continue;
                            foreach (var param in ps.Elements())
                            {
                                var nameAttr = param.Attribute("name");
                                if (nameAttr == null) continue;
                                idx[nameAttr.Value] = param;
                            }
                        }
                    }
                }
                _paramIndex[vehicle] = idx;
            }
            catch (Exception ex)
            {
                log.Error("BuildParamIndex(" + vehicle + ")", ex);
            }
        }

        public static void Reload(string vehicle = "")
        {
            string paramMetaDataXMLFileName =
                String.Format("{0}{1}", Settings.GetDataDirectory(), vehicle + ".apm.pdef.xml");

            try
            {
                if (File.Exists(paramMetaDataXMLFileName))
                {
                    var doc = XDocument.Load(paramMetaDataXMLFileName);
                    _parameterMetaDataXML[vehicle] = doc;

                    // Phase 9 fork: build flat name->XElement index in one
                    // pass so GetParameterMetaData() is O(1) instead of
                    // O(N) per call. See _paramIndex declaration.
                    BuildParamIndex(vehicle, doc);
                }

            }
            catch (System.Xml.XmlException ex) 
            {
                try
                {
                    if (File.Exists(paramMetaDataXMLFileName))
                        File.Delete(paramMetaDataXMLFileName);
                }
                catch { }
                log.Error(paramMetaDataXMLFileName);
                log.Error(ex);
            }
            catch (Exception ex)
            {
                log.Error(paramMetaDataXMLFileName);
                log.Error(ex);
            }
        }

        /// <summary>
        /// Gets the parameter meta data.
        /// </summary>
        /// <param name="nodeKey">The node key.</param>
        /// <param name="metaKey">The meta key.</param>
        /// <returns></returns>
        public static string GetParameterMetaData(string nodeKey, string metaKey, string vechileType)
        {
            // remap names
            if (vechileType == "ArduCopter2")
                vechileType = "ArduCopter";
            if (vechileType == "ArduRover")
                vechileType = "Rover";
            if (vechileType == "ArduTracker")
                vechileType = "AntennaTracker";

            CheckLoad(vechileType);

            // remap keys
            if (metaKey == ParameterMetaDataConstants.DisplayName)
                metaKey = "humanName";
            if (metaKey == ParameterMetaDataConstants.Description)
                metaKey = "documentation";
            if (metaKey == ParameterMetaDataConstants.User)
                metaKey = "user";

            // Phase 9 fork: O(1) index lookup. Try the prefixed key first
            // (matches upstream's "VEHICLE:nodeKey" then plain nodeKey
            // priority).
            if (_paramIndex.TryGetValue(vechileType, out var idx))
            {
                try
                {
                    XElement param;
                    if (!idx.TryGetValue(vechileType + ":" + nodeKey, out param))
                        idx.TryGetValue(nodeKey, out param);
                    if (param == null) return string.Empty;

                    var attr = param.Attribute(metaKey);
                    if (attr != null) return attr.Value;

                    if (metaKey == ParameterMetaDataConstants.Values)
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var a in param.Elements("values").Elements())
                        {
                            if (a.Name == "value")
                            {
                                var code = a.Attribute("code");
                                if (code != null)
                                    sb.Append(code.Value).Append(':').Append(a.Value).Append(',');
                            }
                        }
                        return sb.ToString();
                    }

                    foreach (var xElement in param.Elements())
                    {
                        if (xElement.Name == "field")
                        {
                            var name = xElement.Attribute("name");
                            if (name != null && name.Value == metaKey)
                                return xElement.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                }
            }

            return string.Empty;
        }
    }
}