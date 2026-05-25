using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using MissionPlanner.Plugin;
using MissionPlanner.Utilities;

namespace MissionPlanner.Controls
{
    public partial class PluginUI : Form
    {
        // Phase 9 fork: per-plugin description + impact-if-disabled. Lookup
        // by lowercase DLL filename or synthetic key (e.g. "simulation").
        // Add new entries as plugins are added.
        private static readonly Dictionary<string, (string Desc, string Impact)> PluginInfo =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["terrainmakerplugin.dll"] = (
                    "Generates a 3D terrain mesh overlay around the home location for mission planning.",
                    "Loses the 3D terrain preview in FlightPlanner. Mission planning still works."),
                ["facemap.dll"] = (
                    "Image-based face/object detection on still photos. Niche workflow.",
                    "Removes face detection menu. Unused by the vast majority of users."),
                ["bulb.dll"] = (
                    "Controls WS281x LED strips connected to the autopilot for status indication.",
                    "LED Bulb config tab disappears. No effect unless you actually have an LED strip wired up."),
                ["trackerhome.dll"] = (
                    "Sends a HOME_POSITION mavlink message to an Antenna Tracker using your GCS-attached GPS.",
                    "Antenna tracker home-position auto-update breaks. Manual home set still works."),
                ["mavlinkmessageplugin.dll"] = (
                    "Adds a generic 'send any MAVLink message' tab for advanced debugging.",
                    "Loses ability to inject arbitrary MAVLink messages from the GUI. Niche."),
                ["missionplanner.stats.dll"] = (
                    "Local usage statistics: flight-hours, distance, etc.",
                    "Statistics view disappears. No telemetry leaves your machine either way."),
                ["missionplanner.simplegrid.dll"] = (
                    "Survey-grid generator (simple polygon-to-photos pattern).",
                    "Loses the Simple Grid menu in FlightPlanner. Full Grid v2 still available."),
                ["osdconfigurator.dll"] = (
                    "On-Screen Display configurator for compatible VTX/OSD chips.",
                    "OSD layout editor disappears. Only matters if you fly with an OSD."),
                ["opendroneid.dll"] = (
                    "Remote ID broadcasting for regulatory compliance (US/EU rules).",
                    "Disables Remote ID transmit. Required by law in some jurisdictions."),
                ["shortcuts.dll"] = (
                    "Customisable keyboard shortcuts for common menu actions.",
                    "Loses the Shortcuts config tab; default Ctrl+P / Ctrl+F still work."),
                ["testplugin.dll"] = (
                    "Developer scaffold for plugin authors. Not user-facing.",
                    "No effect. Safe to leave disabled."),
                ["extguided.dll"] = (
                    "Extended GUIDED-mode mission helpers (waypoint pushing).",
                    "Loses some advanced GUIDED-mode buttons. Standard FlightData GUIDED still works."),
                ["tlogthumbnailhandler.dll"] = (
                    "Windows Explorer thumbnail handler for .tlog telemetry files.",
                    "Tlog files lose thumbnail previews in File Explorer. App itself unaffected."),
                ["__simulation__"] = (
                    "SITL (Software In The Loop) tab: runs the autopilot firmware in simulation.",
                    "Removes the Simulation tab from the top bar. Saves ~150ms startup. Connect-to-vehicle workflow unaffected."),

                // Phase 10c fork: .cs Roslyn-compiled script examples.
                // Each entry summarises what the script actually does.
                ["example.cs"] = (
                    "Minimal hello-world script shell. Demonstrates plugin event hookup.",
                    "No user-visible loss. Demo only."),
                ["example-watchbutton.cs"] = (
                    "Logs BUTTON_CHANGE MAVLink messages to console on reception.",
                    "Loses console log of stick / aux switch button events. Demo-grade."),
                ["example2.cs"] = (
                    "Empty placeholder file (no implementation).",
                    "No effect."),
                ["example2-menu.cs"] = (
                    "Adds 'Fix mission top/bottom' menu item that inserts/removes servo waypoint commands.",
                    "Niche mission-edit shortcut disappears."),
                ["example3.cs"] = (
                    "Empty placeholder file (no implementation).",
                    "No effect."),
                ["example3-fencedist.cs"] = (
                    "Geofence-distance heatmap overlay on the map.",
                    "Loses geofence proximity colour overlay. Geofence still enforced server-side."),
                ["example4.cs"] = (
                    "Empty placeholder file (no implementation).",
                    "No effect."),
                ["example4-herelink.cs"] = (
                    "Herelink camera/video control via GStreamer (v1/v2 streams, baud reset).",
                    "Only useful with a Herelink ground unit. Disable otherwise."),
                ["example5.cs"] = (
                    "Empty placeholder file (no implementation).",
                    "No effect."),
                ["example5-latencytracker.cs"] = (
                    "Comms latency tracker with visual indicator + CSV logging (red/yellow/green).",
                    "Loses on-screen link-latency badge. Tlogs still record traffic."),
                ["example6.cs"] = (
                    "Empty placeholder file (no implementation).",
                    "No effect."),
                ["example6-mapicondesc.cs"] = (
                    "Customises map-icon hover/tooltip text via template-string placeholders.",
                    "Map icons revert to default name only. Niche."),
                ["example7.cs"] = (
                    "Empty placeholder file (no implementation).",
                    "No effect."),
                ["example7-canrtcm.cs"] = (
                    "Extracts RTCM correction data from DroneCAN .gpsbase log files.",
                    "Only useful for RTK-base-station log post-processing."),
                ["example8.cs"] = (
                    "Empty placeholder file (no implementation).",
                    "No effect."),
                ["example8-modechange.cs"] = (
                    "Flight-mode dropdown in main menu, synced with vehicle mode.",
                    "Use the FlightData mode buttons instead. Functionally redundant."),
                ["example9-hudonoff.cs"] = (
                    "Toggle individual HUD elements (heading, speed, alt, GPS, battery, etc.).",
                    "HUD shows full set always. Niche."),
                ["example10-canlogfile.cs"] = (
                    "DroneCAN log file parser; dumps frames and messages to text.",
                    "Only useful for DroneCAN log post-processing."),
                ["example11-trace.cs"] = (
                    "Invokes Program.TraceMe() diagnostic tracing function.",
                    "Developer-only. No user impact."),
                ["example12-forwarding.cs"] = (
                    "TCP forwarding proxy on port 14550 mirroring MAVLink to multiple consumers.",
                    "Loses ability to mirror live MAVLink to a second app via TCP. mavproxy/external forwarders unaffected."),
                ["example13-herelink2.cs"] = (
                    "Alternative Herelink control: requests camera info + initiates RX pairing.",
                    "Only useful with Herelink hardware."),
                ["example14-mass.cs"] = (
                    "Parallel multi-vehicle arm/disarm, mode change, takeoff, guided control.",
                    "Loses fleet-style mass-action buttons. Single-vehicle workflow unaffected."),
                ["example15-leds.cs"] = (
                    "LED control menu (red, green, blue, white, black, rainbow animations).",
                    "Loses LED command menu. Only useful with addressable LEDs onboard."),
                ["example16-donate.cs"] = (
                    "Adds a donate toolbar button linking to UNICEF Ukraine appeal.",
                    "Removes the donate button. App functionality unaffected."),
                ["example17-menuremove.cs"] = (
                    "Strips map context-menu to a whitelist (go here, fly to coords).",
                    "Restores the full upstream context menu. Mostly an aesthetic preference."),
                ["example18-externalapi.cs"] = (
                    "DTLS-PSK authenticated link to an external drone-telemetry API server.",
                    "Loses external-API push. No outbound traffic if disabled (privacy positive)."),
                ["example19-multiforward.cs"] = (
                    "Forwards HEARTBEAT, GLOBAL_POSITION_INT, ATTITUDE across multiple links.",
                    "Loses cross-link telemetry mirror. Single-link operation unaffected."),
                ["example20-multiplepositions.cs"] = (
                    "Plots multi-source positions on the map (GPS1/2, AHRS2/3, SimState, etc.).",
                    "Map shows only the primary fix. Diagnostic-only feature."),
                ["example21-persistentsimple.cs"] = (
                    "Persistent floating panel with Auto/Loiter/RTL mode buttons above the tabs.",
                    "Use the standard mode buttons instead."),
                ["example22-payloadconfig.cs"] = (
                    "Config tab with checkbox-based payload-parameter enable/disable management.",
                    "Edit payload params directly via the Full Parameter List."),
                ["example23-switch.cs"] = (
                    "CubeLan 8-port managed-switch I2C config (COS, EEE, VLAN).",
                    "Only useful with CubeLan switch hardware."),
                ["anonymizebinlogplugin.cs"] = (
                    "Anonymises ArduPilot .bin logs by applying random lat/lng offsets.",
                    "Loses one-click log-anonymisation menu. Useful before sharing logs publicly."),
                ["generator.cs"] = (
                    "Generator-status gauge: voltage, power, runtime, maintenance hours.",
                    "Loses generator gauge. Only relevant with a hybrid/generator power source."),
            };

        public PluginUI()
        {
            InitializeComponent();
            AddDescriptionColumn();
            PopulateGridView();
            PerformLayout();
            labelWarning.Visible = Plugin.PluginLoader.bRestartRequired;
        }

        private void AddDescriptionColumn()
        {
            if (dgvPlugins.Columns.Contains("pluginDesc")) return;
            var desc = new DataGridViewTextBoxColumn
            {
                Name = "pluginDesc",
                HeaderText = "Description",
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 200,
                DefaultCellStyle = { WrapMode = DataGridViewTriState.True }
            };
            dgvPlugins.Columns.Add(desc);
        }

        private void ApplyInfo(DataGridViewRow row, string dllName)
        {
            if (PluginInfo.TryGetValue(dllName, out var info))
            {
                row.Cells["pluginDesc"].Value = info.Desc + "  -- If disabled: " + info.Impact;
                row.Cells["pluginDesc"].ToolTipText = info.Desc + "\n\nImpact if disabled:\n" + info.Impact;
            }
            else
            {
                row.Cells["pluginDesc"].Value =
                    "(no description on file; if you do not recognise this plugin it is probably safe to leave disabled)";
            }
        }

        private void PopulateGridView()
        {
            string path = Settings.GetRunningDirectory() + "plugins" +
                          Path.DirectorySeparatorChar;

            dgvPlugins.Rows.Clear();
            //First iterate through loaded plugins.
            //Not enabled but loaded plugins are Orange, loaded and enabed are Red
            foreach (Plugin.Plugin p in Plugin.PluginLoader.Plugins)
            {
                int rowindex = dgvPlugins.Rows.Add();
                var row = dgvPlugins.Rows[rowindex];
                row.Cells["pluginName"].Value = p.Name;
                row.Cells["pluginAuthor"].Value = p.Author;
                row.Cells["pluginVersion"].Value = p.Version;
                row.Cells["pluginDll"].Value = p.FileName.ToLower();
                bool bEnabled = !Plugin.PluginLoader.DisabledPluginNames.Contains(p.FileName, StringComparer.OrdinalIgnoreCase);
                row.Cells["pluginEnabled"].Value = bEnabled;
                if (bEnabled) row.DefaultCellStyle.BackColor = Color.Green;
                else row.DefaultCellStyle.BackColor = Color.DarkOrange;
                ApplyInfo(row, p.FileName.ToLower());
            }

            //Go through names from config.xml, but do not display the ones that are loaded (Those are already displayed in Orange from previous iterate)
            foreach (String s in Plugin.PluginLoader.DisabledPluginNames)
            {
                //Iterate through loaded plugins, so do not add disabled but loaded plugins
                bool isLoaded = false;

                foreach (Plugin.Plugin p in Plugin.PluginLoader.Plugins)
                    if (p.FileName.ToLower().Contains(s)) isLoaded = true;

                if (File.Exists(path + s) && !isLoaded)
                {
                    int rowindex = dgvPlugins.Rows.Add();
                    var row = dgvPlugins.Rows[rowindex];
                    row.Cells["pluginName"].Value = "Not loaded";
                    row.Cells["pluginAuthor"].Value = "--";
                    row.Cells["pluginVersion"].Value = "--";
                    row.Cells["pluginDll"].Value = s;
                    row.Cells["pluginEnabled"].Value = false;
                    row.DefaultCellStyle.BackColor = Color.DarkRed;
                    ApplyInfo(row, s);
                }
            }

            // Phase 9 fork: synthesise a row for the Simulation tab so users
            // can opt out of the SITL view at startup (takes ~150ms to ctor).
            // Backed by the "disable_simulation" Setting; honoured by MainV2
            // when registering MainSwitcher screens.
            {
                int rowindex = dgvPlugins.Rows.Add();
                var row = dgvPlugins.Rows[rowindex];
                row.Cells["pluginName"].Value = "Simulation (SITL)";
                row.Cells["pluginAuthor"].Value = "ArduPilot";
                row.Cells["pluginVersion"].Value = "built-in";
                row.Cells["pluginDll"].Value = "__simulation__";
                bool simEnabled = !Settings.Instance.GetBoolean("disable_simulation", true);
                row.Cells["pluginEnabled"].Value = simEnabled;
                row.DefaultCellStyle.BackColor = simEnabled ? Color.Green : Color.DarkOrange;
                ApplyInfo(row, "__simulation__");
            }
        }

        //Update the <DisabledPlugins> settings in config.xml
        private void UpdateDisabledPlugins()
        {
            Plugin.PluginLoader.DisabledPluginNames.Clear();
            foreach (DataGridViewRow r in dgvPlugins.Rows)
            {
                var dll = r.Cells["pluginDll"].Value?.ToString().ToLower();
                if (dll == null) continue;
                bool enabled = (Boolean)(r.Cells["pluginEnabled"].Value ?? false);

                // Phase 9 fork: the synthetic Simulation row is backed by the
                // disable_simulation Setting, not the DisabledPlugins list.
                if (dll == "__simulation__")
                {
                    Settings.Instance["disable_simulation"] = (!enabled).ToString();
                    continue;
                }

                if (!enabled)
                    Plugin.PluginLoader.DisabledPluginNames.Add(dll);
            }

            if (Plugin.PluginLoader.DisabledPluginNames.Count > 0)
                Settings.Instance.SetList("DisabledPlugins", Plugin.PluginLoader.DisabledPluginNames);
            else
                Settings.Instance.Remove("DisabledPlugins");
        }

        private void bSave_Click(object sender, EventArgs e)
        {
            UpdateDisabledPlugins();
            Plugin.PluginLoader.bRestartRequired = true;
            this.Close();
        }

        private void ResizeFormForDataGrid()
        {
            this.SuspendLayout();
            Control vertical = dgvPlugins.Controls[1];
            dgvPlugins.Width = dgvPlugins.PreferredSize.Width - vertical.Width + 1;
            this.Width = dgvPlugins.Width + 15;
            this.Height = dgvPlugins.PreferredSize.Height + 80;
            //this.Height = dgvPlugins.RowCount * 30 + 40;
            this.ResumeLayout(true);
        }

        private void dgvPlugins_RowHeadersWidthChanged(object sender, EventArgs e)
        {
            ResizeFormForDataGrid();
        }

        private void dgvPlugins_SelectionChanged(object sender, EventArgs e)
        {
            int selectedRow = dgvPlugins.CurrentCell.RowIndex;
            string r = (string)dgvPlugins.Rows[selectedRow].Cells["pluginName"].Value;
        }

        private void btnLoadPlugin_Click(object sender, EventArgs e)
        {

            string path = Settings.GetRunningDirectory() + "plugins" +
              Path.DirectorySeparatorChar;

            int selectedRow = dgvPlugins.CurrentCell.RowIndex;
            string filename = (string)dgvPlugins.Rows[selectedRow].Cells["pluginDLL"].Value;
            //Remove from Disabled list to allow load
            Plugin.PluginLoader.DisabledPluginNames.Remove(filename);
            Plugin.PluginLoader.Load(path + filename);
            //Add back to the Disabled list, since we did not enabled it, just loaded
            Plugin.PluginLoader.DisabledPluginNames.Add(filename);

            PopulateGridView();
            dgvPlugins.CurrentCell = dgvPlugins.Rows[0].Cells[0];
            dgvPlugins.Rows[0].Selected = true;
            ResizeFormForDataGrid();
        }

        private void PluginUI_Shown(object sender, EventArgs e)
        {
            ResizeFormForDataGrid();
        }

        private void but_errors_Click(object sender, EventArgs e)
        {
            var msg = PluginLoader.ErrorInfo.Aggregate("", (s, pair) => s + pair.Value + "\n");
            if (msg == "")
                CustomMessageBox.Show("No Errors", "Errors");
            else
                CustomMessageBox.Show(msg, "Errors");
        }
    }
}
