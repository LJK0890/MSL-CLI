using System;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.CLI;

/// <summary>
/// 控制台输出写入器，将日志按级别输出到控制台，并支持 ANSI 颜色着色。
/// </summary>
public class ConsoleOutputWriter : IOutputWriter
{
    // 是否启用 ANSI 颜色输出（仅在终端支持且未显式禁用时启用）
    private readonly bool _useColor;

    /// <summary>
    /// 初始化 <see cref="ConsoleOutputWriter"/> 的新实例，并检测当前环境是否支持颜色输出。
    /// </summary>
    public ConsoleOutputWriter()
    {
        // 仅在标准输出未重定向且显式启用颜色时使用
        _useColor = !Console.IsOutputRedirected &&
                    Environment.GetEnvironmentVariable("NO_COLOR")?.Equals("true", StringComparison.OrdinalIgnoreCase) != true &&
                    TryEnableAnsi();
    }

    /// <summary>
    /// 尝试在 Windows 上启用 ANSI 虚拟终端处理；Linux/macOS 默认支持。
    /// </summary>
    /// <returns>当前终端是否支持 ANSI 颜色输出。</returns>
    private bool TryEnableAnsi()
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            // Windows 10+ 尝试启用虚拟终端处理
            try
            {
                var handle = Console.Out.GetType().GetProperty("Handle")?.GetValue(Console.Out, null);
                if (handle != null)
                {
                    var handleInt = (IntPtr)handle;
                    const int ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
                    var mode = 0;
                    if (NativeMethods.GetConsoleMode(handleInt, ref mode) && (mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) == 0)
                    {
                        // 尚未开启虚拟终端处理，则开启后写回
                        mode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
                        NativeMethods.SetConsoleMode(handleInt, mode);
                    }
                }
                return true;
            }
            catch { return false; }
        }
        // Linux/macOS 默认支持
        return true;
    }

    /// <summary>
    /// 将一条日志写入控制台，可附带时间戳并依据日志级别着色。
    /// </summary>
    /// <param name="context">日志来源上下文（如模块或服务器名称）。</param>
    /// <param name="level">日志级别。</param>
    /// <param name="message">日志正文内容。</param>
    /// <param name="includeTimestamp">是否在消息前附带当前时间戳，默认为 true。</param>
    public void Write(string context, LogLevel level, string message, bool includeTimestamp = true)
    {
        var timestamp = includeTimestamp ? $"[{DateTime.Now:HH:mm:ss}] " : "";
        var levelStr = level != LogLevel.Debug ? level.ToString() : "DEBUG";
        var prefix = $"[{context}/{levelStr}] ";
        string formatted = $"{prefix}{timestamp}{message}";

        if (_useColor)
        {
            // 支持颜色时先写入 ANSI 颜色码，输出后再重置
            var colorCode = GetAnsiColor(level);
            Console.Write(colorCode);
            Console.WriteLine(formatted);
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine(formatted);
        }
    }

    /// <summary>
    /// 根据日志级别返回对应的 ANSI 颜色码。
    /// </summary>
    /// <param name="level">日志级别。</param>
    /// <returns>对应的 ANSI 转义序列颜色码。</returns>
    private string GetAnsiColor(LogLevel level) => level switch
    {
        LogLevel.Debug => "\x1b[90m",      // 亮黑
        LogLevel.Info => "\x1b[32m",       // 绿色
        LogLevel.Success => "\x1b[92m",    // 亮绿
        LogLevel.Warning => "\x1b[33m",    // 黄色
        LogLevel.Error => "\x1b[31m",      // 红色
        LogLevel.Critical => "\x1b[31;1m", // 亮红加粗
        LogLevel.Fatal => "\x1b[35m",      // 品红
        _ => "\x1b[0m"
    };

    // 简单 P/Invoke 声明（仅 Windows）
    /// <summary>
    /// 控制台模式相关的原生方法声明（仅 Windows 使用）。
    /// </summary>
    private static class NativeMethods
    {
        /// <summary>
        /// 获取指定控制台句柄的当前控制台模式。
        /// </summary>
        /// <param name="hConsoleHandle">控制台句柄。</param>
        /// <param name="lpMode">接收控制台模式的输出参数。</param>
        /// <returns>操作是否成功。</returns>
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern bool GetConsoleMode(IntPtr hConsoleHandle, ref int lpMode);

        /// <summary>
        /// 设置指定控制台句柄的控制台模式。
        /// </summary>
        /// <param name="hConsoleHandle">控制台句柄。</param>
        /// <param name="dwMode">要写入的控制台模式。</param>
        /// <returns>操作是否成功。</returns>
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern bool SetConsoleMode(IntPtr hConsoleHandle, int dwMode);
    }
}
