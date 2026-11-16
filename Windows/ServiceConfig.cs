using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenCodeTrafficLight
{
    /// <summary>
    /// OpenCode 本地服务配置（端口 + Basic Auth 密码）。
    ///
    /// macOS / Linux 上路径固定为 ~/.local/state/opencode/service.json。
    /// Windows 上 OpenCode 有两条路径逻辑（XDG 回退 + 新版 UserDirectories），
    /// 因此这里按优先级依次探测，取第一个可用的。
    /// </summary>
    internal sealed class ServiceConfig
    {
        public string BaseUrl { get; private set; }
        public string Password { get; private set; }
        public string FilePath { get; private set; }

        public static ServiceConfig Load()
        {
            foreach (var path in CandidateFiles())
            {
                var config = TryLoadFile(path);
                if (config != null) return config;
            }
            return null;
        }

        private static IEnumerable<string> CandidateFiles()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");

            var fixedPaths = new List<string>();

            // XDG_STATE_HOME 优先（与 OpenCode 的 service.json 写入逻辑一致）
            if (!string.IsNullOrEmpty(xdgState))
                fixedPaths.Add(Path.Combine(xdgState, "opencode", "service.json"));

            // 传统 XDG 回退路径：C:\Users\<user>\.local\state\opencode\service.json
            if (!string.IsNullOrEmpty(home))
                fixedPaths.Add(Path.Combine(home, ".local", "state", "opencode", "service.json"));

            // 新版 UserDirectories：state -> %LOCALAPPDATA%\opencode
            if (!string.IsNullOrEmpty(local))
            {
                fixedPaths.Add(Path.Combine(local, "opencode", "service.json"));
                fixedPaths.Add(Path.Combine(local, "opencode", "state", "service.json"));
            }

            if (!string.IsNullOrEmpty(roaming))
                fixedPaths.Add(Path.Combine(roaming, "opencode", "service.json"));

            foreach (var path in fixedPaths)
                if (File.Exists(path)) yield return path;

            // 多版本共存时会写 service-<hash>.json，取最新的一个
            var dirs = new[]
            {
                xdgState == null ? null : Path.Combine(xdgState, "opencode"),
                string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".local", "state", "opencode"),
                string.IsNullOrEmpty(local) ? null : Path.Combine(local, "opencode"),
                string.IsNullOrEmpty(roaming) ? null : Path.Combine(roaming, "opencode"),
            };

            foreach (var dir in dirs)
            {
                if (dir == null || !Directory.Exists(dir)) continue;
                IEnumerable<string> files;
                try
                {
                    files = Directory.GetFiles(dir, "service*.json")
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .ToList();
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var file in files) yield return file;
            }
        }

        private static ServiceConfig TryLoadFile(string path)
        {
            try
            {
                var text = File.ReadAllText(path);
                var root = Json.Parse(text);
                if (root == null) return null;

                var url = Json.GetString(root, "url");
                var password = Json.GetString(root, "password");
                if (string.IsNullOrEmpty(url) || password == null) return null;

                // 只接受本机回环地址，避免误连到外部服务
                if (!IsLoopback(url)) return null;

                return new ServiceConfig
                {
                    BaseUrl = url.TrimEnd('/'),
                    Password = password,
                    FilePath = path,
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsLoopback(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            var host = uri.Host.ToLowerInvariant();
            return host == "127.0.0.1" || host == "localhost" || host == "::1";
        }
    }
}
