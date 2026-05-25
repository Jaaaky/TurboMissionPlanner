using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using log4net;
using MissionPlanner.Utilities;

namespace MissionPlanner.Mavlink
{
    public class MAVAuthKeys
    {
        private static readonly ILog log =
    LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Phase 10p4 fork: separate keyfile name from upstream so users who
        // multi-boot between this fork and the official MissionPlanner do
        // not destroy each other's MAVLink signing keys. Upstream uses
        // authkeys.xml + NIC MAC AES; the fork uses a salt-derived AES
        // (Crypto.cs / MachineId in HKCU). If the two ever opened the same
        // file each would fail to decrypt and the Phase 10i corrupt-file
        // fallback would shuffle the file aside. With separate names both
        // installs keep their own working keystore.
        // One-time migration: if turbomp-authkeys.xml is missing and the
        // legacy authkeys.xml exists, copy it over so existing fork users
        // don't have to re-add their keys after this rename.
        static string keyfile = Settings.GetUserDataDirectory() + "turbomp-authkeys.xml";
        static string legacyKeyfile = Settings.GetUserDataDirectory() + "authkeys.xml";

        static Crypto Rij = new Crypto();

        public static AuthKeys Keys = new AuthKeys();

        //https://msdn.microsoft.com/en-us/library/aa347850(v=vs.110).aspx

        [CollectionDataContract(ItemName = "AuthKeys", Namespace = "")]
        public class AuthKeys : Dictionary<string, AuthKey>
        {
        }

        [DataContract(Name = "AuthKey", Namespace = "")]
        public struct AuthKey
        {
            [DataMember()]
            public string Name;
            [DataMember()]
            public byte[] Key;
        }

        // Phase 8 fix (ArduPilot/MissionPlanner#3694): tracks whether Load()
        // actually succeeded. If false, Save() refuses to overwrite the
        // existing keyfile so we never destroy keys we simply couldn't
        // decrypt (e.g. after a NIC enumeration change made the old MAC-
        // derived AES key unusable).
        private static bool _loaded = false;

        static MAVAuthKeys()
        {
            Load();
        }

        public static void AddKey(string name, string seed)
        {
            // sha the user input string
            using (SHA256CryptoServiceProvider signit = new SHA256CryptoServiceProvider())
            {
                var shauser = signit.ComputeHash(Encoding.UTF8.GetBytes(seed));
                Array.Resize(ref shauser, 32);

                Keys[name] = new AuthKey() {Key = shauser, Name = name};
            }
        }

        public static void Save()
        {
            // Phase 8 fix (#3694): refuse to overwrite the keyfile if Load()
            // never succeeded. Otherwise we'd silently destroy the user's
            // stored keys whenever decryption failed (e.g. NIC reordering
            // changed the MAC-derived AES key).
            Console.WriteLine("[MAVAuthKeys] Save() called, _loaded={0}, Keys.Count={1}, file exists={2}",
                _loaded, Keys?.Count, File.Exists(keyfile));
            if (!_loaded && File.Exists(keyfile))
            {
                Console.WriteLine("[MAVAuthKeys] Save() REFUSED: Load() never succeeded; not overwriting existing key file.");
                log.Error("MAVAuthKeys.Save() refused: Load() never succeeded; " +
                          "not overwriting existing key file to avoid data loss.");
                return;
            }

            // Atomic write: encrypt to a .tmp file, then File.Replace into
            // place with a .bak fallback. Avoids leaving a half-written file
            // if the process is killed mid-write.
            var tmpfile = keyfile + ".tmp";
            var bakfile = keyfile + ".bak";

            DataContractSerializer writer =
                new DataContractSerializer(typeof(AuthKeys),
                    new Type[] {typeof (AuthKey)});

            using (var fs = new FileStream(tmpfile, FileMode.Create))
            using (var sw = new CryptoStream(fs, Rij.algorithm.CreateEncryptor(), CryptoStreamMode.Write))
            {
                writer.WriteObject(sw, Keys);
            }

            long tmpSize = -1;
            try { tmpSize = new FileInfo(tmpfile).Length; } catch { }
            if (File.Exists(keyfile))
            {
                File.Replace(tmpfile, keyfile, bakfile);
                Console.WriteLine("[MAVAuthKeys] Save() REPLACED keyfile (tmp {0} bytes -> {1})", tmpSize, keyfile);
            }
            else
            {
                File.Move(tmpfile, keyfile);
                Console.WriteLine("[MAVAuthKeys] Save() CREATED keyfile {0} ({1} bytes)", keyfile, tmpSize);
            }
        }

        internal static void Load()
        {
            Console.WriteLine("[MAVAuthKeys] Load() keyfile = {0}", keyfile);
            // Phase 10p4 fork: one-time migration from upstream-name file.
            // If turbomp-authkeys.xml is missing but legacy authkeys.xml is
            // present, copy it across so the user's existing signing keys
            // survive the rename. Copy (not Move) so the upstream install
            // keeps its file working too if the user dual-boots.
            if (!File.Exists(keyfile) && File.Exists(legacyKeyfile))
            {
                try
                {
                    File.Copy(legacyKeyfile, keyfile, false);
                    Console.WriteLine("[MAVAuthKeys] Migrated legacy {0} -> {1}", legacyKeyfile, keyfile);
                    log.Info("MAVAuthKeys: migrated legacy authkeys.xml to turbomp-authkeys.xml");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[MAVAuthKeys] legacy migration failed: {0}", ex.Message);
                }
            }
            if (!File.Exists(keyfile))
            {
                // No existing file means Save() is free to create one.
                Console.WriteLine("[MAVAuthKeys] Load(): no keyfile present, _loaded=true (Save will create)");
                _loaded = true;
                return;
            }
            long sz = -1;
            try { sz = new FileInfo(keyfile).Length; } catch { }
            Console.WriteLine("[MAVAuthKeys] Load(): keyfile exists, {0} bytes", sz);

            try
            {

                DataContractSerializer reader =
                    new DataContractSerializer(typeof (AuthKeys),
                        new Type[] {typeof (AuthKey)});

                // Phase 10l fork: Load opens read-only + ShareReadWrite so a
                // concurrent Save() (atomic tmp + File.Replace, separate fd)
                // doesn't get blocked by our handle.
                using (var fs = new FileStream(keyfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new CryptoStream(fs, Rij.algorithm.CreateDecryptor(), CryptoStreamMode.Read))
                {
                    Keys = (AuthKeys) reader.ReadObject(sr);
                }
                _loaded = true;
                Console.WriteLine("[MAVAuthKeys] Load() SUCCESS: {0} key(s) loaded", Keys?.Count);
            }
            catch (Exception ex)
            {
                // Phase 10i fork: decryption failed (e.g. an upstream-era
                // authkeys.xml encrypted with the old NIC MAC-derived AES
                // key, vs the salt-derived key the fork uses now). The file
                // is unrecoverable; preserving it forever via _loaded=false
                // would trap the user in "Save refuses" purgatory and they
                // can never add new keys. Move the corrupt file aside ONCE
                // with a .corrupt-<timestamp> suffix so we don't silently
                // destroy data, then set _loaded=true so future Save() calls
                // can write a fresh keyfile with the user's new keys.
                Console.WriteLine("[MAVAuthKeys] Load() FAILED -> {0}: {1}", ex.GetType().Name, ex.Message);
                log.Error(ex);
                try
                {
                    var corruptPath = keyfile + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                    File.Move(keyfile, corruptPath);
                    Console.WriteLine("[MAVAuthKeys] Moved un-decryptable file aside -> {0}", corruptPath);
                    Console.WriteLine("[MAVAuthKeys] Save() WILL now create a fresh keyfile (existing keys lost; the .corrupt-* file is kept in case salt is recovered later)");
                    log.Warn("MAVAuthKeys: existing authkeys.xml could not be decrypted (probably encrypted with a different salt); moved to " + corruptPath + " and starting fresh.");
                    Keys = new AuthKeys();
                    _loaded = true;
                }
                catch (Exception exMv)
                {
                    Console.WriteLine("[MAVAuthKeys] could not move corrupt file: {0}; _loaded stays false to be safe", exMv.Message);
                    log.Error("MAVAuthKeys: failed to set aside corrupt keyfile", exMv);
                }
            }
        }
    }
}
