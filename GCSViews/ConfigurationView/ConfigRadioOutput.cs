using MissionPlanner.Controls;
using MissionPlanner.Utilities;
using System;
using System.Linq;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews.ConfigurationView
{
    public partial class ConfigRadioOutput : MyUserControl, IActivate, IDeactivate
    {
        public ConfigRadioOutput()
        {
            InitializeComponent();

            bindingSource1.DataSource = typeof(CurrentState);

            var num_servos = 16;

            // See if 32 servo support is enabled
            if (MainV2.comPort.MAV.param.ContainsKey("SERVO_32_ENABLE") &&
                    (MainV2.comPort.MAV.param["SERVO_32_ENABLE"].Value > 0))
            {
                num_servos = 32;
            }

            // Phase 8 fix: warm metadata cache before per-servo loop.
            var firmware = MainV2.comPort.MAV.cs.firmware.ToString();
            ParameterMetaDataRepository.GetParameterOptionsInt("SERVO1_FUNCTION", firmware);

            // Phase 9 fork: suspend the ACTUAL container (tableLayoutPanel1)
            // we are adding into, not the UserControl. Upstream SuspendLayout
            // on `this` didn't stop the TableLayoutPanel from reflowing on
            // every Controls.Add -- with AutoSize=true that's O(N^2) over
            // 16-32 servos * 7 controls. Pre-grow RowCount so the table
            // doesn't auto-extend mid-loop either.
            tableLayoutPanel1.SuspendLayout();
            try
            {
                if (tableLayoutPanel1.RowCount < num_servos + 1)
                {
                    tableLayoutPanel1.RowCount = num_servos + 1;
                    while (tableLayoutPanel1.RowStyles.Count < num_servos + 1)
                        tableLayoutPanel1.RowStyles.Add(new RowStyle());
                }
                foreach (var i in Enumerable.Range(1, num_servos))
                {
                    setup(i);
                }
            }
            finally
            {
                tableLayoutPanel1.ResumeLayout(false);
                tableLayoutPanel1.PerformLayout();
            }
        }

        private void setup(int servono)
        {
            var servo = String.Format("SERVO{0}", servono);

            var label = new Label()
                {Text = servono.ToString(), AutoSize = true, TextAlign = System.Drawing.ContentAlignment.MiddleCenter};
            var bAR1 = new HorizontalProgressBar2()
            {
                Minimum = 800, Maximum = 2200, Value = 1500, DrawLabel = true, Name = "BAR" + servono,
                Dock = DockStyle.Fill
            };
            var rev1 = new MissionPlanner.Controls.MavlinkCheckBox()
                {Enabled = false, Dock = DockStyle.Fill, AutoSize = true};
            var func1 = new MavlinkComboBox()
            { Enabled = false, Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
            var min1 = new MavlinkNumericUpDown() { Minimum = 800, Maximum = 2200, Value = 1500, Enabled = false, Width = 50 };
            var trim1 = new MavlinkNumericUpDown() { Minimum = 800, Maximum = 2200, Value = 1500, Enabled = false, Width = 50 };
            var max1 = new MavlinkNumericUpDown() { Minimum = 800, Maximum = 2200, Value = 1500, Enabled = false, Width = 50 };

            this.tableLayoutPanel1.Controls.Add(label, 0, servono);
            this.tableLayoutPanel1.Controls.Add(bAR1, 1, servono);
            this.tableLayoutPanel1.Controls.Add(rev1, 2, servono);
            this.tableLayoutPanel1.Controls.Add(func1, 3, servono);
            this.tableLayoutPanel1.Controls.Add(min1, 4, servono);
            this.tableLayoutPanel1.Controls.Add(trim1, 5, servono);
            this.tableLayoutPanel1.Controls.Add(max1, 6, servono);

            bAR1.DataBindings.Add("Value", bindingSource1, "ch" + servono + "out");
            rev1.setup(1, 0, servo + "_REVERSED", MainV2.comPort.MAV.param);
            func1.setup(ParameterMetaDataRepository.GetParameterOptionsInt(servo + "_FUNCTION",
                    MainV2.comPort.MAV.cs.firmware.ToString()), servo + "_FUNCTION", MainV2.comPort.MAV.param);
            min1.setup(800, 2200, 1, 1, servo + "_MIN", MainV2.comPort.MAV.param);
            trim1.setup(800, 2200, 1, 1, servo + "_TRIM", MainV2.comPort.MAV.param);
            max1.setup(800, 2200, 1, 1, servo + "_MAX", MainV2.comPort.MAV.param);
        }

        public void Activate()
        {
            timer1.Start();
        }

        public void Deactivate()
        {
            timer1.Stop();
        }


        private void timer1_Tick(object sender, EventArgs e)
        {
            try
            {
                MainV2.comPort.MAV.cs.UpdateCurrentSettings(bindingSource1.UpdateDataSource(MainV2.comPort.MAV.cs));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }
    }
}