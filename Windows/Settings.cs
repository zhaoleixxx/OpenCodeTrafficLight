using System;
using System.Collections.Generic;
using System.IO;

namespace OpenCodeTrafficLight
{
    /// <summary>窗口位置与置顶偏好，存于 %APPDATA%\OpenCodeTrafficLight\settings.json。</summary>
    internal sealed class Settings
    {
        public int X = int.MinValue;
        public int Y = int.MinValue;
        public bool TopMost = true;

        public static string Directory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OpenCodeTrafficLight");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(Directory, "settings.json"); }
        }

        public static string QuitFlagPath
        {
            get { return Path.Combine(Directory, "hidden-flag"); }
        }

        public static Settings Load()
        {
            try
            {
                var root = Json.Parse(File.ReadAllText(FilePath));
                if (root == null) return new Settings();

                var settings = new Settings();
                var x = Json.GetNumber(root, "x");
                var y = Json.GetNumber(root, "y");
                if (x != null) settings.X = (int)x.Value;
                if (y != null) settings.Y = (int)y.Value;
                settings.TopMost = Json.GetBool(root, "topMost", true);
                return settings;
            }
            catch (Exception)
            {
                return new Settings();
            }
        }

        public void Save()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var payload = new Dictionary<string, object>
                {
                    { "x", X },
                    { "y", Y },
                    { "topMost", TopMost },
                };
                File.WriteAllText(FilePath, Json.Serialize(payload));
            }
            catch (Exception)
            {
                // 位置保存失败不影响运行
            }
        }

        public static void MarkHidden()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(QuitFlagPath, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            }
            catch (Exception) { }
        }

        public static void ClearHidden()
        {
            try
            {
                if (File.Exists(QuitFlagPath)) File.Delete(QuitFlagPath);
            }
            catch (Exception) { }
        }

        /// <summary>手动隐藏后的暂停是否仍然有效（15 分钟）。</summary>
        public static bool IsHiddenPauseActive()
        {
            try
            {
                if (!File.Exists(QuitFlagPath)) return false;
                var text = File.ReadAllText(QuitFlagPath).Trim();
                long stamp;
                if (!long.TryParse(text, out stamp)) { File.Delete(QuitFlagPath); return false; }

                var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - stamp;
                if (age >= Metrics.QuitPauseSeconds)
                {
                    File.Delete(QuitFlagPath);
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
