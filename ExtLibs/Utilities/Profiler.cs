using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using log4net;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Phase 10e fork: crash-survivable wall-clock profiler for Mission
    /// Planner. Targets the UI-thread freezes that show up on Wine + Windows.
    ///
    /// Why no cross-thread stack sampling: Thread.Suspend silently no-ops on
    /// Wine .NET (Mono/CoreCLR's reimplementation lacks the kernel-side
    /// SuspendThread plumbing CLR uses on Windows). Verified empirically -
    /// 1000+ sampler iterations produced zero stack frames, zero exceptions.
    ///
    /// Instead, this profiler is marker-driven + heartbeat-based:
    ///   * Profiler.Mark(label) logs &lt;label, ms-since-start, ms-since-prev-mark&gt;.
    ///     Gaps between consecutive marks tell you which span was slow.
    ///   * A UI-thread Forms.Timer increments a heartbeat every 50 ms; a
    ///     background watchdog thread logs FREEZE&lt;n&gt; when the heartbeat
    ///     stalls for more than 300 ms. The freeze entry includes the most
    ///     recent Mark, narrowing the suspect code region.
    ///
    /// Output: %LOCALAPPDATA%\Mission Planner\profile-{yyyyMMdd-HHmmss}.log
    /// Format (tab-separated):
    ///   MARK   ms_since_start   ms_since_prev   label
    ///   BEAT   ms_since_start   beat_count
    ///   FREEZE ms_since_start   gap_ms          last_mark
    ///   STOP   ms_since_start
    ///
    /// Enable via env var MP_PROFILER=1 or Settings["EnableProfiler"]=true.
    /// All writes are AutoFlush so SIGKILL/SIGTERM/crash loses at most the
    /// in-flight write.
    /// </summary>
    public static class Profiler
    {
        private static readonly ILog log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static StreamWriter _out;
        private static Stopwatch _sw;
        private static Thread _watchdog;
        private static volatile bool _stop;

        private static long _lastMarkMs;
        private static string _lastMarkLabel = "(none)";
        private static long _lastBeatMs;
        private static long _beatCount;

        public static bool Enabled { get; private set; }
        public static string OutputPath { get; private set; }

        /// <summary>
        /// Call ONCE from Program.Main early. Safe no-op if not enabled.
        /// uiThread is recorded for diagnostics only (we don't sample it).
        /// </summary>
        public static void Start(Thread uiThread)
        {
            try
            {
                bool envOn = string.Equals(Environment.GetEnvironmentVariable("MP_PROFILER"),
                                           "1", StringComparison.Ordinal);
                bool settingOn = false;
                try { settingOn = Settings.Instance.GetBoolean("EnableProfiler"); }
                catch { }
                if (!envOn && !settingOn) return;

                string dir;
                try { dir = Settings.GetUserDataDirectory(); }
                catch
                {
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Mission Planner");
                }
                Directory.CreateDirectory(dir);
                OutputPath = Path.Combine(dir,
                    "profile-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

                _out = new StreamWriter(
                    new FileStream(OutputPath, FileMode.Create, FileAccess.Write,
                                   FileShare.ReadWrite),
                    new UTF8Encoding(false));
                _out.AutoFlush = true;
                _sw = Stopwatch.StartNew();

                Console.WriteLine("[Profiler] writing to {0}", OutputPath);
                log.Info("Profiler: " + OutputPath);

                WriteHeader(uiThread);
                _lastBeatMs = 0;

                _watchdog = new Thread(WatchdogLoop)
                {
                    Name = "MP-Profiler-Watchdog",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                };
                _watchdog.Start();
                Enabled = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Profiler] Start failed: {0}", ex.Message);
                try { _out?.Dispose(); } catch { }
                _out = null;
            }
        }

        /// <summary>
        /// Call from a UI-thread Timer (50 ms interval) Tick handler. The
        /// watchdog thread monitors this counter; if it stops incrementing
        /// for &gt;300 ms a FREEZE entry is written. Wire-up lives in the
        /// MissionPlanner.exe project (Utilities cannot reference WinForms).
        /// </summary>
        public static void Beat()
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _beatCount);
            Volatile.Write(ref _lastBeatMs, _sw.ElapsedMilliseconds);
        }

        public static void Stop()
        {
            if (!Enabled) return;
            _stop = true;
            try { _watchdog?.Join(500); } catch { }
            try
            {
                _out?.WriteLine("STOP\t{0}", _sw.ElapsedMilliseconds);
                _out?.Flush();
                _out?.Dispose();
            }
            catch { }
            Enabled = false;
        }

        /// <summary>
        /// Mark a span boundary. Writes the gap since the previous mark,
        /// making slow spans visually obvious in the log. Callable from any
        /// thread. Cheap (single locked write).
        /// </summary>
        public static void Mark(string label)
        {
            if (!Enabled || _out == null) return;
            try
            {
                long now = _sw.ElapsedMilliseconds;
                long prev = Interlocked.Exchange(ref _lastMarkMs, now);
                _lastMarkLabel = label;
                lock (_out)
                {
                    _out.Write("MARK\t");
                    _out.Write(now);
                    _out.Write('\t');
                    _out.Write(now - prev);
                    _out.Write('\t');
                    _out.WriteLine(label);
                }
            }
            catch { }
        }

        private static void WriteHeader(Thread uiThread)
        {
            _out.WriteLine("# Mission Planner profile (marks + heartbeat)");
            _out.WriteLine("# started: {0:O}", DateTime.UtcNow);
            _out.WriteLine("# pid: {0}", Process.GetCurrentProcess().Id);
            _out.WriteLine("# ui-thread-id: {0}", uiThread?.ManagedThreadId);
            _out.WriteLine("# format (tab-sep):");
            _out.WriteLine("#   MARK    ms_total  ms_gap   label");
            _out.WriteLine("#   BEAT    ms_total  count");
            _out.WriteLine("#   FREEZE  ms_total  ms_gap   last_mark");
            _out.WriteLine("#   STOP    ms_total");
            _out.Flush();
        }

        private static void WatchdogLoop()
        {
            long lastReportedBeat = 0;
            long lastFreezeReportMs = -10000;
            const long FREEZE_THRESHOLD_MS = 300;

            while (!_stop)
            {
                Thread.Sleep(100);
                try
                {
                    long now = _sw.ElapsedMilliseconds;
                    long beatMs = Volatile.Read(ref _lastBeatMs);
                    long count = Interlocked.Read(ref _beatCount);

                    // Periodic heartbeat log every ~2s so we see it ticking.
                    if (count != lastReportedBeat && (now % 2000) < 100)
                    {
                        lock (_out) { _out.WriteLine("BEAT\t{0}\t{1}", now, count); }
                        lastReportedBeat = count;
                    }

                    // Freeze detection: if the UI heartbeat hasn't fired
                    // recently, the UI thread is stuck.
                    long gap = now - beatMs;
                    if (count > 0 && gap > FREEZE_THRESHOLD_MS &&
                        (now - lastFreezeReportMs) > 200)
                    {
                        lock (_out)
                        {
                            _out.WriteLine("FREEZE\t{0}\t{1}\t{2}", now, gap, _lastMarkLabel);
                        }
                        lastFreezeReportMs = now;
                    }
                }
                catch { }
            }
        }
    }
}
