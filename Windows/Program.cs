using System;
using System.Threading;
using System.Windows.Forms;

namespace OpenCodeTrafficLight
{
    internal static class Program
    {
        private const string MutexName = @"Local\OpenCodeTrafficLight.SingleInstance";

        [STAThread]
        private static void Main(string[] args)
        {
            bool selfTest = false;
            string fixedState = null;

            foreach (var arg in args)
            {
                if (string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase))
                {
                    selfTest = true;
                }
                else if (arg.StartsWith("--state=", StringComparison.OrdinalIgnoreCase))
                {
                    fixedState = arg.Substring("--state=".Length).ToLowerInvariant();
                    selfTest = true;
                }
            }

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew) return; // 已有实例在跑

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                try
                {
                    Application.Run(new TrafficLightApp(selfTest, fixedState));
                }
                finally
                {
                    try { mutex.ReleaseMutex(); } catch (Exception) { }
                }
            }
        }
    }
}
