using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using log4net;
using MissionPlanner.Controls.BackstageView;

namespace MissionPlanner.Controls.BackstageView
{
    /// <summary>
    /// A Control to somewhat emulate the 'backstage view' as in MS Office 2010
    /// </summary>
    /// <remarks>
    /// 'Tabs' are added as a control in a <see cref="BackstageViewPage"/>
    /// </remarks>
    public partial class BackstageView : MyUserControl, IContainerControl
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private Color _buttonsAreaBgColor = Color.White;
        private Color _buttonsAreaPencilColor = Color.DarkGray;
        private Color _selectedTextColor = Color.White;
        private Color _unSelectedTextColor = Color.Gray;
        private Color _highlightColor1 = SystemColors.Highlight;
        private Color _highlightColor2 = SystemColors.MenuHighlight;

        private readonly BackstageViewCollection _items = new BackstageViewCollection();
        private BackstageViewPage _activePage;

        private const int ButtonHeight = 30;

        private List<BackstageViewPage> expanded = new List<BackstageViewPage>();

        public BackstageViewPage SelectedPage { get { return _activePage; } }

        public delegate void TrackingEventHandler(string page, string title);
        public static event TrackingEventHandler Tracking;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        public BackstageViewCollection Pages { get { return _items; } }

        private bool _prewarmStarted;
        private Panel _prewarmHost;

        /// <summary>
        /// Phase 10g fork: idle-driven preconstruction of every registered
        /// BackstageViewPage. Forces ctor + handle creation + initial
        /// PerformLayout off the user-click path. Each page is warmed via
        /// one BeginInvoke per UI message-pump cycle, so user input always
        /// preempts; a click on an unwarmed page costs the same as before,
        /// but every page warmed by then opens instantly.
        /// </summary>
        public void PrewarmAllAsync()
        {
            if (_prewarmStarted) return;
            if (!this.IsHandleCreated || this.IsDisposed) return;
            _prewarmStarted = true;

            // Detached invisible host so we can force handle creation on each
            // Page without making it visible inside pnlPages. Parented to the
            // BackstageView itself but kept invisible + zero-size so layout
            // doesn't shift.
            _prewarmHost = new Panel { Visible = false, Width = 0, Height = 0 };
            this.Controls.Add(_prewarmHost);

            var queue = new System.Collections.Generic.Queue<BackstageViewPage>();
            foreach (BackstageViewPage p in _items) queue.Enqueue(p);
            ScheduleNextPrewarm(queue);
        }

        private void ScheduleNextPrewarm(System.Collections.Generic.Queue<BackstageViewPage> q)
        {
            if (q.Count == 0)
            {
                try { _prewarmHost?.Dispose(); } catch { }
                _prewarmHost = null;
                MissionPlanner.Utilities.Profiler.Mark("BackstageView.Prewarm:done");
                return;
            }
            if (!this.IsHandleCreated || this.IsDisposed) return;
            try
            {
                this.BeginInvoke((Action) delegate { PrewarmOne(q); });
            }
            catch { }
        }

        private void PrewarmOne(System.Collections.Generic.Queue<BackstageViewPage> q)
        {
            if (q.Count == 0 || _prewarmHost == null) return;
            var p = q.Dequeue();
            try
            {
                if (!p.isPageCreated)
                {
                    MissionPlanner.Utilities.Profiler.Mark("BackstageView.Prewarm:" + (p.LinkText ?? "?"));
                    // Touching .Page triggers Activator.CreateInstance + the
                    // BackstageViewPage initial property assignments + the
                    // ApplyTheme delegate. UserControl ctor (InitializeComponent)
                    // runs here.
                    var ctrl = p.Page;
                    if (ctrl != null && _prewarmHost != null && !_prewarmHost.IsDisposed)
                    {
                        // Parent to the hidden host so .Handle access cascades
                        // CreateHandle to all 100+ child controls now. This is
                        // the heaviest part of the click-time cost (~80% of it).
                        _prewarmHost.Controls.Add(ctrl);
                        var _ = ctrl.Handle;
                        try { ctrl.PerformLayout(); } catch { }
                        // Remove from host so ActivatePage's Controls.Add into
                        // pnlPages becomes a cheap reparent rather than a full
                        // handle-recreate.
                        _prewarmHost.Controls.Remove(ctrl);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warn("BackstageView prewarm " + (p.LinkText ?? "?") + ": " + ex.Message);
            }
            ScheduleNextPrewarm(q);
        }

        /// <summary>
        /// Show advanced items or not
        /// </summary>
        public static bool Advanced { get; set; }

        public int WidthMenu
        {
            get
            {
                return pnlMenu.Width;
            }
            set
            {
                int delta = value - pnlMenu.Width;
                pnlMenu.Width = value;
                pnlPages.Location = new Point(pnlPages.Location.X + delta, pnlPages.Location.Y);
                pnlPages.Width -= delta;
            }
        }

        private BackstageViewPage popoutPage = null;

        private int ButtonTopPos = 0;

        public BackstageView()
        {
            InitializeComponent();

            this.pnlMenu.Height = this.Height;
            this.pnlPages.Height = this.Height;

            pnlMenu.BackColor = _buttonsAreaBgColor;
            pnlMenu.PencilBorderColor = _buttonsAreaPencilColor;
            pnlMenu.GradColor = this.BackColor;

            
        }

        public void UpdateDisplay()
        {
            foreach (BackstageViewPage itemType in _items)
            {
                if (!itemType.Show)
                    continue;


                if (itemType.Page != null)
                {
                    itemType.Page.Location = new Point(0, 0);
                    itemType.Page.Dock = DockStyle.Fill;
                    itemType.Page.Visible = false;

                    this.pnlPages.Controls.Add(itemType.Page);
                }
            }
        }

        public override Color BackColor
        {
            get
            {
                return base.BackColor;
            }
            set
            {
                base.BackColor = value;
                UpdateButtonAppearance();
                pnlMenu.GradColor = this.BackColor;
            }
        }

        [Description("Background pencil line color for the content region"), Category("Appearance")]
        [DefaultValue(typeof(Color), "DarkGray")]
        public Color ButtonsAreaPencilColor
        {
            get { return _buttonsAreaPencilColor; }
            set
            {
                _buttonsAreaPencilColor = value;
                pnlMenu.PencilBorderColor = _buttonsAreaPencilColor;
                pnlMenu.Invalidate();
                UpdateButtonAppearance();
                Invalidate();
            }
        }


        [Description("Background color for the buttons region"), Category("Appearance")]
        [DefaultValue(typeof(Color), "White")]
        public Color ButtonsAreaBgColor
        {
            get { return _buttonsAreaBgColor; }
            set
            {
                _buttonsAreaBgColor = value;
                this.pnlMenu.BackColor = _buttonsAreaBgColor;
                pnlMenu.Invalidate();
                Invalidate();
            }
        }

        [Description("Color for the selector buttons text"), Category("Appearance")]
        [DefaultValue(typeof(Color), "White")]
        public Color SelectedTextColor
        {
            get { return _selectedTextColor; }
            set
            {
                _selectedTextColor = value;
                UpdateButtonAppearance();
            }
        }

        [Description("Color for the un selected selector buttons text"), Category("Appearance")]
        [DefaultValue(typeof(Color), "Gray")]
        public Color UnSelectedTextColor
        {
            get { return _unSelectedTextColor; }
            set
            {
                _unSelectedTextColor = value;
                UpdateButtonAppearance();
                Invalidate();
            }
        }

        [Description("Color selected button background 1"), Category("Appearance")]
        [DefaultValue(typeof(Color), "DarkBlue")]
        public Color HighlightColor1
        {
            get { return _highlightColor1; }
            set
            {
                _highlightColor1 = value;
                UpdateButtonAppearance();
                Invalidate();
            }
        }

        [Description("Color selected button background 2"), Category("Appearance")]
        [DefaultValue(typeof(Color), "Blue")]
        public Color HighlightColor2
        {
            get { return _highlightColor2; }
            set
            {
                _highlightColor2 = value;
                UpdateButtonAppearance();
                Invalidate();
            }
        }

        /// <summary>
        /// Add a page (tab) to this backstage view. Will be added at the end/bottom
        /// </summary>
        // Phase 10o fork: cache BackstageViewPage instances by (Type, parent
        // header). On vehicle connect / disconnect SoftwareConfig.RebuildPages
        // calls SoftReset() then re-AddPage()s the appropriate set. Without
        // this cache, every re-Add allocated a NEW BackstageViewPage whose
        // .Page getter then ran Activator.CreateInstance + InitializeComponent
        // + ApplyTheme cascade from scratch - ConfigPlanner.Add measured
        // 2887ms on disconnect in profile-20260524-203825. With the cache the
        // existing Control is reused.
        private readonly System.Collections.Generic.Dictionary<string, BackstageViewPage> _pageCache
            = new System.Collections.Generic.Dictionary<string, BackstageViewPage>();

        public BackstageViewPage AddPage(Type userControl, string headerText, BackstageViewPage Parent, bool advanced)
        {
            // Cache key combines Type with Parent's text so a sub-page that
            // legitimately appears under two different parents still works.
            var cacheKey = userControl.FullName + "|" + (Parent?.LinkText ?? "");
            BackstageViewPage page;
            if (_pageCache.TryGetValue(cacheKey, out page) && page != null)
            {
                // Reuse the existing BackstageViewPage (and therefore its
                // already-created Control). Refresh header/advanced flag in
                // case caller changed them between rebuilds.
                page.LinkText = headerText;
                page.Parent = Parent;
                page.Advanced = advanced;
            }
            else
            {
                page = new BackstageViewPage(userControl, headerText, Parent, advanced);
                _pageCache[cacheKey] = page;
            }
            _items.Add(page);
            return page;
        }

        /// <summary>
        /// Add a spacer to this backstage view. Will be added at the end/bottom
        /// </summary>
        /// <param name="spacerheight">the amount to space by</param>
        public void AddSpacer(int spacerheight)
        {
            ButtonTopPos += spacerheight;
        }

        private void CreateLinkButton(BackstageViewPage page, bool haschild = false, bool child = false)
        {
            if (!page.Show)
                return;

            string label = page.LinkText;
            int heightextra = 0;

            if (haschild)
            {
                label = ">> " + label;
            }
            if (child)
            {
                int count = label.Split('\n').Count();
                label = "      " + label.Replace("\n", "\n      ");
                heightextra = 15 * (count - 1);
            }

            var lnkButton = new BackstageViewButton
                                {
                                    Text = label,
                                    Tag = page,
                                    Top = ButtonTopPos,
                                    // Top = _items.TakeWhile(i => i != page).Sum(i => i.Spacing),
                                    Width = this.pnlMenu.Width,
                                    Height = ButtonHeight + heightextra,
                                    ContentPageColor = this.BackColor,
                                    PencilBorderColor = _buttonsAreaPencilColor,
                                    SelectedTextColor = _selectedTextColor,
                                    UnSelectedTextColor = _unSelectedTextColor,
                                    HighlightColor1 = _highlightColor1,
                                    HighlightColor2 = _highlightColor2,
                                    //Dock = DockStyle.Bottom
                                };

            pnlMenu.Controls.Add(lnkButton);
            lnkButton.Click += this.ButtonClick;
            lnkButton.DoubleClick += lnkButton_DoubleClick;

            ButtonTopPos += lnkButton.Height;
            pnlMenu.Invalidate();
        }

        public void DrawMenu(BackstageViewPage CurrentPage, bool force = false)
        {
            if (!force)
            {
                if (_activePage == CurrentPage || CurrentPage == null)
                {
                    bool children = PageHasChildren(CurrentPage);
                    if (!children)
                    {
                        return;
                    }
                }
            }

            pnlMenu.Controls.Clear();

            // reset back to 0
            ButtonTopPos = 0;

            foreach (BackstageViewPage page in _items)
            {
                if (page.GetType() == typeof(BackstageViewPage))
                {
                    // skip advanced pages if we are not advanced
                    if (page.Advanced && !Advanced)
                    {
                        continue;
                    }

                    // its a base item. we want it
                    if (((BackstageViewPage)page).Parent == null)
                    {
                        bool children = PageHasChildren(page);

                        CreateLinkButton((BackstageViewPage)page, children);

                        // remember whats expanded
                        if (CurrentPage == page && children)
                        {
                            if (expanded.Contains((BackstageViewPage)page))
                            {
                                expanded.Remove((BackstageViewPage)page);
                            }
                            else
                            {
                                expanded.Add((BackstageViewPage)page);
                            }
                        }

                        // check for children
                        foreach (BackstageViewPage childrenpage in _items)
                        {
                            if (childrenpage.GetType() == typeof(BackstageViewPage))
                            {
                                if (((BackstageViewPage)childrenpage).Parent == ((BackstageViewPage)page))
                                {
                                    // check if current page has a parent thats not expanded
                                    if (CurrentPage == childrenpage && !expanded.Contains((BackstageViewPage)page))
                                    {
                                        expanded.Add((BackstageViewPage)page);
                                        DrawMenu(CurrentPage, true);
                                        return;
                                    }
                                    // draw all the siblings
                                    if (expanded.Contains((BackstageViewPage)page) || this.DesignMode)
                                    {
                                        CreateLinkButton((BackstageViewPage)childrenpage, false, true);
                                    }
                                }
                            }
                        }
                        continue;
                    }

                }
                else
                {
                    ButtonTopPos += page.Spacing;
                }
            }

            pnlMenu.Invalidate();
        }

        private bool PageHasChildren(BackstageViewPage parent)
        {
            // check for children
            foreach (BackstageViewPage child in _items)
            {
                if (child.GetType() == typeof(BackstageViewPage))
                {
                    if (((BackstageViewPage)child).Parent == parent)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void UpdateButtonAppearance()
        {
            foreach (var backstageViewButton in pnlMenu.Controls.OfType<BackstageViewButton>())
            {
                backstageViewButton.HighlightColor2 = _highlightColor2;
                backstageViewButton.HighlightColor1 = _highlightColor1;
                backstageViewButton.UnSelectedTextColor = _unSelectedTextColor;
                backstageViewButton.SelectedTextColor = _selectedTextColor;
                backstageViewButton.ContentPageColor = this.BackColor;
                backstageViewButton.PencilBorderColor = _buttonsAreaPencilColor;

                backstageViewButton.Invalidate();
            }
        }

        /*
         * Experimental - double clicking a button will spawn it out into a new form
         * Care must be given to lifecycle here - two pages can now be interacted with 
         * 'simultaneously'
         */
        private void lnkButton_DoubleClick(object sender, EventArgs e)
        {
            var backstageViewButton = ((BackstageViewButton)sender);
            var associatedPage = backstageViewButton.Tag as BackstageViewPage;

            var popoutForm = new Form();
            popoutForm.FormClosing += popoutForm_FormClosing;

            int maxright = 0, maxdown = 0;

            foreach (Control ctl in associatedPage.Page.Controls)
            {
                maxright = Math.Max(ctl.Right, maxright);
                maxdown = Math.Max(ctl.Bottom, maxdown);
            }

            // set the height to 0, so we can derive the header height in the next step
            popoutForm.Height = 0;

            popoutForm.Size = new Size(maxright + 20, maxdown + 20 + popoutForm.Height);
            popoutForm.Controls.Add(associatedPage.Page);
            popoutForm.Tag = associatedPage;

            popoutForm.Text = associatedPage.LinkText;

            popoutPage = associatedPage;

            popoutForm.BackColor = this.BackColor;
            popoutForm.ForeColor = this.ForeColor;

            popoutForm.Show(this);
        }

        private void popoutForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // get the page back
            var temp = ((Form)sender).Tag as BackstageViewPage;

            // add back to where it belongs
            this.pnlPages.Controls.Add(temp.Page);

            // clear the controls, so we dont dispose the good control when it closes
            ((Form)sender).Controls.Clear();
            popoutPage = null;
        }

        private void ButtonClick(object sender, EventArgs e)
        {
            var backstageViewButton = ((BackstageViewButton)sender);
            var associatedPage = backstageViewButton.Tag as BackstageViewPage;
            this.ActivatePage(associatedPage);
        }

        // Phase 9 fork: re-entrancy + idempotency guard. Re-clicking the
        // active tab used to re-run the whole Activate path (XML walks,
        // tooltip rewiring, control re-binds) -- visible to users as
        // "double-click doubled the wait". Also block overlapping activates
        // while one is still running (defends against fast tab-jumping
        // queueing multiple full activates on the UI message pump).
        private bool _activating;

        public void ActivatePage(BackstageViewPage associatedPage)
        {
            if (associatedPage == null)
            {
                if (_activePage == null)
                    DrawMenu(null, true);
                return;
            }

            if (_activePage == associatedPage)
                return;
            if (_activating)
                return;
            _activating = true;
            try
            {

            Tracking?.Invoke(associatedPage.Page.GetType().ToString(), associatedPage.LinkText);

            var start = DateTime.Now;

            this.SuspendLayout();
            associatedPage.Page.SuspendLayout();

            DrawMenu(associatedPage, false);

            // Deactivate old page
            if (_activePage != null && _activePage.Page is IDeactivate)
            {
                try
                {
                    ((IDeactivate) (_activePage.Page)).Deactivate();
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                }
            }

            // deactivate the old page - obsolete way of notifying activation
            //_activePage.Page.Close();

            if (_activePage != null && _activePage.Page != null)
                _activePage.Page.Visible = false;

            try
            { // if the button was on an expanded tab. when we leave it no longer exits
                if (_activePage != null)
                {
                    var oldButton = this.pnlMenu.Controls.OfType<BackstageViewButton>().Single(b => b.Tag == _activePage);
                    oldButton.IsSelected = false;
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }

            associatedPage.Page.ResumeLayout(false);
            this.ResumeLayout(false);
            // show it
            associatedPage.Page.Visible = true;

            if (!pnlPages.Controls.Contains(associatedPage.Page))
            {
                MissionPlanner.Utilities.Profiler.Mark("BackstageView.Add:" + (associatedPage.Page?.GetType().Name ?? "?"));
                this.pnlPages.Controls.Add(associatedPage.Page);
                MissionPlanner.Utilities.Profiler.Mark("BackstageView.Add:done");
            }

            // new way of notifying activation. Goal is to get rid of BackStageViewContentPanel
            // so plain old user controls can be added
            if (associatedPage.Page is IActivate)
            {
                MissionPlanner.Utilities.Profiler.Mark("BackstageView.Activate:" + associatedPage.Page.GetType().Name);
                try
                {
                    ((IActivate) (associatedPage.Page)).Activate();
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                }
                MissionPlanner.Utilities.Profiler.Mark("BackstageView.Activate:done");
            }

            try
            {
                var newButton = this.pnlMenu.Controls.OfType<BackstageViewButton>().Single(b => b.Tag == associatedPage);
                newButton.IsSelected = true;
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }

            var end = DateTime.Now;

            log.DebugFormat("{0} {1} {2}", associatedPage.Page.GetType().ToString(), associatedPage.LinkText,
                (end - start).TotalMilliseconds);

            _activePage = associatedPage;

            }
            finally
            {
                _activating = false;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (pnlMenu.Controls.Count == 0)
                DrawMenu(null, false);

            base.OnPaint(e);
        }

        /// <summary>
        /// Phase 10j fork: full teardown for a SoftwareConfig-style host
        /// that wants to rebuild its page list (e.g. after the vehicle
        /// connection state changed). Disposes cached page controls,
        /// clears the Pages collection, removes menu buttons, removes
        /// previously-parented page panels from pnlPages. Caller is then
        /// free to call AddPage() again from scratch.
        /// </summary>
        public void Reset()
        {
            Close(); // dispose cached page controls (existing behaviour)
            try
            {
                pnlPages.Controls.Clear();
            }
            catch { }
            try
            {
                // Drop all menu buttons that CreateLinkButton added. They
                // are direct children of pnlMenu.
                for (int i = pnlMenu.Controls.Count - 1; i >= 0; i--)
                {
                    var c = pnlMenu.Controls[i];
                    if (c is BackstageViewButton)
                    {
                        pnlMenu.Controls.RemoveAt(i);
                        try { c.Dispose(); } catch { }
                    }
                }
            }
            catch { }
            _pageCache.Clear(); // Reset() disposes Pages, cache is invalid
            _items.Clear();
            _activePage = null;
            expanded.Clear();
            ButtonTopPos = 0;
            _prewarmStarted = false;
        }

        /// <summary>
        /// Phase 10o introduced this as a no-dispose variant of Reset() so
        /// the _pageCache could reuse the same Control instances across
        /// rebuilds (saved 2.8s ConfigPlanner.Add on disconnect). Phase
        /// 10p4 reverted that: too many config pages (ConfigRadioOutput,
        /// ConfigHWCompass, ConfigBatteryMonitoring, ...) read MAV.param
        /// ONCE during InitializeComponent / Activate and never refresh,
        /// so reusing a cached Control after a connection state change
        /// produced stale defaults / empty values. Correctness > the
        /// micro-optimisation; SoftReset now just forwards to Reset().
        /// </summary>
        public void SoftReset()
        {
            Reset();
        }

        /// <summary>
        /// Phase 10p3 fork: explicit redraw after a SoftReset + AddPage()
        /// batch. SoftReset cleared the pnlMenu buttons, AddPage only adds
        /// to _items - nothing rebuilds the buttons unless ActivatePage
        /// fires (and on first launch with no lastpagename match it never
        /// does). Without this call the Setup tab shows an empty menu /
        /// blank content panel even though _items has 38 pages. Pair every
        /// SoftReset() + AddPage() batch with RedrawMenu() at the end.
        /// </summary>
        public void RedrawMenu()
        {
            try { DrawMenu(null, true); }
            catch (Exception ex) { log.Warn("BackstageView.RedrawMenu: " + ex.Message); }
        }

        public new void Close()
        {
            foreach (var page in _items)
            {
                if (popoutPage != null && popoutPage == page)
                    continue;

                if (!((BackstageViewPage)page).isPageCreated)
                    continue;

                if (((BackstageViewPage)page).Page is IDeactivate)
                {
                    try
                    {
                        ((IDeactivate) ((BackstageViewPage) (page)).Page).Deactivate();
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex);
                    }

                    try
                    {
                        ((BackstageViewPage)page).Page.Dispose();
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex);
                    }
                }
                else
                {
                    try
                    {
                        ((BackstageViewPage)page).Page.Dispose();
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex);
                    }
                }
            }
        }

        private void BackstageView_Load(object sender, EventArgs e)
        {
            UpdateDisplay();
            DrawMenu(_activePage, false);
        }
    }
}