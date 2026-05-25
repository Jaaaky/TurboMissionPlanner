using MissionPlanner.Controls;
using MissionPlanner.Utilities;
using System;
using System.Linq;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews.ConfigurationView
{
    public partial class ConfigUserDefined : MyUserControl, IActivate, IDeactivate
    {
        public ConfigUserDefined()
        {
            InitializeComponent();

            if (Settings.Instance.ContainsKey("UserParams"))
                Options = Settings.Instance["UserParams"].Split(',');
        }

        public string[] Options { get; set; } = new string[]
        {
            "CH6_OPT",
            "CH7_OPT",
            "CH8_OPT",
            "CH9_OPT",
            "CH10_OPT",
            "CH11_OPT",
            "CH12_OPT",
            "CH13_OPT",
            "CH14_OPT",
            "CH15_OPT",
            "CH16_OPT",

            "RC6_OPTION",
            "RC7_OPTION",
            "RC8_OPTION",
            "RC9_OPTION",
            "RC10_OPTION",
            "RC11_OPTION",
            "RC12_OPTION",
            "RC13_OPTION",
            "RC14_OPTION",
            "RC15_OPTION",
            "RC16_OPTION"
        };

        // Phase 10n+10o fork: avoid rebuilding the control tree every Activate
        // (was the source of the "flashing for some seconds" report) BUT
        // invalidate the cache when the set of resolved params changes -
        // otherwise we lock in the pre-connect empty state ("opens instantly
        // but shows almost nothing").
        private string[] _builtForOptions;
        private string _builtForFirmware;
        private int _builtForResolvedCount = -1;

        public void LoadOptions()
        {
            tableLayoutPanel1.SuspendLayout();
            try
            {
                tableLayoutPanel1.Controls.Clear();
                tableLayoutPanel1.RowCount = 0;

                var button = new MyButton() { Text = "Modify", Name = "Modify" };
                button.Click += (o, e) =>
                {
                    var opts = Options.Aggregate((a, b) => a + "\r\n" + b);
                    InputBox.Show("Params", "Enter Param Names", ref opts, false, true);
                    Options = opts.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    Settings.Instance["UserParams"] = Options.Aggregate((a, b) => a.Trim() + "," + b.Trim());
                    _builtForOptions = null; // force rebuild
                    Activate();
                };
                tableLayoutPanel1.RowCount++;
                tableLayoutPanel1.Controls.Add(button);
                tableLayoutPanel1.SetColumnSpan(button, 2);

                var firmware = MainV2.comPort.MAV.cs.firmware.ToString();
                foreach (var option in Options)
                {
                    if (!MainV2.comPort.MAV.param.ContainsKey(option))
                        continue;
                    tableLayoutPanel1.RowCount++;
                    tableLayoutPanel1.Controls.Add(new Label() { Text = option, Name = option });
                    var options = ParameterMetaDataRepository.GetParameterOptionsInt(option, firmware);
                    if(options.Count == 0)
                    {
                        double min = 0,max = 0;
                        var opt = ParameterMetaDataRepository.GetParameterRange(option,ref min,ref max, firmware);
                        var num = new MavlinkNumericUpDown();
                        num.setup((float)min,(float)max,1,1,option, MainV2.comPort.MAV.param);
                        // Phase 10o fork: upstream BUG - the numeric was created
                        // but never added to the table; for params without enum
                        // options the user only saw a label, nothing editable.
                        tableLayoutPanel1.Controls.Add(num);
                    } else {
                        var cmb = new MavlinkComboBox();
                        tableLayoutPanel1.Controls.Add(cmb);
                        cmb.setup(options, option, MainV2.comPort.MAV.param);
                    }
                }

                // ResumeLayout(true) forces an immediate PerformLayout pass so
                // the controls actually arrange before the next paint. The
                // earlier ResumeLayout(false) + WM_SETREDRAW combo deferred
                // layout, which on Wine left the panel rendering empty until
                // a manual resize/repaint kicked it.
                tableLayoutPanel1.ResumeLayout(true);
            }
            catch (Exception ex) { log.Error(ex); throw; }
        }

        private static readonly log4net.ILog log =
            log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public void Activate()
        {
            // Phase 10o fork: cache key includes COUNT OF RESOLVED OPTIONS
            // (params actually present on the vehicle right now). If the user
            // opened this tab BEFORE the param download completed, we built
            // with zero resolved options; the next Activate would skip the
            // rebuild and the tab would forever show only the Modify button.
            var firmware = MainV2.comPort.MAV.cs.firmware.ToString();
            int resolved = 0;
            foreach (var opt in Options)
                if (MainV2.comPort.MAV.param.ContainsKey(opt)) resolved++;

            if (_builtForOptions != null
                && _builtForOptions.Length == Options.Length
                && _builtForOptions.SequenceEqual(Options)
                && _builtForFirmware == firmware
                && _builtForResolvedCount == resolved
                && tableLayoutPanel1.Controls.Count > 0)
            {
                return;
            }
            LoadOptions();
            _builtForOptions = (string[])Options.Clone();
            _builtForFirmware = firmware;
            _builtForResolvedCount = resolved;
        }

        public void Deactivate()
        {

        }
    }
}
