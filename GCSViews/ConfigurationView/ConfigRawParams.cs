using log4net;
using MissionPlanner.Controls;
using MissionPlanner.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using org.mariuszgromada.math.mxparser;
using System.Runtime.CompilerServices;

namespace MissionPlanner.GCSViews.ConfigurationView
{
    public partial class ConfigRawParams : MyUserControl, IActivate, IDeactivate
    {
        // from http://stackoverflow.com/questions/2512781/winforms-big-paragraph-tooltip/2512895#2512895
        private const int maximumSingleLineTooltipLength = 50;

        private static readonly ILog log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static Hashtable tooltips = new Hashtable();
        // Changes made to the params between writing to the copter
        private readonly Hashtable _changes = new Hashtable();
        private static List<GitHubContent.FileInfo> paramfiles;
        // ?
        internal static bool startup = true;
        internal static List<DataGridViewRow> rowlist = new List<DataGridViewRow>();

        // Used by Param Tree to filter by prefix
        private string filterPrefix = "";

        private NaturalStringComparer naturalsorter = new NaturalStringComparer();

        // Phase 10n fork: deferred metadata enrichment (Desc/Units/Options +
        // tooltips). Set by processToScreen, consumed by EnrichMetadataChunked
        // via BeginInvoke chain after first paint.
        private string _pendingEnrichFirmware;
        private int _enrichGen; // increments to cancel in-flight chunked walk

        public ConfigRawParams()
        {
            InitializeComponent();
        }

        public void Activate()
        {
            Profiler.Mark("ConfigRawParams.Activate:begin");
            if ((rowlist.Count == 0) || (!Settings.Instance.GetBoolean("SlowMachine", false))) startup = true;
            //If we connected to another vehicle the do a full refresh
            if (rowlist.Count != MainV2.comPort.MAV.param.Count()) startup = true;

            _changes.Clear();

            BUT_writePIDS.Enabled = MainV2.comPort.BaseStream.IsOpen;
            BUT_rerequestparams.Enabled = MainV2.comPort.BaseStream.IsOpen;
            BUT_reset_params.Enabled = MainV2.comPort.BaseStream.IsOpen;
            BUT_commitToFlash.Visible = MainV2.DisplayConfiguration.displayParamCommitButton;
            BUT_refreshTable.Visible = Settings.Instance.GetBoolean("SlowMachine", false);

            CMB_paramfiles.Enabled = false;
            BUT_paramfileload.Enabled = false;
            ThreadPool.QueueUserWorkItem(updatedefaultlist);

            Params.Enabled = false;

            // Phase 10m fork: gate log per column (was unconditional INFO).
            bool logInfo = log.IsInfoEnabled;
            foreach (DataGridViewColumn col in Params.Columns)
            {
                if (col.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill) continue;
                if (col.Resizable == DataGridViewTriState.False) continue;

                var key = "rawparam_" + col.Name + "_width";
                if (!String.IsNullOrEmpty(Settings.Instance[key]))
                {
                    col.Width = (int)Math.Max(5, Settings.Instance.GetInt32(key));
                    if (logInfo) log.InfoFormat("{0} to {1}", col.Name, col.Width);
                }
            }
            splitContainer1.SplitterDistance = Settings.Instance.GetInt32("rawparam_splitterdistance", 180);
            splitContainer1.Panel1Collapsed = Settings.Instance.GetBoolean("rawparam_panel1collapsed", false);
            but_collapse.Text = splitContainer1.Panel1Collapsed ? ">" : "<";
            Profiler.Mark("ConfigRawParams.Activate:before-processToScreen startup=" + startup);

            processToScreen();
            Profiler.Mark("ConfigRawParams.Activate:after-processToScreen");

            Params.Enabled = true;

            Common.MessageShowAgain(Strings.RawParamWarning, Strings.RawParamWarningi);

            startup = false;

            txt_search.Focus();
            Profiler.Mark("ConfigRawParams.Activate:done");
        }

        public void Deactivate()
        {
            // Phase 8 fix: stop+unwire the debounce timer on deactivate so
            // repeated Activate/Deactivate cycles do not leak handlers or
            // fire a stale filter against a disposed grid.
            _filterTimer.Stop();
            if (_filterTimerWired)
            {
                _filterTimer.Tick -= FilterTimerOnElapsed;
                _filterTimerWired = false;
            }

            foreach (DataGridViewColumn col in Params.Columns)
            {
                // Don't need to save the width of a fill column
                if (col.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill) continue;

                // Don't need to save the width of a column that can't be resized
                if (col.Resizable == DataGridViewTriState.False) continue;

                Settings.Instance["rawparam_" + col.Name + "_width"] = col.Width.ToString("0", CultureInfo.InvariantCulture);
            }

            Settings.Instance["rawparam_splitterdistance"] = splitContainer1.SplitterDistance.ToString();
            Settings.Instance["rawparam_panel1collapsed"] = splitContainer1.Panel1Collapsed.ToString();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S))
            {
                BUT_writePIDS_Click(null, null);
                return true;
            }

            return false;
        }

        private void BUT_load_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog
            {
                AddExtension = true,
                DefaultExt = ".param",
                RestoreDirectory = true,
                Filter = ParamFile.FileMask
            })
            {
                var dr = ofd.ShowDialog();

                if (dr == DialogResult.OK)
                {
                    loadparamsfromfile(ofd.FileName, !MainV2.comPort.BaseStream.IsOpen);

                    if (!MainV2.comPort.BaseStream.IsOpen)
                        Activate();
                }
            }
        }

        private void loadparamsfromfile(string fn, bool offline = false)
        {
            var param2 = ParamFile.loadParamFile(fn);

            var loaded = 0;
            var missed = 0;
            List<string> missing = new List<string>();

            foreach (string name in param2.Keys)
            {
                var set = false;
                var value = param2[name].ToString();
                // set param table as well
                foreach (DataGridViewRow row in Params.Rows)
                {
                    if (name == "SYSID_SW_MREV")
                        continue;
                    if (name == "WP_TOTAL")
                        continue;
                    if (name == "CMD_TOTAL")
                        continue;
                    if (name == "FENCE_TOTAL")
                        continue;
                    if (name == "SYS_NUM_RESETS")
                        continue;
                    if (name == "ARSPD_OFFSET")
                        continue;
                    if (name == "GND_ABS_PRESS")
                        continue;
                    if (name == "GND_TEMP")
                        continue;
                    if (name == "CMD_INDEX")
                        continue;
                    if (name == "LOG_LASTFILE")
                        continue;
                    if (name == "FORMAT_VERSION")
                        continue;
                    if (row.Cells[Command.Index].Value != null && row.Cells[Command.Index].Value?.ToString() == name)
                    {
                        set = true;
                        if (row.Cells[Value.Index].Value.ToString() != value)
                            row.Cells[Value.Index].Value = value;
                        break;
                    }
                }

                if (offline && !set)
                {
                    set = true;
                    MainV2.comPort.MAV.param.Add(new MAVLink.MAVLinkParam(name, double.Parse(value),
                        MAVLink.MAV_PARAM_TYPE.REAL32));
                }

                if (set)
                {
                    loaded++;
                }
                else
                {
                    missed++;
                    missing.Add(name);
                }
            }

            if (missed > 0)
            {
                string list = "";
                foreach (var item in missing)
                {
                    list += item + " ";
                }
                CustomMessageBox.Show("Missing " + missed + " params\n" + list, "No matching Params", MessageBoxButtons.OK);
            }
        }

        private void BUT_save_Click(object sender, EventArgs e)
        {
            using (var sfd = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = ".param",
                RestoreDirectory = true,
                Filter = "Param List|*.param;*.parm"
            })
            {
                var dr = sfd.ShowDialog();
                if (dr == DialogResult.OK)
                {
                    var data = new Hashtable();
                    foreach (DataGridViewRow row in Params.Rows)
                    {
                        try
                        {
                            var value = double.Parse(row.Cells[Value.Index].Value.ToString());

                            data[row.Cells[Command.Index].Value.ToString()] = value;
                        }
                        catch (Exception)
                        {
                            CustomMessageBox.Show(Strings.InvalidNumberEntered + " " + row.Cells[Command.Index].Value);
                        }
                    }

                    ParamFile.SaveParamFile(sfd.FileName, data);
                }
            }
        }

        private void BUT_writePIDS_Click(object sender, EventArgs e)
        {
            // sort with enable at the bottom - this ensures params are set before the function is disabled
            var temp = _changes.Keys.Cast<string>().ToList();

            temp.SortENABLE();

            bool enable = temp.Any(a => a.EndsWith("_ENABLE"));

            int error = 0;
            bool reboot = false;
            int maxdisplay = 20;

            if (temp.Count > 0 && temp.Count <= maxdisplay)
            {
                // List to track successfully saved parameters
                List<string> savedParams = new List<string>();

                foreach (string value in temp)
                {
                    if (MainV2.comPort.BaseStream == null || !MainV2.comPort.BaseStream.IsOpen)
                    {
                        CustomMessageBox.Show("You are not connected", Strings.ERROR);
                        return;
                    }

                    // Get the previous value of the param to display in 'param change info'
                    // (a better way would be to get the value somewhere from inside the code, insted of recieving it over mavlink)
                    string previousValue = MainV2.comPort.MAV.param[value].ToString();
                    // new value of param
                    double newValue = (double)_changes[value];

                    // Add the parameter, previous and new values to the list for 'param change info'
                    // remember, the 'value' here is key of param, while prev and new are actual values of param
                    savedParams.Add($"{value}: {previousValue} -> {newValue}");
                }

                // Join the saved parameters list to a string
                string savedParamsMessage = string.Join(Environment.NewLine, savedParams);

                // Ask the user for confirmation showing detailed changes
                if (CustomMessageBox.Show($"You are about to change {savedParams.Count} parameters. Please review the changes below:\n\n{savedParamsMessage}\n\nDo you want to proceed?", "Confirm Parameter Changes",
        CustomMessageBox.MessageBoxButtons.YesNo, CustomMessageBox.MessageBoxIcon.Information) !=
    CustomMessageBox.DialogResult.Yes)
                    return;
            }
            else if (temp.Count > maxdisplay)
            {
                // Ask the user for confirmation without listing individual changes
                if (CustomMessageBox.Show($"You are about to change {temp.Count} parameters. Are you sure you want to proceed?", "Confirm Parameter Changes",
            CustomMessageBox.MessageBoxButtons.YesNo, CustomMessageBox.MessageBoxIcon.Information) !=
        CustomMessageBox.DialogResult.Yes)
                    return;
            }


            foreach (string value in temp)
            {
                try
                {
                    if (MainV2.comPort.BaseStream == null || !MainV2.comPort.BaseStream.IsOpen)
                    {
                        CustomMessageBox.Show("Your are not connected", Strings.ERROR);
                        return;
                    }

                    MainV2.comPort.setParam(value, (double)_changes[value]);
                    //check if reboot required
                    if (ParameterMetaDataRepository.GetParameterRebootRequired(value, MainV2.comPort.MAV.cs.firmware.ToString()))
                    {
                        reboot = true;
                    }
                    try
                    {
                        // set control as well
                        var textControls = Controls.Find(value, true);
                        if (textControls.Length > 0)
                        {
                            ThemeManager.ApplyThemeTo(textControls[0]);
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        // set param table as well
                        foreach (DataGridViewRow row in Params.Rows)
                        {
                            if (row.Cells[Command.Index].Value.ToString() == value)
                            {
                                row.Cells[Value.Index].Style.BackColor = ThemeManager.ControlBGColor;
                                _changes.Remove(value);
                                break;
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                catch
                {
                    error++;
                    CustomMessageBox.Show("Set " + value + " Failed");
                }
            }

            if (error > 0)
                CustomMessageBox.Show("Not all parameters successfully saved.", "Saved");
            else if (temp.Count>0)
                CustomMessageBox.Show($"{temp.Count} parameters successfully saved.", "Saved");
            else
                CustomMessageBox.Show("No parameters were changed.", "No changes");

            //Check if reboot is required
            if (reboot)
            {
               CustomMessageBox.Show("Reboot is required for some parameters to take effect.", "Reboot Required");
            }

            if (MainV2.comPort.MAV.param.TotalReceived != MainV2.comPort.MAV.param.TotalReported )
            {
                if (MainV2.comPort.MAV.cs.armed)
                {
                    CustomMessageBox.Show("The number of available parameters changed, until full param refresh is done, some parameters will not be available.", "Params");
                    //Hack the number of reported params to keep params list available
                    MainV2.comPort.MAV.param.TotalReported = MainV2.comPort.MAV.param.TotalReceived;
                }
                else
                {
                    CustomMessageBox.Show("The number of available parameters changed. A full param refresh will be done to show all params.", "Params");
                    //Click on refresh button
                    BUT_rerequestparams_Click(BUT_rerequestparams, null);
                }
            }
        }

        private void BUT_compare_Click(object sender, EventArgs e)
        {
            var param2 = new Dictionary<string, double>();

            using (var ofd = new OpenFileDialog
            {
                AddExtension = true,
                DefaultExt = ".param",
                RestoreDirectory = true,
                Filter = ParamFile.FileMask
            })
            {
                var dr = ofd.ShowDialog();
                if (dr == DialogResult.OK)
                {
                    param2 = ParamFile.loadParamFile(ofd.FileName);

                    Form paramCompareForm = new ParamCompare(Params, MainV2.comPort.MAV.param, param2);

                    ThemeManager.ApplyThemeTo(paramCompareForm);
                    paramCompareForm.ShowDialog();
                }
            }
        }

        private void BUT_rerequestparams_Click(object sender, EventArgs e)
        {
            if (!MainV2.comPort.BaseStream.IsOpen)
                return;

            if (!MainV2.comPort.MAV.cs.armed || DialogResult.OK ==
                Common.MessageShowAgain("Refresh Params", Strings.WarningUpdateParamList, true))
            {
                ((Control)sender).Enabled = false;

                try
                {
                    MainV2.comPort.getParamList();
                }
                catch (Exception ex)
                {
                    log.Error("Exception getting param list", ex);
                    CustomMessageBox.Show(Strings.ErrorReceivingParams, Strings.ERROR);
                }


                ((Control)sender).Enabled = true;

                startup = true;

                processToScreen();

                FilterTimerOnElapsed(null, null);

                startup = false;
            }
        }

        private void Params_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex == -1 || e.ColumnIndex == -1 || startup || e.ColumnIndex != Value.Index)
                return;
            try
            {
                if (Params[Command.Index, e.RowIndex].Value.ToString().EndsWith("_REV") &&
                    (Params[Command.Index, e.RowIndex].Value.ToString().StartsWith("RC") ||
                     Params[Command.Index, e.RowIndex].Value.ToString().StartsWith("HS")))
                {
                    if (Params[e.ColumnIndex, e.RowIndex].Value.ToString() == "0")
                        Params[e.ColumnIndex, e.RowIndex].Value = "-1";
                }

                double min = 0;
                double max = 0;

                var value = Params[e.ColumnIndex, e.RowIndex].Value.ToString();
                value = value.Replace(',', '.');

                var newvalue = (double) new Expression(value).calculate();
                if (double.IsNaN(newvalue) || double.IsInfinity(newvalue))
                {
                    throw new Exception();
                }

                var readonly1 = ParameterMetaDataRepository.GetParameterMetaData(
                    Params[Command.Index, e.RowIndex].Value.ToString(),
                    ParameterMetaDataConstants.ReadOnly, MainV2.comPort.MAV.cs.firmware.ToString());
                if (!String.IsNullOrEmpty(readonly1))
                {
                    var readonly2 = bool.Parse(readonly1);
                    if (readonly2)
                    {
                        CustomMessageBox.Show(
                            Params[Command.Index, e.RowIndex].Value +
                            " is marked as ReadOnly, and will not be changed", "ReadOnly",
                            MessageBoxButtons.OK);
                        Params.CellValueChanged -= Params_CellValueChanged;
                        Params[e.ColumnIndex, e.RowIndex].Value = cellEditValue;
                        Params.CellValueChanged += Params_CellValueChanged;
                        return;
                    }
                }

                if (ParameterMetaDataRepository.GetParameterRange(Params[Command.Index, e.RowIndex].Value.ToString(),
                    ref min, ref max, MainV2.comPort.MAV.cs.firmware.ToString()))
                {
                    if (newvalue > max || newvalue < min)
                    {
                        if (
                            CustomMessageBox.Show(
                                Params[Command.Index, e.RowIndex].Value +
                                " value is out of range. Do you want to continue?", "Out of range",
                                MessageBoxButtons.YesNo) == (int)DialogResult.No)
                        {
                            Params.CellValueChanged -= Params_CellValueChanged;
                            Params[e.ColumnIndex, e.RowIndex].Value = cellEditValue;
                            Params.CellValueChanged += Params_CellValueChanged;
                            return;
                        }
                    }
                }

                Params[e.ColumnIndex, e.RowIndex].Style.BackColor = Color.Green;
                log.InfoFormat("Queue change {0} = {1} ({2})", Params[Command.Index, e.RowIndex].Value, Params[e.ColumnIndex, e.RowIndex].Value, newvalue);
                _changes[Params[Command.Index, e.RowIndex].Value] = newvalue;

                Params.CellValueChanged -= Params_CellValueChanged;
                Params[e.ColumnIndex, e.RowIndex].Value = newvalue.ToString();
                Params.CellValueChanged += Params_CellValueChanged;
            }
            catch (Exception)
            {
                Params[e.ColumnIndex, e.RowIndex].Style.BackColor = Color.Red;
            }


            Params.Focus();
        }

        private static string AddNewLinesForTooltip(string text)
        {
            if (text.Length < maximumSingleLineTooltipLength)
                return text;
            var lineLength = (int)Math.Sqrt(text.Length) * 2;
            var sb = new StringBuilder();
            var currentLinePosition = 0;
            for (var textIndex = 0; textIndex < text.Length; textIndex++)
            {
                // If we have reached the target line length and the next
                // character is whitespace then begin a new line.
                if (currentLinePosition >= lineLength &&
                    char.IsWhiteSpace(text[textIndex]))
                {
                    sb.Append(Environment.NewLine);
                    currentLinePosition = 0;
                }
                // If we have just started a new line, skip all the whitespace.
                if (currentLinePosition == 0)
                    while (textIndex < text.Length && char.IsWhiteSpace(text[textIndex]))
                        textIndex++;
                // Append the next character.
                if (textIndex < text.Length) sb.Append(text[textIndex]);
                currentLinePosition++;
            }
            return sb.ToString();
        }

        internal void processToScreen()
        {
            Profiler.Mark("processToScreen:begin startup=" + startup);
            toolTip1.RemoveAll();
            Params.Rows.Clear();
            log.Info("processToScreen");
            Profiler.Mark("processToScreen:after-Rows.Clear");

            var list = new List<string>();

            // process hashdefines and update display
            // But only if startup is true, otherwise we assume that rowlist is valid and just put it back
            // to the gridview

            if (startup)
            {
                rowlist.Clear();

                bool has_defaults = false;

                // Phase 10m fork: SNAPSHOT MAV.param once. The original code
                // hit MAV.param[value] 2-3x per row inside Parallel.ForEach,
                // and the MAVLinkParamList indexer is O(n) linear scan +
                // ReaderWriterLock.AcquireReaderLock per call. With 1500 params
                // that is 4500 calls * O(1500) = ~6.75M string compares plus
                // 4500 lock acquires (~30us each on Wine, serialised across
                // parallel workers via the same lock instance). Snapshotting
                // to a plain Dictionary collapses all of that into O(n).
                MAVLink.MAVLinkParam[] snapshot;
                try { snapshot = MainV2.comPort.MAV.param.ToArray(); }
                catch { snapshot = new MAVLink.MAVLinkParam[0]; }

                var paramByName = new Dictionary<string, MAVLink.MAVLinkParam>(
                    snapshot.Length, StringComparer.Ordinal);
                foreach (var p in snapshot)
                {
                    if (p?.Name == null) continue;
                    paramByName[p.Name] = p;
                    list.Add(p.Name);
                }
                Profiler.Mark("processToScreen:snapshot count=" + snapshot.Length);

                // Phase 8 fix: hoist out per-iteration work:
                //   1. Settings.Instance.GetList("fav_params") was called
                //      1500 times inside the loop (each hits the dict).
                //   2. MAV.cs.firmware.ToString() was called 4x per row.
                //   3. ParameterMetaData first call parses ~10-50MB XML on
                //      the UI thread; warm the cache here so Parallel.ForEach
                //      threads don't all race to parse it.
                // Phase 10m fork: fav_params is a List<string>, so Contains
                // is O(|fav|) per call -> O(n*|fav|) total. Hash it once.
                var favSet = new HashSet<string>(
                    Settings.Instance.GetList("fav_params") ?? Enumerable.Empty<string>(),
                    StringComparer.Ordinal);
                var firmware = MainV2.comPort.MAV.cs.firmware.ToString();
                if (list.Count > 0)
                {
                    var firstKey = list.FirstOrDefault(s => !string.IsNullOrEmpty(s));
                    if (firstKey != null)
                        ParameterMetaDataRepository.GetParameterMetaData(firstKey,
                            ParameterMetaDataConstants.Description, firmware);
                }

                // Phase 10n fork: Parallel.ForEach was HURTING this loop, not
                // helping. The actual work is UI-bound (row.CreateCells reads
                // Params.Columns under WinForms locks) and serialised by
                // `lock (rowlist)`. Parallel adds thread pool dispatch
                // overhead + blocks the UI thread for the wall-clock duration
                // anyway. Plain UI-thread foreach is simpler AND faster.
                //
                // Additionally we split into two passes:
                //   Pass 1 (this loop): name + value + favorite + default.
                //                       Fast - ~50us per row. ~60ms total.
                //   Pass 2 (deferred):  description / units / options /
                //                       tooltips. Chunked BeginInvoke so the
                //                       grid paints first and stays responsive.
                foreach (var value in list)
                {
                    if (string.IsNullOrEmpty(value)) continue;
                    if (!paramByName.TryGetValue(value, out var p) || p == null) continue;

                    var row = new DataGridViewRow() { Height = 36 };
                    row.CreateCells(Params);
                    row.Cells[Command.Index].Value = value;
                    row.Cells[Value.Index].Value = p.ToString();
                    row.Cells[Fav.Index].Value = favSet.Contains(value);

                    if (p.default_value.HasValue) {
                        has_defaults = true;
                        row.Cells[Default_value.Index].Value = p.default_value_to_string();
                    } else {
                        row.Cells[Default_value.Index].Value = "NaN";
                    }
                    rowlist.Add(row);
                }

                _pendingEnrichFirmware = firmware;
                Default_value.Visible = has_defaults;
                chk_none_default.Visible = has_defaults;
                Profiler.Mark("processToScreen:rows-built count=" + rowlist.Count);

                // Phase 10m fork: pre-sort rowlist before AddRange. The grid's
                // SortCompare event hits ~7,500 cell reads for 1,500 rows
                // (~200-300ms on the UI thread). Sorting a List<DataGridViewRow>
                // by the same key (fav desc, then natural by command) is cheaper
                // and lets us skip the post-AddRange Sort() entirely.
                var cmdIdx = Command.Index;
                var favIdx = Fav.Index;
                var comparer = naturalsorter;
                rowlist.Sort((r1, r2) =>
                {
                    var f1 = (r1.Cells[favIdx].Value is bool b1) && b1;
                    var f2 = (r2.Cells[favIdx].Value is bool b2) && b2;
                    if (f1 != f2) return f1 ? -1 : 1;
                    return comparer.NaturalCompare(
                        r1.Cells[cmdIdx].Value?.ToString() ?? "",
                        r2.Cells[cmdIdx].Value?.ToString() ?? "");
                });
                Profiler.Mark("processToScreen:rowlist-sorted");
            }
            //update values in rowlist
            if (!startup)
            {
                // Phase 10m fork: snapshot lookup (see comment above) so the
                // refresh path is O(n) instead of O(n^2) on MAV.param[name].
                MAVLink.MAVLinkParam[] refreshSnap;
                try { refreshSnap = MainV2.comPort.MAV.param.ToArray(); }
                catch { refreshSnap = new MAVLink.MAVLinkParam[0]; }
                var refreshMap = new Dictionary<string, MAVLink.MAVLinkParam>(
                    refreshSnap.Length, StringComparer.Ordinal);
                foreach (var p in refreshSnap)
                    if (p?.Name != null) refreshMap[p.Name] = p;

                foreach (DataGridViewRow r in rowlist)
                {
                    var name = r.Cells[Command.Index].Value?.ToString();
                    if (name != null && refreshMap.TryGetValue(name, out var p) && p != null)
                        r.Cells[Value.Index].Value = p.ToString();
                }
            }


            log.Info("about to add all");

            // Phase 10m fork: SuspendLayout around AddRange. WinForms re-flows
            // the grid on every Rows.AddRange iteration even with Visible=false;
            // SuspendLayout collapses that into a single ResumeLayout call.
            Params.Visible = false;
            Params.SuspendLayout();
            Params.Rows.AddRange(rowlist.ToArray());
            Params.ResumeLayout(false);
            Profiler.Mark("processToScreen:AddRange-done");

            // SortCompare must still be wired so the user can click columns to
            // re-sort. The actual initial sort is already done in-list above,
            // so we skip Params.Sort() (saves the 200-300ms cost).
            Params.SortCompare -= OnParamsOnSortCompare; // idempotent
            Params.SortCompare += OnParamsOnSortCompare;

            Params.Visible = true;

            if (splitContainer1.Panel1Collapsed == false)
            {
                // Phase 8 fix: BuildTree is O(n^2) over the 1500-row list
                // (~500ms-1s on the UI thread). Defer it briefly so the grid
                // renders first; tree appears a moment later, user gets to
                // scroll the grid right away.
                BeginInvoke(new Action(BuildTree));
            }

            // Phase 10n fork: kick off deferred metadata enrichment. Cancels
            // any in-flight chunked walk (via _enrichGen bump) and starts
            // fresh on this rowlist. First chunk runs at next message pump.
            if (startup && !string.IsNullOrEmpty(_pendingEnrichFirmware))
            {
                Interlocked.Increment(ref _enrichGen);
                var gen = _enrichGen;
                var fw = _pendingEnrichFirmware;
                BeginInvoke(new Action(() => EnrichMetadataChunked(0, fw, gen)));
            }

            log.Info("Done");
            Profiler.Mark("processToScreen:done");
        }

        // Phase 10n fork: walk Params.Rows in small chunks, filling in the
        // expensive Description/Units/Options cells + tooltips. Yields between
        // chunks via BeginInvoke so the UI message pump keeps running -
        // heartbeat stays alive, user can scroll/click/filter during the walk.
        private void EnrichMetadataChunked(int startIdx, string firmware, int gen)
        {
            if (gen != Volatile.Read(ref _enrichGen)) return; // a newer pass took over
            if (Params == null || Params.IsDisposed) return;

            const int chunkSize = 64;
            int total = Params.Rows.Count;
            int end = Math.Min(startIdx + chunkSize, total);

            if (startIdx == 0) Profiler.Mark("EnrichMetadata:begin total=" + total);

            // Phase 10n: prefer the pre-warmed ParamDisplayCache. If hot, the
            // expensive string composition is already done on a bg thread -
            // this loop is pure UI cell assignment, ~5us per row.
            var cache = ParamDisplayCache.Current;
            bool cacheHot = cache != null
                && ParamDisplayCache.CurrentFirmware == firmware;

            for (int i = startIdx; i < end; i++)
            {
                try
                {
                    var row = Params.Rows[i];
                    var nameObj = row.Cells[Command.Index].Value;
                    if (nameObj == null) continue;
                    var name = nameObj.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    ParamDisplayCache.EnrichedParam pre = null;
                    if (cacheHot) cache.TryGetValue(name, out pre);

                    string desc, descTip, units, optionsCell, optionsTip;
                    if (pre != null)
                    {
                        desc = pre.Description;
                        descTip = pre.DescriptionTooltip;
                        units = pre.Units;
                        optionsCell = pre.OptionsCellText;
                        optionsTip = pre.OptionsTooltipText;
                    }
                    else
                    {
                        // Live compose (cache miss or not warm yet).
                        desc = ParameterMetaDataRepository.GetParameterMetaData(name,
                            ParameterMetaDataConstants.Description, firmware);
                        if (string.IsNullOrEmpty(desc)) continue;
                        descTip = AddNewLinesForTooltip(desc);
                        var range = ParameterMetaDataRepository.GetParameterMetaData(name,
                            ParameterMetaDataConstants.Range, firmware) ?? "";
                        var options = ParameterMetaDataRepository.GetParameterMetaData(name,
                            ParameterMetaDataConstants.Values, firmware) ?? "";
                        units = ParameterMetaDataRepository.GetParameterMetaData(name,
                            ParameterMetaDataConstants.Units, firmware);
                        optionsCell = (range + "\n" + options.Replace(",", "\n")).Trim();
                        optionsTip = options.Length > 0 ? options.Replace(',', '\n') : null;
                    }

                    if (string.IsNullOrEmpty(desc)) continue;
                    row.Cells[Desc.Index].Value = desc;
                    row.Cells[Desc.Index].ToolTipText = descTip;
                    row.Cells[Command.Index].ToolTipText = descTip;
                    row.Cells[Value.Index].ToolTipText = descTip;
                    row.Cells[Units.Index].Value = units;
                    row.Cells[Options.Index].Value = optionsCell;
                    if (!string.IsNullOrEmpty(optionsTip))
                        row.Cells[Options.Index].ToolTipText = optionsTip;
                }
                catch (Exception ex) { log.Error(ex); }
            }

            if (end < total)
            {
                BeginInvoke(new Action(() => EnrichMetadataChunked(end, firmware, gen)));
            }
            else
            {
                Profiler.Mark("EnrichMetadata:done");
            }
        }

        private void BuildTree()
        {
            treeView1.Nodes.Clear();
            var currentNode = treeView1.Nodes.Add("All");
            string currentPrefix = "";

            // Get command names from the gridview
            List<string> commands = new List<string>();
            foreach (DataGridViewRow row in Params.Rows)
            {
                string command = row.Cells[Command.Index].Value.ToString();
                if (!commands.Contains(command))
                {
                    commands.Add(command);
                }
            }

            // Sort them again (because of the favorites, they may be out of order)
            commands.Sort(naturalsorter);

            for (int i = 0; i < commands.Count; i++)
            {
                string param = commands[i];

                // While param does not start with currentPrefix, step up a layer in the tree
                while (!param.StartsWith(currentPrefix))
                {
                    currentPrefix = currentPrefix.RemoveFromEnd(currentNode.Text.Split('_').Last() + "_");
                    currentNode = currentNode.Parent;
                }

                // If this is the last parameter, add it
                if (i == commands.Count - 1)
                {
                    currentNode.Nodes.Add(param);
                    break;
                }

                string next_param = commands[i + 1];
                // While the next parameter has a common prefix with this, add branch nodes
                string nodeToAdd = param.Substring(currentPrefix.Length).Split('_')[0] + "_";
                while (nodeToAdd.Length > 1 // While the currentPrefix is smaller than param
                    && param.StartsWith(currentPrefix + nodeToAdd) // And while this parameter starts with currentPrefix+nodeToAdd (needed for edge case where next_param starts with the full name of this param; see Q_PLT_Y_RATE and Q_PLT_Y_RATE_TC)
                    && next_param.StartsWith(currentPrefix + nodeToAdd)) // And the next parameter also starts with currentPrefix
                {
                    currentPrefix += nodeToAdd;
                    currentNode = currentNode.Nodes.Add(currentPrefix.Substring(0, currentPrefix.Length - 1));
                    nodeToAdd = param.Substring(currentPrefix.Length).Split('_')[0] + "_";
                }
                currentNode.Nodes.Add(param);
            }
            treeView1.TopNode.Expand();
        }


        // Based on https://gist.github.com/Nazardo/e42de483a03ec2e1ef9348e23bec4f95
        // (c) 2019 M. Levra

        public sealed class NaturalStringComparer : IComparer<string>
        {
            #region IComparer<string> Members

            public int Compare(string x, string y)
            {
                return NaturalCompare(x, y);
            }

            #endregion

            public int NaturalCompare(string x, string y)
            {
                int indexX = 0;
                int indexY = 0;
                while (true)
                {
                    // Handle the case when one string has ended.
                    if (indexX == x.Length)
                    {
                        return indexY == y.Length ? 0 : -1;
                    }
                    if (indexY == y.Length)
                    {
                        return 1;
                    }

                    char charX = x[indexX];
                    char charY = y[indexY];
                    if (char.IsDigit(charX) && char.IsDigit(charY))
                    {
                        // Skip leading zeroes in numbers.
                        while (indexX < x.Length && x[indexX] == '0')
                        {
                            indexX++;
                        }
                        while (indexY < y.Length && y[indexY] == '0')
                        {
                            indexY++;
                        }

                        // Find the end of numbers
                        int endNumberX = indexX;
                        int endNumberY = indexY;
                        while (endNumberX < x.Length && char.IsDigit(x[endNumberX]))
                        {
                            endNumberX++;
                        }
                        while (endNumberY < y.Length && char.IsDigit(y[endNumberY]))
                        {
                            endNumberY++;
                        }

                        int digitsLengthX = endNumberX - indexX;
                        int digitsLengthY = endNumberY - indexY;

                        // If the lengths are different, then the longer number is bigger
                        if (digitsLengthX != digitsLengthY)
                        {
                            return digitsLengthX - digitsLengthY;
                        }
                        // Compare numbers digit by digit
                        while (indexX < endNumberX)
                        {
                            if (x[indexX] != y[indexY])
                                return x[indexX] - y[indexY];
                            indexX++;
                            indexY++;
                        }
                    }
                    else
                    {
                        // Plain characters comparison
                        int compareResult = char.ToUpperInvariant(charX).CompareTo(char.ToUpperInvariant(charY));
                        if (compareResult != 0)
                        {
                            return compareResult;
                        }
                        indexX++;
                        indexY++;
                    }
                }
            }
        }



        private void OnParamsOnSortCompare(object sender, DataGridViewSortCompareEventArgs args)
        {
            var fav1obj = Params[Fav.Index, args.RowIndex1].Value;
            var fav2obj = Params[Fav.Index, args.RowIndex2].Value;

            var fav1 = fav1obj == null ? false : (bool)fav1obj;

            var fav2 = fav2obj == null ? false : (bool)fav2obj;

            if (args.CellValue1 == null)
                return;

            if (args.CellValue2 == null)
                return;

            args.SortResult = naturalsorter.NaturalCompare(args.CellValue1.ToString(), args.CellValue2.ToString());
            args.Handled = true;

            if (fav1 && fav2)
            {
                return;
            }

            if (fav1 || fav2)
                args.SortResult = fav1.CompareTo(fav2) * (Params.SortOrder == SortOrder.Ascending ? -1 : 1);
        }

        private void updatedefaultlist(object crap)
        {
            try
            {
                if (paramfiles == null)
                {
                    string subdir = "";
                    if (MainV2.comPort.MAV.param.ContainsKey("Q_ENABLE") &&
                        MainV2.comPort.MAV.param["Q_ENABLE"].Value >= 1.0)
                    {
                        subdir = "QuadPlanes/";
                    }
                    paramfiles = GitHubContent.GetDirContent("ardupilot", "ardupilot", "/Tools/Frame_params/" + subdir, ".param");
                }

                BeginInvoke((Action)delegate
               {
                   CMB_paramfiles.DataSource = paramfiles.ToArray();
                   CMB_paramfiles.DisplayMember = "name";
                   CMB_paramfiles.Enabled = true;
                   BUT_paramfileload.Enabled = true;
               });
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        }

        void filterList(string searchfor)
        {
            DateTime start = DateTime.Now;
            Params.Visible = false;
            if (searchfor.Length >= 2 || searchfor.Length == 0)
            {
                Regex filter = new Regex(searchfor.Replace("*", ".*").Replace("..*", ".*"), RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

                foreach (DataGridViewRow row in Params.Rows)
                {
                    string name = row.Cells[Command.Index].Value.ToString();
                    if (name != filterPrefix.TrimEnd('_') && !name.StartsWith(filterPrefix))
                    {
                        row.Visible = false;
                        continue;
                    }
                    foreach (DataGridViewCell cell in row.Cells)
                    {
                        if (cell.Value != null && filter.IsMatch(cell.Value.ToString()))
                        {
                            row.Visible = true;
                            break;
                        }
                        row.Visible = false;
                    }
                }
            }

            if (chk_modified.Checked)
            {
                foreach (DataGridViewRow row in Params.Rows)
                {
                    // is it modified? - always show
                    if (_changes.ContainsKey(row.Cells[Command.Index].Value))
                    {
                        row.Visible = true;
                    }
                    else
                    {
                        row.Visible = false;
                    }
                }
            }

            if (chk_none_default.Checked)
            {
                foreach (DataGridViewRow row in Params.Rows)
                {
                    row.Visible = row.Cells[Default_value.Index].Value.ToString() != row.Cells[Value.Index].Value.ToString();
                }
            }

            Params.Visible = true;

            log.InfoFormat("Filter: {0}ms", (DateTime.Now - start).TotalMilliseconds);
        }

        private void BUT_paramfileload_Click(object sender, EventArgs e)
        {
            var filepath = Settings.GetUserDataDirectory() + CMB_paramfiles.Text;

            try
            {
                var data = GitHubContent.GetFileContent("ArduPilot", "ardupilot",
                    ((GitHubContent.FileInfo)CMB_paramfiles.SelectedValue).path);

                File.WriteAllBytes(filepath, data);

                var param2 = ParamFile.loadParamFile(filepath);

                Form paramCompareForm = new ParamCompare(Params, MainV2.comPort.MAV.param, param2);

                ThemeManager.ApplyThemeTo(paramCompareForm);
                if (paramCompareForm.ShowDialog() == DialogResult.OK)
                {
                    CustomMessageBox.Show("Loaded parameters, please make sure you write them!", "Loaded");
                }

                // no activate the user needs to click write.
                //this.Activate();
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show("Failed to load file.\n" + ex);
            }
        }

        private void CMB_paramfiles_SelectedIndexChanged(object sender, EventArgs e)
        {
        }

        private void BUT_reset_params_Click(object sender, EventArgs e)
        {
            if (
                CustomMessageBox.Show("Reset all parameters to default\nAre you sure!!", "Reset",
                    MessageBoxButtons.YesNo) == (int)DialogResult.Yes)
            {
                try
                {
                    MainV2.comPort.setParam(new[] { "FORMAT_VERSION", "SYSID_SW_MREV" }, 0);
                    Thread.Sleep(1000);
                    MainV2.comPort.doReboot(false, true);
                    MainV2.comPort.BaseStream.Close();


                    CustomMessageBox.Show(
                        "Your board is now rebooting, You will be required to reconnect to the autopilot.");
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                    CustomMessageBox.Show(Strings.ErrorCommunicating + "\n" + ex, Strings.ERROR);
                }
            }
        }

        // Fork patch: was System.Timers.Timer (fires on a thread-pool thread,
        // then synchronous Invoke back to UI — blocks the pool thread for the
        // duration of the heavy filterList() + grid reflow). Replaced with a
        // System.Windows.Forms.Timer that fires directly on the UI thread, so
        // no Invoke is needed. Also shortened debounce 500ms -> 300ms.
        private readonly System.Windows.Forms.Timer _filterTimer = new System.Windows.Forms.Timer { Interval = 300 };
        private bool _filterTimerWired;
        private string cellEditValue;

        private void txt_search_TextChanged(object sender, EventArgs e)
        {
            if (!_filterTimerWired)
            {
                _filterTimer.Tick += FilterTimerOnElapsed;
                _filterTimerWired = true;
            }
            _filterTimer.Stop();   // reset debounce on every keystroke
            _filterTimer.Start();
        }

        public void FilterTimerOnElapsed(object sender, EventArgs elapsedEventArgs)
        {
            _filterTimer.Stop();
            filterList(txt_search.Text);
            optionsControlUpateBounds();
        }

        private void Params_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            // Only process the Description column
            if (e.RowIndex == -1 || startup)
                return;

            if (e.ColumnIndex == Desc.Index)
            {
                try
                {
                    string descStr = Params[e.ColumnIndex, e.RowIndex].Value.ToString();
                    CheckForUrlAndLaunchInBrowser(descStr);
                }
                catch
                {
                }
            }

            if (e.ColumnIndex == Fav.Index)
            {
                var check = Params[e.ColumnIndex, e.RowIndex].EditedFormattedValue;
                var name = Params[Command.Index, e.RowIndex].Value.ToString();

                if (check != null && (bool)check)
                {
                    // add entry
                    Settings.Instance.AppendList("fav_params", name);
                }
                else
                {
                    // remove entry
                    var list = Settings.Instance.GetList("fav_params");
                    Settings.Instance.SetList("fav_params", list.Where(s => s != name));
                }

                Params.Sort(Command, ListSortDirection.Ascending);
            }
        }

        public static void CheckForUrlAndLaunchInBrowser(string stringWithPossibleUrl)
        {
            if (stringWithPossibleUrl == null)
                return;

            foreach (string url in stringWithPossibleUrl.Split(' '))
            {
                Uri uriResult;
                if (Uri.TryCreate(url, UriKind.Absolute, out uriResult) &&
                    (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps))
                {
                    try
                    {
                        // launch the URL in your default browser
                        System.Diagnostics.Process process = new System.Diagnostics.Process();
                        process.StartInfo.UseShellExecute = true;
                        process.StartInfo.FileName = url;
                        process.Start();
                    }
                    catch { }

                    // only handle the first valid URL
                    return;
                }
            }
        }

        private void BUT_commitToFlash_Click(object sender, EventArgs e)
        {
            try
            {
                MainV2.comPort.doCommand((byte)MainV2.comPort.sysidcurrent, (byte)MainV2.comPort.compidcurrent, MAVLink.MAV_CMD.PREFLIGHT_STORAGE, 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f);
            }
            catch
            {
                CustomMessageBox.Show("Invalid command");
                return;
            }

            CustomMessageBox.Show("Parameters committed to non-volatile memory");
            return;
        }

        private void chk_filter_CheckedChanged(object sender, EventArgs e)
        {
            FilterTimerOnElapsed(null, null);
        }

        private void Params_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            cellEditValue = Params[e.ColumnIndex, e.RowIndex].Value.ToString();
        }

        private void BUT_refreshTable_Click(object sender, EventArgs e)
        {
            startup = true;
            processToScreen();
            startup = false;
        }

        private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
        {
            string txt = treeView1.SelectedNode.Text + "_";
            if (txt == "All_") txt = "";
            filterPrefix = txt;
            FilterTimerOnElapsed(null, null);
        }

        private void but_collapse_Click(object sender, EventArgs e)
        {
            if (splitContainer1.Panel1Collapsed)
            {
                but_collapse.Text = "<";
                splitContainer1.Panel1Collapsed = false;
                BuildTree();
            }
            else
            {
                but_collapse.Text = ">";
                splitContainer1.Panel1Collapsed = true;
                filterPrefix = "";
                FilterTimerOnElapsed(null, null);
            }
        }

        Control optionsControl;
        // Create and place the relevant control in the options column when a row is entered
        private void Params_RowEnter(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0)
                return;

            if (optionsControl != null)
            {
                try
                {
                    Params.Controls.Remove(optionsControl);
                    optionsControl.Dispose();
                } catch { }
                optionsControl = null;
            }

            string param_name = Params[Command.Index, e.RowIndex].Value.ToString();
            string vehicle = MainV2.comPort.MAV.cs.firmware.ToString();
            var options = ParameterMetaDataRepository.GetParameterOptionsInt(param_name, vehicle);
            var bitmask = ParameterMetaDataRepository.GetParameterBitMaskInt(param_name, vehicle);
            // If this is a bitmask, create a button to open the bitmask editor
            // (this is better than trying to cram the bitmask checkboxes into the small cell)
            if (bitmask.Count > 0)
            {
                optionsControl = new MyButton() { Text = "Set Bitmask" };
                optionsControl.Click += (s, a) =>
                {
                    var mcb = new MavlinkCheckBoxBitMask();
                    var list = new MAVLink.MAVLinkParamList();

                    // Try and get type so the correct bitmask to value convertion is done
                    var type = MAVLink.MAV_PARAM_TYPE.INT32;
                    if (MainV2.comPort.MAV.param.ContainsKey(param_name))
                    {
                        type = MainV2.comPort.MAV.param[param_name].TypeAP;
                    }

                    list.Add(new MAVLink.MAVLinkParam(param_name, double.Parse(Params[Value.Index, e.RowIndex].Value.ToString(), CultureInfo.InvariantCulture),
                        type));
                    mcb.setup(param_name, list);
                    mcb.ValueChanged += (o, x, value) =>
                    {
                        Params.CurrentRow.Cells[Value.Index].Value = value;
                        Params.Invalidate();
                        mcb.Focus();
                    };
                    var frm = mcb.ShowUserControl();
                    frm.TopMost = true;
                };

                ThemeManager.ApplyThemeTo(optionsControl);
                optionsControl.Bounds = Params.GetCellDisplayRectangle(Options.Index, e.RowIndex, false);
                Params.Controls.Add(optionsControl);
            }
            // If there are options, create a combo box and populate it with the options
            else if (options.Count > 0)
            {
                ComboBox cmb = new ComboBox() { Dock = DockStyle.Fill };
                cmb.DropDownStyle = ComboBoxStyle.DropDownList;
                cmb.DataSource = options;
                cmb.DisplayMember = "Value";
                cmb.ValueMember = "Key";

                // Widen the dropdown menu if the text is too long
                // https://www.codeproject.com/Articles/5801/Adjust-combo-box-drop-down-list-width-to-longest-s
                cmb.DropDown += (s, ev) =>
                {
                    ComboBox senderComboBox = (ComboBox)s;
                    int width = senderComboBox.DropDownWidth;
                    Graphics g = senderComboBox.CreateGraphics();
                    Font font = senderComboBox.Font;
                    int vertScrollBarWidth =
                        (senderComboBox.Items.Count > senderComboBox.MaxDropDownItems)
                        ? SystemInformation.VerticalScrollBarWidth : 0;

                    int newWidth;
                    foreach (KeyValuePair<int, string> item in ((ComboBox)s).Items)
                    {
                        newWidth = (int)g.MeasureString(item.Value, font).Width
                            + vertScrollBarWidth;
                        if (width < newWidth)
                        {
                            width = newWidth;
                        }
                    }
                    senderComboBox.DropDownWidth = width;
                };

                ThemeManager.ApplyThemeTo(cmb);

                // Create a blank panel to hold the combo box
                // (this blanks out the cell so that the text doesn't peak through)
                optionsControl = new Panel();
                ((Panel)optionsControl).BackColor = Params.Rows[e.RowIndex].InheritedStyle.BackColor;
                optionsControl.Controls.Add(cmb);
                optionsControl.Bounds = Params.GetCellDisplayRectangle(Options.Index, e.RowIndex, false);
                Params.Controls.Add(optionsControl);

                // Populate the current selection from the cell value, if it's valid
                int val = -1;
                if(int.TryParse(Params[Value.Index, e.RowIndex].Value.ToString(), out val))
                {
                    cmb.SelectedValue = val;
                }
                else
                {
                    cmb.SelectedIndex = -1;
                }

                // When the combo box selection changes, update the cell value
                cmb.SelectedIndexChanged += (s, a) =>
                {
                    Params.CurrentRow.Cells[Value.Index].Value = cmb.SelectedValue.ToString();
                    Params.Invalidate();
                };
            }

            // Otherwise, this is a simple numeric parameter
            else
            {
                double min = -32768.0;
                double max = 32768.0;
                if (ParameterMetaDataRepository.GetParameterRange(param_name, ref min, ref max, vehicle))
                {
                    // Default increment is the range divided by 1000, rounded to the nearest power of 10
                    double inc = Math.Pow(10, Math.Floor(Math.Log10((max - min) / 1000)));

                    ParameterMetaDataRepository.GetParameterIncrement(param_name, ref inc, vehicle);

                    NumericUpDown num = new NumericUpDown() { Dock = DockStyle.Fill };

                    // Set the number of decimal places based on the increment, or the minimum value if it's smaller (but not zero)
                    int decimalPlaces = (int)Math.Round(Math.Max(0, -Math.Log10(Math.Abs(inc))));
                    if (Math.Abs(min) < inc && Math.Abs(min) >= 1e-9)
                    {
                        decimalPlaces = (int)Math.Round(Math.Max(0, -Math.Log10(Math.Abs(min))));
                    }
                    num.DecimalPlaces = decimalPlaces;
                    num.Minimum = Math.Round((decimal)min, num.DecimalPlaces);
                    num.Maximum = Math.Round((decimal)max, num.DecimalPlaces);
                    num.Increment = Math.Round((decimal)inc, num.DecimalPlaces);

                    // Parse the cell. Clamp the value to the bounds.
                    decimal val = num.Minimum;
                    decimal.TryParse(Params[Value.Index, e.RowIndex].Value?.ToString(), out val);
                    val = Math.Min(val, num.Maximum);
                    val = Math.Max(val, num.Minimum);
                    num.Value = Math.Round(val, num.DecimalPlaces);

                    // Update the cell if the text in the box changes
                    num.TextChanged += (s, a) =>
                    {
                        Params.CurrentRow.Cells[Value.Index].Value = num.Text;
                        Params.Invalidate();
                    };

                    ThemeManager.ApplyThemeTo(num);

                    optionsControl = new Panel();
                    ((Panel)optionsControl).BackColor = Params.Rows[e.RowIndex].InheritedStyle.BackColor;
                    optionsControl.Controls.Add(num);
                    optionsControl.Bounds = Params.GetCellDisplayRectangle(Options.Index, e.RowIndex, false);
                    Params.Controls.Add(optionsControl);

                }

            }

        }

        // Upate the size and location of our options control whenever a scroll or resize happens
        private void optionsControlUpateBounds()
        {
            if (optionsControl != null)
            {
                if (Params.CurrentRow == null)
                {
                    Params.Controls.Remove(optionsControl);
                    optionsControl.Dispose();
                    optionsControl = null;
                    return;
                }
                var bounds = Params.GetCellDisplayRectangle(Options.Index, Params.CurrentRow.Index, false);
                optionsControl.Bounds = bounds;
                optionsControl.Visible = bounds.Height > 0;
            }
        }

        private void Params_Scroll(object sender, ScrollEventArgs e)
        {
            optionsControlUpateBounds();
        }

        private void Params_RowHeightChanged(object sender, DataGridViewRowEventArgs e)
        {
            if(e.Row == Params.CurrentRow || e.Row.Index + 1 == Params.CurrentRow.Index)
            {
                optionsControlUpateBounds();
            }
        }

        private void Params_ColumnWidthChanged(object sender, DataGridViewColumnEventArgs e)
        {
            if (e.Column.Index == Options.Index || e.Column.Index + 1 == Options.Index)
            {
                optionsControlUpateBounds();
            }
        }
    }

}
