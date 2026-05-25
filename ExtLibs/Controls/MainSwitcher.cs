using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MissionPlanner.Controls
{
    public partial class MainSwitcher : IDisposable
    {
        public delegate void ThemeManager(Control ctl);

        public static event ThemeManager ApplyTheme;

        public delegate void TrackingEventHandler(string page, string title);

        public static event TrackingEventHandler Tracking;

        public List<Screen> screens = new List<Screen>();
        public Screen current;
        UserControl MainControl = new UserControl();

        public int Width
        {
            get { return MainControl.Width; }
        }

        public int Height
        {
            get { return MainControl.Height; }
        }

        public Control.ControlCollection Controls
        {
            get { return MainControl.Controls; }
        }

        public MainSwitcher(Control Parent)
        {
            MainControl.Dock = DockStyle.Fill;

            Parent.Controls.Add(MainControl);
        }

        public void AddScreen(Screen Screen)
        {
            if (Screen == null)
                return;

            // add to list - remove existing
            if (screens.Any(a => a.Name == Screen.Name))
                screens.Remove(screens.First(a => a.Name == Screen.Name));
            screens.Add(Screen);

            // hide it
            if (Screen.Control != null)
                Screen.Control.Visible = false;
        }

        public void Reload()
        {
            ShowScreen(current.Name);
        }

        // Phase 10h fork: hidden off-screen Form that hosts preload-target
        // screens so we can fire their Form Load event without showing them
        // to the user. Control.CreateControl() only triggers OnLoad when the
        // control is Visible AND parented to a visible container; we need
        // both. A regular hidden Panel doesn't work (parent not "visible").
        private Form _preloadHost;
        private Form GetPreloadHost()
        {
            if (_preloadHost != null && !_preloadHost.IsDisposed) return _preloadHost;
            _preloadHost = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                Size = new Size(800, 600),
                ShowInTaskbar = false,
                Opacity = 0.0,
                Visible = false,
            };
            // Show off-screen + invisible so SetVisibleCore fires CreateControl
            // on its children when we add them.
            try { _preloadHost.Show(); _preloadHost.Hide(); } catch { }
            _preloadHost.Visible = true; // needed for child CreateControl path
            return _preloadHost;
        }

        /// <summary>
        /// Phase 10h fork: pre-construct a screen's Control + force handle
        /// cascade + force Form Load event off the user-click path. Hosted
        /// in an invisible off-screen Form so OnLoad fires.
        ///
        /// Returns true if a new Control was created; false if it already
        /// existed (or screen not found / not persistent).
        /// </summary>
        private static void ForceCreateControlRecursive(Control c)
        {
            if (c == null) return;
            try { c.CreateControl(); } catch { }
            try { var _ = c.Handle; } catch { }
            foreach (Control child in c.Controls)
                ForceCreateControlRecursive(child);
        }

        public bool PreloadScreen(string name)
        {
            Screen s;
            try { s = screens.SingleOrDefault(sc => sc.Name == name); }
            catch { return false; }
            if (s == null) return false;
            if (s.Control != null && !s.Control.IsDisposed) return false;
            if (!s.Persistent) return false; // pointless to preload a non-persistent screen
            try
            {
                CreateControl(s);
                if (s.Control == null) return false;
                var host = GetPreloadHost();
                s.Control.Visible = true; // required for CreateControl -> OnLoad
                s.Control.Dock = DockStyle.Fill;
                if (!host.Controls.Contains(s.Control))
                    host.Controls.Add(s.Control);
                // Force handle + load event chain on UI thread RIGHT NOW.
                var _ = s.Control.Handle;
                try { s.Control.CreateControl(); } catch { }
                try { s.Control.PerformLayout(); } catch { }
                if (s.Control is IActivate)
                    try { ((IActivate) s.Control).Activate(); } catch { }
                // Do NOT pump messages here: Application.DoEvents inside the
                // preload was pumping the entire backstage prewarm chain
                // (16+ seconds of queued work), blocking startup. The
                // ActivatePage queued by SoftwareConfig_Load will run on its
                // own when the message pump returns to idle.
                if (s.Control is IDeactivate)
                    try { ((IDeactivate) s.Control).Deactivate(); } catch { }
                // Remove from host so ShowScreen can re-parent into MainControl
                // without complaints. Handle stays valid; controls remember
                // their state. ShowScreen will set Visible appropriately.
                try { host.Controls.Remove(s.Control); } catch { }
                s.Control.Visible = false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        void CreateControl(Screen current)
        {
            Type type = current.Type;

            // create new instance on gui thread
            if (MainControl.InvokeRequired)
            {
                MainControl.Invoke((MethodInvoker) delegate
                {
                    try
                    {
                        current.Control = (MyUserControl) Activator.CreateInstance(type);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(
                            "Unable to invoke create control " + current.Name + " of type " + current.Type, ex);
                    }
                });
            }
            else
            {
                try
                {
                    current.Control = (MyUserControl) Activator.CreateInstance(type);
                }
                catch
                {
                    try
                    {
                        current.Control = (MyUserControl) Activator.CreateInstance(type);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception("Unable to create control " + current.Name + " of type " + current.Type,
                            ex);
                    }
                }
            }

            // set the next new instance as not visible
            current.Control.Visible = false;
        }

        public void ShowScreen(string name)
        {
            if (current != null && current.Control != null)
            {
                // hide current screen
                current.Visible = false;

                // remove reference
                MainControl.Controls.Remove(current.Control);

                if (current.Control is IDeactivate)
                {
                    ((IDeactivate) (current.Control)).Deactivate();
                }

                // check if we need to remove the current control
                if (!current.Persistent)
                {
                    // cleanup
                    current.Control.Close();

                    current.Control.Dispose();

                    current.Control = null;

                    GC.Collect();
                }
            }

            if (name == "")
                return;

            if (!screens.Any(s => s.Name == name))
                return;

            // find next screen
            Screen nextscreen = screens.Single(s => s.Name == name);

            // screen control is null, create it
            if (nextscreen.Control == null || nextscreen.Control.IsDisposed)
                CreateControl(nextscreen);

            MainControl.SuspendLayout();
            nextscreen.Control.SuspendLayout();

            nextscreen.Control.Location = new Point(0, 0);

            nextscreen.Control.AutoScaleMode = AutoScaleMode.None;

            nextscreen.Control.Size = MainControl.Size;

            nextscreen.Control.Dock = DockStyle.Fill;

            Tracking?.Invoke(nextscreen.Control.GetType().ToString(), name);

            if (nextscreen.Control is IActivate)
            {
                ((IActivate) (nextscreen.Control)).Activate();
            }

            if (ApplyTheme != null)
                ApplyTheme(nextscreen.Control);

            if (MainControl.InvokeRequired)
            {
                MainControl.Invoke((MethodInvoker) delegate
                {
                    MainControl.Controls.Add(nextscreen.Control);
                    nextscreen.Control.ResumeLayout();
                    MainControl.ResumeLayout();
                });
            }
            else
            {
                MainControl.Controls.Add(nextscreen.Control);
                nextscreen.Control.ResumeLayout();
                MainControl.ResumeLayout();
            }

            nextscreen.Control.Refresh();

            nextscreen.Visible = true;

            current = nextscreen;

            current.Control.Focus();
        }

        public class Screen
        {
            public string Name;
            public MyUserControl Control;
            public Type Type;

            public bool Visible
            {
                get
                {
                    if (Control == null)
                        return false;
                    return Control.Visible;
                }
                set
                {
                    try
                    {
                        Control.SuspendLayout();
                        Control.Visible = value;
                        Control.ResumeLayout();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                }
            }

            public bool Persistent;

            public Screen(string Name, MyUserControl Control, bool Persistent = false)
            {
                this.Name = Name;
                this.Control = Control;
                this.Persistent = Persistent;
                if (Control == null)
                    return;
                this.Type = Control.GetType();
            }

            public Screen(string Name, Type Type, bool Persistent = false)
            {
                this.Name = Name;
                this.Type = Type;
                this.Persistent = Persistent;
            }
        }

        public void Dispose()
        {
            if (current != null && current.Control != null && current.Control is IDeactivate)
            {
                ((IDeactivate) (current.Control)).Deactivate();
            }

            foreach (var item in screens)
            {
                try
                {
                    Console.WriteLine("MainSwitcher dispose " + item?.Name);
                    if (item?.Control != null)
                    {
                        item.Control.Close();
                        item.Control.Dispose();
                    }
                }
                catch
                {
                }
            }

            MainControl.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}