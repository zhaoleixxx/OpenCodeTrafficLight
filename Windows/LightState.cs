using System;

namespace OpenCodeTrafficLight
{
    internal enum LightMode
    {
        Red = 0,
        Yellow = 1,
        Green = 2,
    }

    internal enum BlinkKind
    {
        None,
        YellowPulse,   // 等你确认：1.6s 呼吸
        RedFast,       // 失败：2Hz 快闪
        GreenSoft,     // 完成未查看：1.0s 柔闪
    }

    /// <summary>与 macOS 版 LightState 一一对应的状态快照。</summary>
    internal sealed class LightState
    {
        public LightMode Mode = LightMode.Red;
        public BlinkKind Blink = BlinkKind.None;
        public string Headline = "正在连接 OpenCode";
        public string Detail = "";
        public bool Connected;
        public int Running;
        public int Perms;
        public int Forms;

        public LightState() { }

        public LightState(LightMode mode, BlinkKind blink, string headline, string detail)
        {
            Mode = mode;
            Blink = blink;
            Headline = headline;
            Detail = detail;
        }

        public bool SameLampAs(LightState other)
        {
            if (other == null) return false;
            return Mode == other.Mode && Blink == other.Blink;
        }

        public string TooltipText
        {
            get
            {
                return string.IsNullOrEmpty(Detail)
                    ? "OpenCode 红绿灯\n" + Headline
                    : "OpenCode 红绿灯\n" + Headline + "\n\n" + Detail;
            }
        }
    }

    /// <summary>灯语常量（与 macOS 版窗口尺寸保持一致，逻辑像素）。</summary>
    internal static class Metrics
    {
        public const float WindowWidth = 52f;
        public const float WindowHeight = 127f;
        public const float HousingInset = 5f;
        public const float LampDiameter = 31f;
        public const float LampGap = 5f;

        public const double FailureWindowSeconds = 600;   // 失败后红灯保持 10 分钟
        public const double DoneWindowSeconds = 90;       // 完成未查看的绿闪持续 90 秒
        public const double PollIntervalSeconds = 1.2;
        public const double WatchIntervalSeconds = 2.0;
        public const double QuitPauseSeconds = 900;       // 手动隐藏后 15 分钟内不再自动弹出
    }
}
