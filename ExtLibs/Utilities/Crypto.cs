using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace MissionPlanner.Utilities
{
    public sealed class Crypto : IDisposable
    {
        private static readonly byte[] Key =
        {
            0xd1, 0x3c, 0x35, 0x6f, 0xb5, 0xd, 0x87, 0xf0,
            0x92, 0x07, 0x6d, 0xab, 0x76, 0x82, 0x36, 0xa,
            0x13, 0x5a, 0x77, 0xfe, 0x77, 0xf3, 0x7f, 0xa8,
            0xa4, 0x04, 0x11, 0x46, 0x68, 0x2d, 0x48, 0xa1
        };

        private static readonly byte[] IV =
        {
            0x6d, 0x2d, 0xf5, 0x34, 0xc7, 0x60, 0xc5, 0x33,
            0xe2, 0xa3, 0xd7, 0xc3, 0xf3, 0x39, 0xf2, 0x16
        };

        /// <summary>
        /// Abstract object
        /// </summary>
        public SymmetricAlgorithm algorithm;

        /// <summary>
        /// Default constructor
        /// </summary>
        public Crypto()
        {
            // Phase 8 fix (ArduPilot/MissionPlanner#3694): upstream derived
            // the AES-256 Key + IV from the first NIC's MAC address. That
            // enumeration order is not stable: VPN connect/disconnect, USB
            // NIC plug, Hyper-V virtual adapter creation, or a Wine prefix
            // rebuild all change which adapter appears first, so the key
            // changes, decryption fails, and (combined with the bug fixed in
            // MAVAuthKeys.cs) destroys all stored signing keys.
            //
            // Replace with a stable per-install random salt persisted to
            // GetUserDataDirectory()/crypto.salt. Same machine = same key
            // for the life of that file. Works identically on Windows,
            // Linux/Mono and Wine -- no NIC enumeration involved.

            byte[] salt = LoadOrCreateMachineId();
            string saltSha = "?";
            try
            {
                using (var s = SHA256.Create()) saltSha = BitConverter.ToString(s.ComputeHash(salt)).Replace("-", "").Substring(0, 16);
            }
            catch { }
            Console.WriteLine("[Crypto] salt {0} bytes, sha256(salt) first8={1}", salt.Length, saltSha);
            try
            {
                using (var sha = SHA256.Create())
                {
                    var keyDerived = sha.ComputeHash(salt);
                    Array.Copy(keyDerived, Key, Math.Min(keyDerived.Length, Key.Length));
                }
                using (var md5 = MD5.Create())
                {
                    var ivDerived = md5.ComputeHash(salt);
                    Array.Copy(ivDerived, IV, Math.Min(ivDerived.Length, IV.Length));
                }
                Console.WriteLine("[Crypto] derived key first8={0} iv first8={1} (salt path)",
                    BitConverter.ToString(Key, 0, 8).Replace("-", ""),
                    BitConverter.ToString(IV, 0, 8).Replace("-", ""));
            }
            catch (Exception exDerive)
            {
                // If anything went wrong leave the static Key/IV defaults
                // in place (still better than the MAC-derived path).
                Console.WriteLine("[Crypto] DERIVE FAILED, using STATIC hardcoded Key/IV (this WILL break authkeys.xml decryption): {0}", exDerive.Message);
            }

            this.algorithm = new RijndaelManaged();
            this.algorithm.Mode = CipherMode.CBC;
            this.algorithm.Padding = PaddingMode.PKCS7;
            this.algorithm.Key = Key;
            this.algorithm.IV = IV;
        }

        // Phase 10l fork: machine ID lives in HKCU\Software\MissionPlanner.
        // Registry is per-user, survives reinstall + app-dir wipe, doesn't
        // depend on NIC MAC, doesn't depend on the Documents dir layout.
        // Written EXACTLY ONCE (when missing); never overwritten while it
        // exists. Random 32 bytes from RNGCryptoServiceProvider.
        //
        // Migration: if the legacy crypto.salt file (Phase 9e) exists and
        // the registry doesn't, we adopt the salt-file value into the
        // registry. Avoids invalidating already-encrypted authkeys.xml
        // for users who installed the prior fork build.
        private const string MachineIdKeyPath = @"Software\MissionPlanner";
        private const string MachineIdValueName = "MachineId";
        private static readonly object _idLock = new object();

        private static byte[] LoadOrCreateMachineId()
        {
            lock (_idLock)
            {
                // 1. Try HKCU registry (preferred).
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(MachineIdKeyPath, false))
                    {
                        if (k != null)
                        {
                            var v = k.GetValue(MachineIdValueName) as byte[];
                            if (v != null && v.Length >= 16)
                            {
                                Console.WriteLine("[Crypto] MachineId LOADED from HKCU\\{0}\\{1} ({2} bytes)",
                                    MachineIdKeyPath, MachineIdValueName, v.Length);
                                return v;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Crypto] MachineId registry read failed: {0}", ex.Message);
                }

                // 2. Migrate legacy crypto.salt file (Phase 9e), if present.
                try
                {
                    string saltFile = Settings.GetUserDataDirectory() + "crypto.salt";
                    if (File.Exists(saltFile))
                    {
                        var bytes = File.ReadAllBytes(saltFile);
                        if (bytes.Length >= 16)
                        {
                            Console.WriteLine("[Crypto] MachineId MIGRATING legacy crypto.salt -> registry");
                            WriteMachineIdToRegistry(bytes);
                            return bytes;
                        }
                    }
                }
                catch (Exception exSalt)
                {
                    Console.WriteLine("[Crypto] MachineId salt-file migration probe failed: {0}", exSalt.Message);
                }

                // 3. Generate fresh + persist to registry.
                var fresh = new byte[32];
                try
                {
                    using (var rng = RandomNumberGenerator.Create())
                        rng.GetBytes(fresh);
                    Console.WriteLine("[Crypto] MachineId GENERATED fresh ({0} bytes), writing to registry", fresh.Length);
                    WriteMachineIdToRegistry(fresh);
                }
                catch (Exception exGen)
                {
                    Console.WriteLine("[Crypto] MachineId GENERATE/PERSIST failed: {0} -- ephemeral this run, authkeys.xml decryption will likely fail next run", exGen.Message);
                }
                return fresh;
            }
        }

        private static void WriteMachineIdToRegistry(byte[] value)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(MachineIdKeyPath))
                {
                    if (k == null)
                    {
                        Console.WriteLine("[Crypto] MachineId registry CreateSubKey returned null");
                        return;
                    }
                    k.SetValue(MachineIdValueName, value, RegistryValueKind.Binary);
                    Console.WriteLine("[Crypto] MachineId WROTE to HKCU\\{0}\\{1} ({2} bytes)",
                        MachineIdKeyPath, MachineIdValueName, value.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Crypto] MachineId registry write failed: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Release all resources used by the SymmetricAlgorithm class
        /// </summary>
        public void Dispose()
        {
            this.algorithm.Clear();
        }

        /// <summary>
        /// Set Binary Keys
        /// </summary>
        public void SetBinaryKeys(byte[] Key, byte[] IV)
        {
            this.algorithm.Key = Key;
            this.algorithm.IV = IV;
        }

        /// <summary>
        /// Extract Binary Keys
        /// </summary>
        public void ExtractBinaryKeys(out byte[] Key, out byte[] IV)
        {
            Key = this.algorithm.Key;
            IV = this.algorithm.IV;
        }

        /// <summary>
        /// Process the data with CryptoStream
        /// </summary>
        byte[] Process(byte[] data, int startIndex, int count, ICryptoTransform cryptor)
        {
            //
            // the memory stream granularity must match the block size
            // of the current cryptographic operation
            //
            int capacity = count;
            int mod = count%algorithm.BlockSize;
            if (mod > 0)
            {
                capacity += (algorithm.BlockSize - mod);
            }

            MemoryStream memoryStream = new MemoryStream(capacity);

            CryptoStream cryptoStream = new CryptoStream(
                memoryStream,
                cryptor,
                CryptoStreamMode.Write);

            cryptoStream.Write(data, startIndex, count);
            cryptoStream.FlushFinalBlock();

            cryptoStream.Close();
            cryptoStream = null;

            cryptor.Dispose();
            cryptor = null;

            return memoryStream.ToArray();
        }

        /// <summary>
        ///  Byte array encryption function
        /// </summary>
        /// <param name="cleanBuffer">input byte array</param>
        /// <returns>output encrypted byte array</returns>
        public byte[] EncryptBuffer(byte[] cleanBuffer)
        {
            byte[] output;

            // Encryptor object
            ICryptoTransform cryptoTransform = this.algorithm.CreateEncryptor();

            // Get the result
            output = this.Process(cleanBuffer, 0, cleanBuffer.Length, cryptoTransform);

            //clean
            cryptoTransform.Dispose();

            return output;
        }

        /// <summary>
        ///  Byte array decryption function
        /// </summary>
        /// <param name="cryptoBuffer">input chiper byte array</param>
        /// <returns>output decrypted byte array</returns>
        public byte[] DecryptBuffer(byte[] cryptoBuffer)
        {
            byte[] output;

            // Decryptor object
            ICryptoTransform cryptoTransform = this.algorithm.CreateDecryptor();

            // Get the result   
            output = this.Process(cryptoBuffer, 0, cryptoBuffer.Length, cryptoTransform);

            //clean
            cryptoTransform.Dispose();

            return output;
        }

        /// <summary>
        /// String encryption function
        /// </summary>
        /// <param name="plainText">clean text</param>
        /// <returns>base64 encrypted string</returns>
        public string EncryptString(string plainText)
        {
            return Convert.ToBase64String(EncryptBuffer(Encoding.UTF8.GetBytes(plainText)));
        }

        /// <summary>
        /// String decryption function
        /// </summary>
        /// <param name="encyptedText">base64 encrypted string</param>
        /// <returns>decrypted text</returns>
        public string DecryptString(string encyptedText)
        {
            return Encoding.UTF8.GetString(DecryptBuffer(Convert.FromBase64String(encyptedText)));
        }
    }
}