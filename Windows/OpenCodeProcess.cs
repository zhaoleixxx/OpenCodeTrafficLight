using System;
using System.Diagnostics;
using System.Management;

namespace OpenCodeTrafficLight
{
    /// <summary>
    /// Windows 版 watcher：替代 macOS 的 watcher.sh + LaunchAgent。
    /// 每 2 秒检查是否存在交互式 opencode 进程：
    ///   - 有 → 显示悬浮窗
    ///   - 全部关闭 → 隐藏悬浮窗并清除「手动隐藏」标记
    /// 后台常驻服务 `opencode serve --service` 不算一次终端会话，必须排除。
    /// </summary>
    internal static class OpenCodeProcess
    {
        private static readonly string[] ProcessNames = { "opencode", "OpenCode" };

        public static bool IsRunning()
        {
            foreach (var name in ProcessNames)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(name);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var process in processes)
                {
                    using (process)
                    {
                        try
                        {
                            var commandLine = GetCommandLine(process.Id);

                            // 读不到命令行时不视为交互式会话，避免后台服务让红绿灯永远亮着
                            if (commandLine == null) continue;

                            if (IsServiceProcess(commandLine)) continue;
                            return true;
                        }
                        catch (Exception)
                        {
                            // 进程已退出 / 权限不足：忽略
                        }
                    }
                }
            }
            return false;
        }

        private static bool IsServiceProcess(string commandLine)
        {
            return commandLine.IndexOf("--service", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetCommandLine(int processId)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + processId))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        using (item)
                        {
                            return item["CommandLine"] as string;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // WMI 不可用
            }
            return null;
        }
    }
}
