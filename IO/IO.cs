using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;

namespace MSL_CLI.IO;

/// <summary>
/// 日志级别定义及颜色样式
/// </summary>
public static class IO
{
    // ---------- ANSI 颜色样式（真彩色） ----------
    public static class Style
    {
        // 重置序列
        public const string Reset = "\x1b[0m";

        // 辅助构建函数
        private static string Fg(byte r, byte g, byte b) => $"\x1b[38;2;{r};{g};{b}m";
        private static string Bg(byte r, byte g, byte b) => $"\x1b[48;2;{r};{g};{b}m";

        public static readonly string Debug = "\x1b[2;" + Fg(127, 127, 127);
        public static readonly string Info = Fg(0, 127, 0);
        public static readonly string Success = Fg(0, 207, 0);
        public static readonly string Warning = Fg(207, 127, 0);
        public static readonly string Error = Fg(207, 0, 0);
        public static readonly string Critical = Fg(207, 0, 0) + Bg(0, 0, 0);
        public static readonly string Fatal = Fg(127, 0, 0);

        // 根据级别获取样式
        public static string GetStyle(LogLevel level) => level switch
        {
            LogLevel.DEBUG => Debug,
            LogLevel.INFO => Info,
            LogLevel.SUCCESS => Success,
            LogLevel.WARNING => Warning,
            LogLevel.ERROR => Error,
            LogLevel.CRITICAL => Critical,
            LogLevel.FATAL => Fatal,
            _ => Reset
        };
    }

    // 日志级别枚举
    public enum LogLevel
    {
        DEBUG,
        INFO,
        SUCCESS,
        WARNING,
        ERROR,
        CRITICAL,
        FATAL
    }

    // 全局输出实例（线程安全）
    public static ConsoleO Output { get; private set; } = new ConsoleO();

    // 全局输入实例（线程安全）
    public static ConsoleI Input { get; private set; } = new ConsoleI();
}