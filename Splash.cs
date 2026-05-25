using System;
using System.Reflection;
using System.Windows.Forms;

namespace MissionPlanner
{
    public partial class Splash : Form
    {
        public Splash()
        {
            // Phase 9 fork: load + apply IBM Plex Sans before designer fonts.
            try
            {
                MissionPlanner.Utilities.AppFonts.Load();
                if (MissionPlanner.Utilities.AppFonts.PlexSans != null)
                    this.Font = MissionPlanner.Utilities.AppFonts.Make(this.Font?.Size ?? 8.25f);
            }
            catch { }

            InitializeComponent();

            string strVersion = typeof(Splash).GetType().Assembly.GetName().Version.ToString();

            TXT_version.Text = "Version: " + Application.ProductVersion; // +" Build " + strVersion;

            Console.WriteLine(strVersion);

            if (Program.Logo != null)
            {
                pictureBox1.BackgroundImage = MissionPlanner.Properties.Resources.bgdark;
                pictureBox1.Image = Program.Logo;
                pictureBox1.Visible = true;
            }

            Console.WriteLine("Splash .ctor");
        }
    }
}