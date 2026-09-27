using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using MissionPlanner.Controls;

namespace MissionPlanner.GCSViews.ConfigurationView
{
    public partial class ConfigParamLoading : UserControl, IActivate, IDeactivate
    {
        public bool gotAllParams
        {
            get
            {
                // Fork: same rule as InitialSetup/SoftwareConfig. With
                // rep == 0 the old check said "done" while the hosts said
                // "not done", so Reload rebuilt the Loading page at 10 Hz.
                int rx = MainV2.comPort.MAV.param.TotalReceived;
                int rep = MainV2.comPort.MAV.param.TotalReported;
                return rep > 0 && rx >= rep;
            }
        }

        public ConfigParamLoading()
        {
            InitializeComponent();
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
            if (gotAllParams)
            {
                // Fork: stop first so a slow or re-entrant Reload cannot
                // fire a second one from this page.
                timer1.Stop();
                MainV2.View.Reload();
            }
        }

        private void but_forceparams_Click(object sender, EventArgs e)
        {
            MainV2.comPort.getParamList();
        }
    }
}
