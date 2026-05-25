using System;
using System.Configuration;
using System.IO;
using System.Xml.Linq;
using System.Linq;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

namespace MissionPlanner.Utilities
{
    public static class ParameterMetaDataRepository
    {
        // Phase 9 fork: was a Microsoft.Extensions MemoryCache gated by a
        // single `lock(_cache)` for every read and write. With ~1500 params
        // * 5+ metaKeys per param looked up by ConfigRawParams + Ardu*
        // tooltip loops + servo setup, the lock became the serialising
        // bottleneck (Parallel.ForEach in ConfigRawParams was effectively
        // sequential). Swap to ConcurrentDictionary -- lock-free reads,
        // no eviction needed since the param metadata is bounded and
        // immutable for the life of the process.
        private static readonly ConcurrentDictionary<string, string> _cache =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Gets the parameter meta data.
        /// </summary>
        /// <param name="nodeKey">The node key.</param>
        /// <param name="metaKey">The meta key.</param>
        /// <returns></returns>
        public static string GetParameterMetaData(string nodeKey, string metaKey, string vechileType)
        {
            var key = nodeKey + "" + metaKey + "" + vechileType;
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            if (vechileType == "PX4")
            {
                var px = ParameterMetaDataRepositoryPX4.GetParameterMetaData(nodeKey, metaKey, vechileType);
                if (!string.IsNullOrEmpty(px))
                    _cache.TryAdd(key, px);
                return px ?? string.Empty;
            }

            var answer = ParameterMetaDataRepositoryAPMpdef.GetParameterMetaData(nodeKey, metaKey, vechileType);
            if (answer == string.Empty)
                answer = ParameterMetaDataRepositoryAPMpdef.GetParameterMetaData(nodeKey, metaKey, "SITL");
            if (answer == string.Empty)
                answer = ParameterMetaDataRepositoryAPMpdef.GetParameterMetaData(nodeKey, metaKey, "AP_Periph");
            if (answer == string.Empty)
                answer = ParameterMetaDataRepositoryAPM.GetParameterMetaData(nodeKey, metaKey, vechileType);

            // Cache both hits AND misses. Caching misses is critical because
            // the lookup just walked four fallback repositories; without
            // this, every miss does the full four-repo retry on every call.
            _cache.TryAdd(key, answer);
            return answer;
        }

        /// <summary>
        /// Return a key, value list off all options selectable
        /// </summary>
        /// <param name="nodeKey"></param>
        /// <returns></returns>
        public static List<KeyValuePair<int, string>> GetParameterOptionsInt(string nodeKey, string vechileType)
        {
            string availableValuesRaw = GetParameterMetaData(nodeKey, ParameterMetaDataConstants.Values, vechileType);
            string[] availableValues = availableValuesRaw.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries);
            if (availableValues.Any())
            {
                var splitValues = new List<KeyValuePair<int, string>>();
                // Add the values to the ddl
                foreach (string val in availableValues)
                {
                    try
                    {
                        string[] valParts = val.Split(new[] {':'});
                        splitValues.Add(new KeyValuePair<int, string>(int.Parse(valParts[0].Trim()),
                            (valParts.Length > 1) ? valParts[1].Trim() : valParts[0].Trim()));
                    }
                    catch
                    {
                        Console.WriteLine("Bad entry in param meta data: " + nodeKey);
                    }
                }
                ;

                return splitValues;
            }

            return new List<KeyValuePair<int, string>>();
        }

        public static List<KeyValuePair<int, string>> GetParameterBitMaskInt(string nodeKey, string vechileType)
        {
            string availableValuesRaw;

            availableValuesRaw = GetParameterMetaData(nodeKey, ParameterMetaDataConstants.Bitmask, vechileType);

            string[] availableValues = availableValuesRaw.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries);
            if (availableValues.Any())
            {
                var splitValues = new List<KeyValuePair<int, string>>();
                // Add the values to the ddl
                foreach (string val in availableValues)
                {
                    try
                    {
                        string[] valParts = val.Split(new[] {':'});
                        splitValues.Add(new KeyValuePair<int, string>(int.Parse(valParts[0].Trim()),
                            (valParts.Length > 1) ? valParts[1].Trim() : valParts[0].Trim()));
                    }
                    catch
                    {
                        Console.WriteLine("Bad entry in param meta data: " + nodeKey);
                    }
                }
                ;

                return splitValues;
            }

            return new List<KeyValuePair<int, string>>();
        }

        public static bool GetParameterRange(string nodeKey, ref double min, ref double max, string vechileType)
        {
            string rangeRaw = ParameterMetaDataRepository.GetParameterMetaData(nodeKey, ParameterMetaDataConstants.Range,
                vechileType);

            string[] rangeParts = rangeRaw.Split(new[] {' '});
            if (rangeParts.Count() == 2)
            {
                double lowerRange;
                if (double.TryParse(rangeParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lowerRange))
                {
                    double upperRange;
                    if (double.TryParse(rangeParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out upperRange))
                    {
                        min = lowerRange;
                        max = upperRange;

                        return true;
                    }
                }
            }

            return false;
        }

        public static bool GetParameterRebootRequired(string nodeKey, string vechileType)
        {
            // set the default answer
            bool answer = false;

            string rebootrequired = ParameterMetaDataRepository.GetParameterMetaData(nodeKey,
                ParameterMetaDataConstants.RebootRequired, vechileType);

            if (!string.IsNullOrEmpty(rebootrequired))
            {
                bool.TryParse(rebootrequired, out answer);
            }

            return answer;
        }

        public static bool GetParameterIncrement(string nodeKey, ref double inc, string vechileType)
        {
            string incrementAmt = ParameterMetaDataRepository.GetParameterMetaData(nodeKey,
                ParameterMetaDataConstants.Increment, vechileType);
            if (incrementAmt.Length == 0) return false;
            float Amt = 0;
            float.TryParse(incrementAmt, NumberStyles.Float, CultureInfo.InvariantCulture, out Amt);
            inc = Amt;
            return true;
        }
    }
}