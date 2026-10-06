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

    // 串行化控制台写入，避免多线程输出互相穿插
    private readonly Lock _consoleLock = new();

    /// <summary>
    /// 初始化 <see cref="ConsoleOutputWriter"/> 的新实例，并检测当前环境是否支持颜色输出。
    /// </summary>
    public ConsoleOutputWriter()
    {
        // 仅在标准输出未重定向、未通过 NO_COLOR 关闭颜色时使用
        // （NO_COLOR 的通行约定是“只要设置了非空值就关闭”，而不是必须等于 "true"）
        var noColor = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        _useColor = !Console.IsOutputRedirected &&
                    !noColor &&
                    TryEnableAnsi();
    }

    /// <summary>
    /// 尝试在 Windows 上启用 ANSI 虚拟终端处理；Linux/macOS 默认支持。
    /// </summary>
    /// <returns>当前终端是否支持 ANSI 颜色输出。</returns>
    private bool TryEnableAnsi()
    {
        if (!OperatingSystem.IsWindows())
            return true; // Linux/macOS 终端默认支持

        try
        {
            // 用 GetStdHandle 拿真正的控制台句柄：
            // 以前用反射找 Console.Out 的 "Handle" 属性，该属性并不存在，
            // 于是模式从未被设置，函数却仍然返回 true —— 旧版 conhost 上会直接打印出转义序列
            var handle = NativeMethods.GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                return false;

            var mode = 0;
            if (!NativeMethods.GetConsoleMode(handle, ref mode))
                return false;

            if ((mode & EnableVirtualTerminalProcessing) == 0 &&
                !NativeMethods.SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
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
        var timestamp = includeTimestamp
            ? $"[{DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}] "
            : "";
        var levelStr = level != LogLevel.Debug ? level.ToString() : "DEBUG";
        var prefix = $"[{context}/{levelStr}] ";
        var formatted = $"{prefix}{timestamp}{message}";

        // 颜色码与正文必须一次写出并加锁：多线程（各服务器输出泵 + 代理 + 控制台）
        // 若分成两次 Console 调用，别的线程可能插在中间，导致串行错乱与颜色串色
        lock (_consoleLock)
        {
            if (_useColor)
                Console.WriteLine(GetAnsiColor(level) + formatted + "\x1b[0m");
            else
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
    /// 标准输出句柄编号（GetStdHandle 参数）。
    /// </summary>
    private const int StdOutputHandle = -11;

    /// <summary>
    /// 启用 ANSI 虚拟终端处理的控制台模式位。
    /// </summary>
    private const int EnableVirtualTerminalProcessing = 0x0004;

    /// <summary>
    /// 控制台模式相关的原生方法声明（仅 Windows 使用）。
    /// </summary>
    private static class NativeMethods
    {
        /// <summary>
        /// 获取标准设备句柄。
        /// </summary>
        /// <param name="nStdHandle">标准设备编号（-11 = 标准输出）。</param>
        /// <returns>设备句柄；失败时返回 INVALID_HANDLE_VALUE。</returns>
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetStdHandle(int nStdHandle);

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
