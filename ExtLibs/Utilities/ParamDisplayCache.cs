using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Phase 10n fork: pre-composed parameter display strings, populated in
    /// the background once the vehicle's parameter list has finished
    /// downloading. ConfigRawParams' Pass 2 enrichment then becomes pure UI
    /// cell assignments (the heavy string composition - AddNewLinesForTooltip
    /// per-char loops, options Replace/Split, range concat - happens off the
    /// UI thread, before the user even opens the Full Parameter List tab).
    ///
    /// Trigger: MainV2 calls TriggerWarm(firmware, paramNames) after
    /// getParamList() returns. Idempotent + debounced - safe to call from
    /// PropertyChanged handlers too.
    /// </summary>
    public static class ParamDisplayCache
    {
        private static readonly ILog log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public class EnrichedParam
        {
            public string Description;
            public string DescriptionTooltip;
            public string Units;
            public string OptionsCellText;
            public string OptionsTooltipText;
        }

        // Volatile so UI thread reads see the latest committed cache after a
        // bg Task completes its assignment.
        private static ConcurrentDictionary<string, EnrichedParam> _cache;
        private static string _cacheFirmware;
        private static int _cacheParamCount;
        private static int _warmGen;
        private static Task _warmTask;
        private static readonly object _gate = new object();

        public static ConcurrentDictionary<string, EnrichedParam> Current
            => Volatile.Read(ref _cache);

        public static string CurrentFirmware => Volatile.Read(ref _cacheFirmware);

        public static bool IsWarmFor(string firmware, int paramCount)
        {
            var c = Volatile.Read(ref _cache);
            if (c == null) return false;
            if (Volatile.Read(ref _cacheFirmware) != firmware) return false;
            // Allow small drift - cache covering >= 95% of params is "warm
            // enough"; ConfigRawParams falls back to live for misses.
            return c.Count * 100 >= paramCount * 95;
        }

        /// <summary>
        /// Kick off (or restart) the background warm-up. Safe to call from
        /// any thread. Coalesces rapid back-to-back calls via _warmGen.
        /// </summary>
        public static void TriggerWarm(string firmware, IEnumerable<string> paramNames)
        {
            if (string.IsNullOrEmpty(firmware) || paramNames == null) return;
            string[] snap;
            try { snap = paramNames.Where(s => !string.IsNullOrEmpty(s)).ToArray(); }
            catch { return; }
            if (snap.Length == 0) return;

            int gen;
            lock (_gate)
            {
                gen = ++_warmGen;
            }

            // Run on the thread pool. Cheap if cache is already hot for this
            // (firmware, count) pair.
            _warmTask = Task.Run(() => BuildCache(firmware, snap, gen));
        }

        private static void BuildCache(string firmware, string[] names, int myGen)
        {
            try
            {
                // Skip if a newer trigger superseded us, or if the existing
                // cache is already valid for this (firmware, count).
                if (myGen != Volatile.Read(ref _warmGen)) return;
                var existing = Volatile.Read(ref _cache);
                if (existing != null
                    && Volatile.Read(ref _cacheFirmware) == firmware
                    && Volatile.Read(ref _cacheParamCount) == names.Length)
                {
                    return;
                }

                var dst = new ConcurrentDictionary<string, EnrichedParam>(
                    Environment.ProcessorCount, names.Length, StringComparer.Ordinal);
                int processed = 0;

                foreach (var name in names)
                {
                    if (myGen != Volatile.Read(ref _warmGen)) return; // superseded
                    try
                    {
                        var desc = ParameterMetaDataRepository.GetParameterMetaData(name,
                            ParameterMetaDataConstants.Description, firmware);
                        if (string.IsNullOrEmpty(desc)) continue;

                        var info = new EnrichedParam
                        {
                            Description = desc,
                            DescriptionTooltip = AddNewLinesForTooltip(desc),
                            Units = ParameterMetaDataRepository.GetParameterMetaData(name,
                                ParameterMetaDataConstants.Units, firmware),
                        };
                        var range = ParameterMetaDataRepository.GetParameterMetaData(name,
                            ParameterMetaDataConstants.Range, firmware) ?? "";
                        var options = ParameterMetaDataRepository.GetParameterMetaData(name,
                            ParameterMetaDataConstants.Values, firmware) ?? "";
                        info.OptionsCellText = (range + "\n" + options.Replace(",", "\n")).Trim();
                        if (options.Length > 0)
                            info.OptionsTooltipText = options.Replace(',', '\n');
                        dst[name] = info;
                    }
                    catch { /* per-param failures are non-fatal */ }
                    processed++;
                }

                // Commit only if we're still the active generation.
                if (myGen != Volatile.Read(ref _warmGen)) return;
                Volatile.Write(ref _cache, dst);
                Volatile.Write(ref _cacheFirmware, firmware);
                Volatile.Write(ref _cacheParamCount, names.Length);
                log.InfoFormat("ParamDisplayCache warmed: fw={0} params={1} enriched={2}",
                    firmware, names.Length, dst.Count);
            }
            catch (Exception ex)
            {
                log.Error("ParamDisplayCache warm failed", ex);
            }
        }

        // Same as ConfigRawParams.AddNewLinesForTooltip - duplicated here to
        // avoid a ExtLibs->MissionPlanner.exe reverse dependency. ~50us per
        // ~100-char description; called ~1500 times = ~75ms on bg thread.
        private static string AddNewLinesForTooltip(string text)
        {
            const int maximumSingleLineTooltipLength = 50;
            if (text.Length < maximumSingleLineTooltipLength) return text;
            var lineLength = (int)Math.Sqrt(text.Length) * 2;
            var sb = new System.Text.StringBuilder(text.Length + 16);
            var currentLinePosition = 0;
            for (var textIndex = 0; textIndex < text.Length; textIndex++)
            {
                if (currentLinePosition >= lineLength
                    && char.IsWhiteSpace(text[textIndex]))
                {
                    sb.Append(Environment.NewLine);
                    currentLinePosition = 0;
                }
                if (currentLinePosition == 0)
                    while (textIndex < text.Length && char.IsWhiteSpace(text[textIndex]))
                        textIndex++;
                if (textIndex < text.Length) sb.Append(text[textIndex]);
                currentLinePosition++;
            }
            return sb.ToString();
        }
    }
}
