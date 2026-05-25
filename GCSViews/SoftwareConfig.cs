using log4net;
using MissionPlanner.ArduPilot;
using MissionPlanner.Controls;
using MissionPlanner.Controls.BackstageView;
using MissionPlanner.GCSViews.ConfigurationView;
using MissionPlanner.Utilities;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    public partial class SoftwareConfig : MyUserControl, IActivate
    {
        internal static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static string lastpagename = "";
        [Flags]
        public enum pageOptions
        {
            none = 0,
            isConnected = 1,
            isDisConnected = 2,
            isTracker = 4,
            isCopter = 8,
            isCopter35plus = 16,
            isHeli = 32,
            isQuadPlane = 64,
            isPlane = 128,
            isRover = 256,
            gotAllParams = 512
        }

        public class pluginPage
        {
            public Type page;
            public string headerText;
            public pageOptions options;

            public pluginPage(Type page, string headerText, pageOptions options)
            {
                this.page = page;
                this.headerText = headerText;
                this.options = options;
            }
        }


        private static List<pluginPage> pluginViewPages = new List<pluginPage>();

        public static void AddPluginViewPage(Type page, string headerText, pageOptions options = pageOptions.none)
        {
            pluginViewPages.Add(new pluginPage(page, headerText, options));
        }
        public bool isConnected
        {
            get { return MainV2.comPort.BaseStream.IsOpen; }
        }
        public bool isTracker
        {
            get { return isConnected && MainV2.comPort.MAV.cs.firmware == Firmwares.ArduTracker; }
        }

        public bool isCopter
        {
            get { return isConnected && MainV2.comPort.MAV.cs.firmware == Firmwares.ArduCopter2; }
        }

        public bool isCopter35plus
        {
            get { return MainV2.comPort.MAV.cs.version >= Version.Parse("3.5"); }
        }

        public bool isHeli
        {
            get { return isConnected && MainV2.comPort.MAV.aptype == MAVLink.MAV_TYPE.HELICOPTER; }
        }

        public bool isQuadPlane
        {
            get
            {
                return isConnected && isPlane &&
                       MainV2.comPort.MAV.param.ContainsKey("Q_ENABLE") &&
                       (MainV2.comPort.MAV.param["Q_ENABLE"].Value == 1.0);
            }
        }

        public bool isPlane
        {
            get
            {
                return isConnected &&
                       (MainV2.comPort.MAV.cs.firmware == Firmwares.ArduPlane ||
                        MainV2.comPort.MAV.cs.firmware == Firmwares.Ateryx);
            }
        }

        public bool isRover
        {
            get { return isConnected && MainV2.comPort.MAV.cs.firmware == Firmwares.ArduRover; }
        }


        public bool gotAllParams
        {
            get
            {
                log.InfoFormat("TotalReceived {0} TotalReported {1}", MainV2.comPort.MAV.param.TotalReceived,
                    MainV2.comPort.MAV.param.TotalReported);
                if (MainV2.comPort.MAV.param.TotalReceived < MainV2.comPort.MAV.param.TotalReported)
                {
                    return false;
                }

                return true;
            }
        }
        public SoftwareConfig()
        {
            InitializeComponent();
        }

        // Phase 10j fork: track whether the current backstage page set was
        // built for the current connection state. SoftwareConfig is now
        // Persistent across tab switches (so we don't pay the 2-3s rebuild
        // every visit), but that means SoftwareConfig_Load fires only ONCE
        // -- typically at startup-preload time, while disconnected. Without
        // this rebuild trigger, connecting to a vehicle later never adds
        // BasicTuning / ExtendedTuning / FullParamList / FW-specific tabs.
        private bool _builtForConnected = false;
        private Firmwares _builtForFirmware = Firmwares.PX4;
        private bool _firstBuildDone = false;

        public void Activate()
        {
            try
            {
                bool nowConnected = MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen;
                Firmwares nowFw = nowConnected ? MainV2.comPort.MAV.cs.firmware : Firmwares.PX4;
                if (_firstBuildDone && (nowConnected != _builtForConnected || nowFw != _builtForFirmware))
                {
                    MissionPlanner.Utilities.Profiler.Mark("SoftwareConfig.Activate:state-changed -> rebuild");
                    RebuildPages();
                }
            }
            catch (Exception ex) { log.Warn("SoftwareConfig.Activate rebuild check: " + ex.Message); }
        }

        public BackstageViewPage AddBackstageViewPage(Type userControl, string headerText,
            BackstageViewPage Parent = null, bool advanced = false)
        {
            try
            {
                return backstageView.AddPage(userControl, headerText, Parent, advanced);
            }
            catch (Exception ex)
            {
                log.Error(ex);
                return null;
            }
        }

        private void SoftwareConfig_Load(object sender, EventArgs e)
        {
            MissionPlanner.Utilities.Profiler.Mark("SoftwareConfig.Load:begin");
            RebuildPages();
            MissionPlanner.Utilities.Profiler.Mark("SoftwareConfig.Load:done");
        }

        // Phase 10j fork: extracted from SoftwareConfig_Load so we can also
        // call it from Activate when the connection state changes.
        private void RebuildPages()
        {
            MissionPlanner.Utilities.Profiler.Mark("SoftwareConfig.RebuildPages:begin");
            try
            {
                // Phase 10o fork: SoftReset() preserves _pageCache so re-Add
                // of the same Type reuses the existing Control instance. The
                // hard Reset() disposed every Page and caused 2.8s spikes on
                // disconnect (BackstageView.Add:ConfigPlanner took 2887ms in
                // profile-20260524-203825 because Activator.CreateInstance +
                // InitializeComponent + ApplyTheme cascade re-ran).
                try
                {
                    backstageView.SoftReset();
                }
                catch (Exception exClr) { log.Warn("backstageView soft reset: " + exClr.Message); }

                bool nowConnected = MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen;
                Firmwares nowFw = nowConnected ? MainV2.comPort.MAV.cs.firmware : Firmwares.PX4;
                _builtForConnected = nowConnected;
                _builtForFirmware = nowFw;
                _firstBuildDone = true;

                BackstageViewPage start = null;

                if (gotAllParams)
                {
                    if (MainV2.comPort.BaseStream.IsOpen)
                    {
                        if (MainV2.comPort.MAV.cs.firmware == Firmwares.ArduCopter2)
                        {
                            if (MainV2.DisplayConfiguration.displayGeoFence)
                            {
                                AddBackstageViewPage(typeof(ConfigAC_Fence), Strings.GeoFence);
                            }
                        }

                        if (MainV2.comPort.MAV.cs.firmware == Firmwares.ArduCopter2)
                        {
                            if (MainV2.DisplayConfiguration.displayBasicTuning)
                            {
                                start = AddBackstageViewPage(typeof(ConfigSimplePids), Strings.BasicTuning);
                            }

                            if (MainV2.DisplayConfiguration.displayExtendedTuning)
                            {
                                AddBackstageViewPage(typeof(ConfigArducopter), Strings.ExtendedTuning);
                            }
                        }

                        if (MainV2.comPort.MAV.cs.firmware == Firmwares.ArduPlane)
                        {
                            if (MainV2.DisplayConfiguration.displayBasicTuning)
                            {
                                start = AddBackstageViewPage(typeof(ConfigArduplane), Strings.BasicTuning);
                            }

                            if (MainV2.DisplayConfiguration.displayExtendedTuning)
                            {
                                AddBackstageViewPage(typeof(ConfigArducopter), "QP " + Strings.ExtendedTuning);
                            }
                        }

                        if (MainV2.comPort.MAV.cs.firmware == Firmwares.ArduRover)
                        {
                            start = AddBackstageViewPage(typeof(ConfigArdurover), Strings.BasicTuning);
                        }

                        if (MainV2.comPort.MAV.cs.firmware == Firmwares.ArduTracker)
                        {
                            start = AddBackstageViewPage(typeof(ConfigAntennaTracker), Strings.ExtendedTuning);
                        }

                        if (MainV2.DisplayConfiguration.displayStandardParams)
                        {
                            AddBackstageViewPage(typeof(ConfigFriendlyParams), Strings.StandardParams);
                        }

                        if (MainV2.DisplayConfiguration.displayAdvancedParams)
                        {
                            AddBackstageViewPage(typeof(ConfigFriendlyParamsAdv), Strings.AdvancedParams, null, true);
                        }

                        if (!Program.MONO && ConfigOSD.IsApplicable() && MainV2.DisplayConfiguration.displayOSD)
                        {
                            AddBackstageViewPage(typeof(ConfigOSD), Strings.OnboardOSD);
                        }

                        if (MainV2.DisplayConfiguration.displayMavFTP)
                        {
                            if ((MainV2.comPort.MAV.cs.capabilities & (int)MAVLink.MAV_PROTOCOL_CAPABILITY.FTP) > 0)
                            {
                                AddBackstageViewPage(typeof(MavFTPUI), Strings.MAVFtp);
                            }
                        }

                        if (MainV2.DisplayConfiguration.displayUserParam)
                        {
                            AddBackstageViewPage(typeof(ConfigUserDefined), Strings.User_Params);
                        }
                    }
                }

                if (MainV2.DisplayConfiguration.displayFullParamList)
                {
                    if(!MainV2.comPort.BaseStream.IsOpen || gotAllParams)
                        AddBackstageViewPage(typeof(ConfigRawParams), Strings.FullParameterList, null, false);
                }
                if (MainV2.comPort.BaseStream.IsOpen)
                {
                    if (MainV2.comPort.MAV.cs.firmware == Firmwares.Ateryx)
                    {
                        start = AddBackstageViewPage(typeof(ConfigFlightModes), Strings.FlightModes);
                        AddBackstageViewPage(typeof(ConfigAteryxSensors), "Ateryx Zero Sensors");
                        AddBackstageViewPage(typeof(ConfigAteryx), "Ateryx Pids");
                    }

                    if (!gotAllParams)
                    {
                        if (start == null)
                            start = AddBackstageViewPage(typeof(ConfigParamLoading), Strings.Loading);
                        else
                            AddBackstageViewPage(typeof(ConfigParamLoading), Strings.Loading);
                    }

                    if (MainV2.DisplayConfiguration.displayPlannerSettings)
                    {
                        AddBackstageViewPage(typeof(ConfigPlanner), Strings.Planner);
                    }
                }
                else
                {
                    if (MainV2.DisplayConfiguration.displayPlannerSettings)
                    {
                        start = AddBackstageViewPage(typeof(ConfigPlanner), Strings.Planner);
                    }
                }

                // Add custrom pages set up by plugins
                foreach (var item in pluginViewPages)
                {

                    // go through all options expect disconnected since there is no meaning for sw config in disconnected state
                    if (item.options.HasFlag(pageOptions.isConnected) && !isConnected)
                        continue;
                    if (item.options.HasFlag(pageOptions.isTracker) && !isTracker)
                        continue;
                    if (item.options.HasFlag(pageOptions.isCopter) && !isCopter)
                        continue;
                    if (item.options.HasFlag(pageOptions.isCopter35plus) && !isCopter35plus)
                        continue;
                    if (item.options.HasFlag(pageOptions.isHeli) && !isHeli)
                        continue;
                    if (item.options.HasFlag(pageOptions.isQuadPlane) && !isQuadPlane)
                        continue;
                    if (item.options.HasFlag(pageOptions.isPlane) && !isPlane)
                        continue;
                    if (item.options.HasFlag(pageOptions.isRover) && !isRover)
                        continue;
                    if (item.options.HasFlag(pageOptions.gotAllParams) && !gotAllParams)
                        continue;

                    AddBackstageViewPage(item.page, item.headerText);
                }



                // apply theme before trying to display it
                ThemeManager.ApplyThemeTo(this);

                // remeber last page accessed
                foreach (BackstageViewPage page in backstageView.Pages)
                {
                    if (page.LinkText == lastpagename)
                    {
                        backstageView.ActivatePage(page);
                        break;
                    }
                }


                if (backstageView.SelectedPage == null && start != null)
                    this.BeginInvoke((Action) delegate
                    {
                        try
                        {
                            backstageView.ActivatePage(start);
                        }
                        catch (Exception ex)
                        {
                            log.Error(ex);
                        }
                    });

                // Phase 10g fork: pre-construct every sub-page on the message
                // pump so subsequent clicks don't pay the per-page handle-
                // creation + InitializeComponent cost (~2-3s for ConfigPlanner
                // on Wine). User input always preempts the prewarmer.
                this.BeginInvoke((Action) delegate
                {
                    try { backstageView.PrewarmAllAsync(); }
                    catch (Exception ex) { log.Warn("SWConfig prewarm: " + ex.Message); }
                });
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
            // Phase 10p3 fork: SoftReset + AddPage batch needs explicit
            // menu redraw or pnlMenu stays empty (no buttons appear) on
            // first launch when lastpagename has no match.
            try { backstageView.RedrawMenu(); }
            catch (Exception ex) { log.Warn("SoftwareConfig.RebuildPages RedrawMenu: " + ex.Message); }
            MissionPlanner.Utilities.Profiler.Mark("SoftwareConfig.RebuildPages:done");
        }

        private void SoftwareConfig_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (backstageView.SelectedPage != null)
                lastpagename = backstageView.SelectedPage.LinkText;

            backstageView.Close();
        }
    }
}