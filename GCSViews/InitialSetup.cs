using log4net;
using MissionPlanner.ArduPilot;
using MissionPlanner.Controls;
using MissionPlanner.Controls.BackstageView;
using MissionPlanner.GCSViews.ConfigurationView;
using MissionPlanner.Radio;
using MissionPlanner.Utilities;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Resources;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    public partial class InitialSetup : MyUserControl, IActivate
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


        public InitialSetup()
        {
            InitializeComponent();
        }

        public bool isConnected
        {
            get { return MainV2.comPort.BaseStream.IsOpen; }
        }

        public bool isDisConnected
        {
            get { return !MainV2.comPort.BaseStream.IsOpen; }
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
                // Phase 10p5 fork: upstream returned true when Received==0 and
                // Reported==0 (the disconnected / mid-handshake case). That
                // tricked RebuildPages into adding all the connection +
                // params-required tabs (Servo Output, Compass, ...) before
                // any params had actually arrived; Activate then read an
                // empty MAV.param and showed defaults forever. Now require
                // the vehicle to have at least reported a total before
                // claiming we have it all.
                int rx = MainV2.comPort.MAV.param.TotalReceived;
                int rep = MainV2.comPort.MAV.param.TotalReported;
                if (rep == 0) return false;       // nothing reported yet
                if (rx < rep) return false;       // still downloading
                return true;
            }
        }

        public BackstageViewPage AddBackstageViewPage(Type userControl, string headerText, bool enabled = true,
    BackstageViewPage Parent = null, bool advanced = false)
        {
            try
            {
                if (enabled)
                    return backstageView.AddPage(userControl, headerText, Parent, advanced);
            }
            catch (Exception ex)
            {
                log.Error(ex);
                return null;
            }

            return null;
        }

        // Phase 10p fork: track what state we built the page list for. On
        // Windows the preload path fires Load while DISCONNECTED, building
        // the Setup tab with most `isConnected && gotAllParams` filters
        // false -> page list nearly empty -> tab appears blank when the
        // user clicks it after vehicle connect. Activate now compares
        // current state and rebuilds if diverged. Wine masks the bug
        // because OnLoad doesn't reliably fire during off-screen preload
        // there, so Load runs at click-time with the right state.
        private bool? _builtForConnected;
        private Firmwares _builtForFirmware;
        private bool _builtForGotAllParams;

        public void Activate()
        {
            try
            {
                bool nowConnected = MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen;
                Firmwares nowFw = nowConnected ? MainV2.comPort.MAV.cs.firmware : Firmwares.PX4;
                bool nowGotParams = gotAllParams;
                var msg = string.Format("InitialSetup.Activate: now=({0},{1},{2}) built=({3},{4},{5}) pages={6}",
                    nowConnected, nowFw, nowGotParams,
                    _builtForConnected, _builtForFirmware, _builtForGotAllParams,
                    backstageView.Pages.Count);
                // Phase 10p6 fork: bug fixed in 10p5; downgrade trace verbosity
                // to Debug so it stops polluting the default WARN-level log.
                // Profiler.Mark still fires so MP_PROFILER=1 catches it.
                log.Debug(msg);
                MissionPlanner.Utilities.Profiler.Mark(msg);
                if (_builtForConnected == nowConnected
                    && _builtForFirmware == nowFw
                    && _builtForGotAllParams == nowGotParams
                    && backstageView.Pages.Count > 0)
                {
                    log.Debug("InitialSetup.Activate: SKIP rebuild - state unchanged");
                    return;
                }
                log.Debug("InitialSetup.Activate: state diverged, calling RebuildPages");
                RebuildPages();
            }
            catch (Exception ex)
            {
                log.Warn("InitialSetup.Activate rebuild check exception: " + ex);
            }
        }

        private void HardwareConfig_Load(object sender, EventArgs e)
        {
            var msg = string.Format("InitialSetup.HardwareConfig_Load FIRED. isOpen={0} TotalReceived={1} TotalReported={2}",
                MainV2.comPort?.BaseStream?.IsOpen, MainV2.comPort?.MAV?.param?.TotalReceived,
                MainV2.comPort?.MAV?.param?.TotalReported);
            log.Debug(msg);
            MissionPlanner.Utilities.Profiler.Mark(msg);
            RebuildPages();
        }

        private void RebuildPages()
        {
            // Phase 10p fork: extracted from HardwareConfig_Load so Activate
            // can re-run the page build when connection state changes. Uses
            // BackstageView.SoftReset (Phase 10o) so existing Page Controls
            // are reused on rebuild instead of disposed + reconstructed.
            try { backstageView.SoftReset(); }
            catch (Exception ex) { log.Warn("InitialSetup SoftReset: " + ex.Message); }

            ResourceManager rm = new ResourceManager(this.GetType());

            _builtForConnected = MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen;
            _builtForFirmware = _builtForConnected.Value ? MainV2.comPort.MAV.cs.firmware : Firmwares.PX4;
            _builtForGotAllParams = gotAllParams;
            var buildMsg = string.Format("InitialSetup.RebuildPages BUILDING: connected={0} firmware={1} gotAllParams={2}",
                _builtForConnected, _builtForFirmware, _builtForGotAllParams);
            log.Debug(buildMsg);
            MissionPlanner.Utilities.Profiler.Mark(buildMsg);

            if (!gotAllParams)
            {
                if (MainV2.comPort.BaseStream.IsOpen)
                    AddBackstageViewPage(typeof(ConfigParamLoading), Strings.Loading);
            }

            if (MainV2.DisplayConfiguration.displayInstallFirmware)
            {
                // if (!Program.WindowsStoreApp)
                {
                    AddBackstageViewPage(typeof(ConfigFirmwareDisabled), rm.GetString("backstageViewPagefw.Text"),
                        isConnected);
                    AddBackstageViewPage(typeof(ConfigFirmwareManifest), rm.GetString("backstageViewPagefw.Text"),
                        isDisConnected);
                    AddBackstageViewPage(typeof(ConfigFirmware), rm.GetString("backstageViewPagefw.Text") + " Legacy",
                        isDisConnected);
                }
            }

            AddBackstageViewPage(typeof(ConfigSecureAP), "Secure",
                isDisConnected);


            var mand = AddBackstageViewPage(typeof(ConfigMandatory), rm.GetString("backstageViewPagemand.Text"), isConnected && gotAllParams);

            if (MainV2.DisplayConfiguration.displayFrameType)
            {
                //AddBackstageViewPage(typeof(ConfigTradHeli), rm.GetString("backstageViewPagetradheli.Text"), isHeli && gotAllParams, mand);
                AddBackstageViewPage(typeof(ConfigTradHeli4), rm.GetString("backstageViewPagetradheli.Text"), isHeli && gotAllParams, mand);
                AddBackstageViewPage(typeof(ConfigFrameType), rm.GetString("backstageViewPageframetype.Text"), isCopter && gotAllParams && !isCopter35plus, mand);
                AddBackstageViewPage(typeof(ConfigFrameClassType), rm.GetString("backstageViewPageframetype.Text"),
                    MainV2.comPort.MAV.param.ContainsKey("FRAME_CLASS") || isCopter && gotAllParams && isCopter35plus,
                    mand);
            }

            if (MainV2.DisplayConfiguration.displayAccelCalibration)
            {
                AddBackstageViewPage(typeof(ConfigAccelerometerCalibration), rm.GetString("backstageViewPageaccel.Text"), isConnected && gotAllParams, mand);
            }


            if (MainV2.DisplayConfiguration.displayCompassConfiguration)
            {
                if (MainV2.comPort.MAV.param.ContainsKey("COMPASS_PRIO1_ID"))
                    AddBackstageViewPage(typeof(ConfigHWCompass2), rm.GetString("backstageViewPagecompass.Text"),
                        isConnected && gotAllParams, mand);
                else
                    AddBackstageViewPage(typeof(ConfigHWCompass), rm.GetString("backstageViewPagecompass.Text"),
                        isConnected && gotAllParams, mand);
            }
            if (MainV2.DisplayConfiguration.displayRadioCalibration)
            {
                AddBackstageViewPage(typeof(ConfigRadioInput), rm.GetString("backstageViewPageradio.Text"), isConnected && gotAllParams, mand);
            }
            if (MainV2.DisplayConfiguration.displayServoOutput)
            {
                AddBackstageViewPage(typeof(ConfigRadioOutput), "Servo Output", isConnected && gotAllParams, mand);

            }
            if (MainV2.DisplayConfiguration.displaySerialPorts)
            {
                AddBackstageViewPage(typeof(ConfigSerial), rm.GetString("backstageViewPageSerial.Text"), isConnected && gotAllParams, mand);
            }
            if (MainV2.DisplayConfiguration.displayEscCalibration)
            {
                AddBackstageViewPage(typeof(ConfigESCCalibration), "ESC Calibration", isConnected && gotAllParams, mand);
            }
            if (MainV2.DisplayConfiguration.displayFlightModes)
            {
                AddBackstageViewPage(typeof(ConfigFlightModes), rm.GetString("backstageViewPageflmode.Text"), isConnected && gotAllParams, mand);
            }
            if (MainV2.DisplayConfiguration.displayFailSafe)
            {
                AddBackstageViewPage(typeof(ConfigFailSafe), rm.GetString("backstageViewPagefs.Text"), isConnected && gotAllParams, mand);
            }

            if ((isCopter || isQuadPlane) && MainV2.DisplayConfiguration.displayInitialParams)
            {
                AddBackstageViewPage(typeof(ConfigInitialParams), rm.GetString("backstageViewPageInitialParams.Text"), isConnected && gotAllParams, mand);
            }

            if (MainV2.DisplayConfiguration.displayHWIDs)
                AddBackstageViewPage(typeof(ConfigHWIDs), "HW ID", isConnected && gotAllParams, mand);

            var opt = AddBackstageViewPage(typeof(ConfigOptional), rm.GetString("backstageViewPageopt.Text"));
            if (MainV2.DisplayConfiguration.displayRTKInject)
            {
                var rtcmStr = rm.GetString("backstageViewPageSerialInjectGPS.Text");
                if(rtcmStr == null)
                    {
                    rtcmStr = "RTK/GPS Inject";
                }
                AddBackstageViewPage(typeof(ConfigSerialInjectGPS), rtcmStr, true, opt);
            }

            AddBackstageViewPage(typeof(ConfigCubeID), "CubeID Update",
    isConnected, opt);

            if (MainV2.DisplayConfiguration.displaySikRadio)
            {
                AddBackstageViewPage(typeof(Sikradio), rm.GetString("backstageViewPageSikradio.Text"), true, opt);
            }

            if (MainV2.DisplayConfiguration.displayADSB)
                AddBackstageViewPage(typeof(ConfigADSB), "ADSB", isConnected && gotAllParams, mand);

            if (MainV2.DisplayConfiguration.displayGPSOrder)
                AddBackstageViewPage(typeof(ConfigGPSOrder), "CAN GPS Order", isConnected && gotAllParams, opt);

            if (MainV2.DisplayConfiguration.displayBattMonitor)
            {
                AddBackstageViewPage(typeof(ConfigBatteryMonitoring), rm.GetString("backstageViewPagebatmon.Text"), isConnected && gotAllParams, opt);
                AddBackstageViewPage(typeof(ConfigBatteryMonitoring2), rm.GetString("backstageViewPageBatt2.Text"), isConnected && gotAllParams, opt);
            }
            if (MainV2.DisplayConfiguration.displayCAN)
            {
                //AddBackstageViewPage(typeof(ConfigHWCAN), "CAN", isConnected, opt);
                AddBackstageViewPage(typeof(ConfigDroneCAN), "DroneCAN/UAVCAN", true, opt);
            }
            if (MainV2.DisplayConfiguration.displayJoystick)
            {
                AddBackstageViewPage(typeof(Joystick.JoystickSetup), "Joystick", true, opt);
            }

            if (MainV2.DisplayConfiguration.displayCompassMotorCalib)
            {
                AddBackstageViewPage(typeof(ConfigCompassMot), rm.GetString("backstageViewPagecompassmot.Text"), isConnected && gotAllParams, opt);
            }
            if (MainV2.DisplayConfiguration.displayRangeFinder)
            {
                AddBackstageViewPage(typeof(ConfigHWRangeFinder), rm.GetString("backstageViewPagesonar.Text"), isConnected && gotAllParams, opt);
            }
            if (MainV2.DisplayConfiguration.displayAirSpeed)
            {
                AddBackstageViewPage(typeof(ConfigHWAirspeed), rm.GetString("backstageViewPageairspeed.Text"), isConnected && gotAllParams, opt);
            }
            if (MainV2.DisplayConfiguration.displayPx4Flow)
            {
                AddBackstageViewPage(typeof(ConfigHWPX4Flow), rm.GetString("backstageViewPagePX4Flow.Text"), true, opt);
            }
            if (MainV2.DisplayConfiguration.displayOpticalFlow)
            {
                AddBackstageViewPage(typeof(ConfigHWOptFlow), rm.GetString("backstageViewPageoptflow.Text"), isConnected && gotAllParams, opt);
            }
            if (MainV2.DisplayConfiguration.displayOsd)
            {
                AddBackstageViewPage(typeof(ConfigHWOSD), rm.GetString("backstageViewPageosd.Text"), isConnected && gotAllParams, opt);
            }
            if (MainV2.DisplayConfiguration.displayCameraGimbal)
            {
                AddBackstageViewPage(typeof(ConfigMount), rm.GetString("backstageViewPagegimbal.Text"), isConnected && gotAllParams, opt);
            }
            if (MainV2.DisplayConfiguration.displayAntennaTracker)
            {
                AddBackstageViewPage(typeof(ConfigAntennaTracker), rm.GetString("backstageViewPageAntTrack.Text"), isTracker, opt);
            }
            if (MainV2.DisplayConfiguration.displayMotorTest)
            {
                AddBackstageViewPage(typeof(ConfigMotorTest), rm.GetString("backstageViewPageMotorTest.Text"), isConnected && gotAllParams, opt);
            }
            if (MainV2.DisplayConfiguration.displayBluetooth)
            {
                AddBackstageViewPage(typeof(ConfigHWBT), rm.GetString("backstageViewPagehwbt.Text"), true, opt);
            }
            if (MainV2.DisplayConfiguration.displayParachute)
            {
                AddBackstageViewPage(typeof(ConfigHWParachute), rm.GetString("backstageViewPageParachute.Text"), isConnected && gotAllParams, opt);
            }
            if (MainV2.DisplayConfiguration.displayEsp)
            {
                AddBackstageViewPage(typeof(ConfigHWESP8266), rm.GetString("backstageViewPageESP.Text"), isConnected && gotAllParams, opt);
            }
            if (MainV2.DisplayConfiguration.displayAntennaTracker)
            {
                AddBackstageViewPage(typeof(Antenna.TrackerUI), "Antenna Tracker", true, opt);
            }
            if (MainV2.DisplayConfiguration.displayFFTSetup)
            {
                AddBackstageViewPage(typeof(ConfigFFT), "FFT Setup", isConnected && gotAllParams, opt);
            }

            if (MainV2.DisplayConfiguration.isAdvancedMode)
            {
                var adv = AddBackstageViewPage(typeof(ConfigAdvanced), "Advanced");

                if (MainV2.DisplayConfiguration.displayTerminal)
                {
                    AddBackstageViewPage(typeof(ConfigTerminal), "Terminal", true, adv);
                }

                if (MainV2.DisplayConfiguration.displayREPL)
                {
                    AddBackstageViewPage(typeof(ConfigREPL), "Script REPL", isConnected, adv);
                }
            }


            foreach (var item in pluginViewPages)
            {

                // go through all options
                if (item.options.HasFlag(pageOptions.isConnected) && !isConnected)
                    continue;
                if (item.options.HasFlag(pageOptions.isDisConnected) && !isDisConnected)
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

            var doneMsg = string.Format("InitialSetup.RebuildPages DONE. backstageView.Pages.Count={0}", backstageView.Pages.Count);
            log.Debug(doneMsg);
            MissionPlanner.Utilities.Profiler.Mark(doneMsg);

            // Phase 10p3 fork: after SoftReset cleared the menu buttons,
            // _items was repopulated via AddPage but the menu is still
            // empty. Without an explicit redraw, the Setup tab shows a
            // blank menu (and no content) unless lastpagename happens to
            // match a page (which fires ActivatePage -> DrawMenu).
            try { backstageView.RedrawMenu(); }
            catch (Exception ex) { log.Warn("InitialSetup.RebuildPages RedrawMenu: " + ex.Message); }

            // remeber last page accessed
            foreach (BackstageViewPage page in backstageView.Pages)
            {
                if (page.LinkText == lastpagename && page.Show)
                {
                    backstageView.ActivatePage(page);
                    break;
                }
            }

            ThemeManager.ApplyThemeTo(this);

            // Phase 10g fork: pre-construct every sub-page on the message
            // pump so subsequent clicks don't pay handle-creation cost.
            this.BeginInvoke((Action) delegate
            {
                try { backstageView.PrewarmAllAsync(); }
                catch (Exception ex) { log.Warn("HWConfig prewarm: " + ex.Message); }
            });
        }

        private void HardwareConfig_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (backstageView.SelectedPage != null)
                lastpagename = backstageView.SelectedPage.LinkText;

            backstageView.Close();
        }
    }
}