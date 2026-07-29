// LunchHelper — 触控锁屏助手
// Copyright (C) 2026  LunchHelper Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace LunchHelper
{
    /// <summary>
    /// 配置读写：配置文件位于程序同目录 config.json。
    /// 应急密码仅以 PBKDF2-HMAC-SHA256 哈希形式存储（每安装随机盐），绝不保存明文。
    /// </summary>
    internal static class ConfigManager
    {
        public const string AppName = "LunchHelper";
        public const string Version = "1.0.0";

        private static string ConfigPath
        {
            get
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(dir, "config.json");
            }
        }

        public static Config Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    using (var fs = File.OpenRead(ConfigPath))
                    {
                        var ser = new DataContractJsonSerializer(typeof(Config));
                        var cfg = ser.ReadObject(fs) as Config;
                        if (cfg != null)
                        {
                            cfg.Normalize();
                            return cfg;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("读取配置失败，回退默认配置: " + ex.Message);
            }
            return Default();
        }

        public static void Save(Config cfg)
        {
            try
            {
                cfg.Normalize();
                using (var fs = File.Create(ConfigPath))
                {
                    var ser = new DataContractJsonSerializer(typeof(Config));
                    ser.WriteObject(fs, cfg);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("保存配置失败: " + ex.Message);
                throw;
            }
        }

        public static Config Default()
        {
            var cfg = new Config
            {
                LockSeconds = 10,
                LogRetentionDays = 14,
                Slogan = "",
                PinLength = 6
            };
            string hash, salt;
            ComputePasswordHash("000000", out hash, out salt);
            cfg.PasswordHash = hash;
            cfg.PasswordSalt = salt;
            cfg.PasswordIterations = DefaultIterations;
            return cfg;
        }

        public static void ResetToDefault()
        {
            Save(Default());
        }

        /// <summary>应急密码 PBKDF2 迭代次数（1 万次，解锁瞬时且无感知安全退步）。</summary>
        public const int DefaultIterations = 10000;

        /// <summary>应急密码最小长度下限（公开规则，绝非用户实际密码位数）。锁屏短码彩蛋与配置校验共用此常量。</summary>
        public const int MinPinLength = 4;

        /// <summary>应急密码最大长度上限（公开规则）。</summary>
        public const int MaxPinLength = 12;

        /// <summary>计算应急密码的 PBKDF2-HMAC-SHA256 哈希并生成每安装唯一的随机盐。</summary>
        public static void ComputePasswordHash(string plain, out string hash, out string salt)
        {
            byte[] saltBytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(saltBytes);
            salt = Convert.ToBase64String(saltBytes);
            hash = Pbkdf2(plain, saltBytes, DefaultIterations);
        }

        private static string Pbkdf2(string plain, byte[] saltBytes, int iterations)
        {
            using (var derive = new Rfc2898DeriveBytes(plain ?? "", saltBytes, iterations, HashAlgorithmName.SHA256))
                return Convert.ToBase64String(derive.GetBytes(32));
        }

        public static bool VerifyPassword(string plain, string hash, string salt, int iterations)
        {
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt)) return false;
            byte[] saltBytes;
            try { saltBytes = Convert.FromBase64String(salt); }
            catch { return false; }
            string computed = Pbkdf2(plain, saltBytes, iterations);
            return FixedTimeEquals(computed, hash);
        }

        /// <summary>定长时间比较，降低时序侧信道风险。</summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    [DataContract]
    internal class Config
    {
        [DataMember(Name = "lockSeconds")]
        public int LockSeconds { get; set; } = 10;

        [DataMember(Name = "passwordHash")]
        public string PasswordHash { get; set; } = "";

        [DataMember(Name = "passwordSalt")]
        public string PasswordSalt { get; set; } = "";

        [DataMember(Name = "passwordIterations")]
        public int PasswordIterations { get; set; } = ConfigManager.DefaultIterations;

        [DataMember(Name = "logRetentionDays")]
        public int LogRetentionDays { get; set; } = 14;

        [DataMember(Name = "slogan")]
        public string Slogan { get; set; } = "";

        /// <summary>是否启用 UI Access 超级置顶（需提权/UAC 或受保护目录）。默认 false：以普通置顶锁屏。</summary>
        [DataMember(Name = "enableUiAccess")]
        public bool EnableUiAccess { get; set; } = false;

        /// <summary>应急密码位数（4–12）。由配置界面设置密码时按实际位数推导；此处仅作合法范围兜底。</summary>
        [DataMember(Name = "pinLength")]
        public int PinLength { get; set; } = 6;

        /// <summary>将非法值修正为合法默认值，保证程序健壮性。</summary>
        public void Normalize()
        {
            if (LockSeconds < 1) LockSeconds = 10;
            if (LogRetentionDays < 0) LogRetentionDays = 0;
            if (string.IsNullOrEmpty(PasswordHash) || string.IsNullOrEmpty(PasswordSalt))
            {
                string hash, salt;
                ConfigManager.ComputePasswordHash("000000", out hash, out salt);
                PasswordHash = hash;
                PasswordSalt = salt;
                PasswordIterations = ConfigManager.DefaultIterations;
            }
            else if (PasswordIterations <= 0)
            {
                // Legacy config: hash was derived with 100k iterations and the count
                // was not stored. Keep verifying at 100k so existing passwords still work.
                PasswordIterations = 100000;
            }
            if (Slogan == null) Slogan = "";
            if (PinLength < ConfigManager.MinPinLength || PinLength > ConfigManager.MaxPinLength) PinLength = 6;
        }
    }
}
