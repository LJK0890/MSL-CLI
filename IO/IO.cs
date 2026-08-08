using System.Collections.Concurrent;
using System.Text;

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
        NULL,
        DEBUG,
        INFO,
        SUCCESS,
        WARNING,
        ERROR,
        CRITICAL,
        FATAL
    }

    private static readonly StringBuilder _captureBuffer = new();
    private static readonly object _captureLock = new();
    private static bool _isCapturing = false;
    private static readonly ConcurrentDictionary<string, StringBuilder> _serverBuffers = new();
    private static readonly object _serverBufferLock = new();

    // 全局输出实例（线程安全）
    public static ConsoleO COStream { get; private set; } = new ConsoleO();
    public static LogO LOStream { get; private set; } = new LogO();
    /// <summary>
    /// 开始捕获输出。之后所有 Print 调用（capture=true）将写入缓冲区。
    /// </summary>
    public static void StartCapture()
    {
        lock (_captureLock)
        {
            _captureBuffer.Clear();
            _isCapturing = true;
        }
    }

    /// <summary>
    /// 停止捕获并返回捕获到的所有文本。
    /// </summary>
    public static string StopCapture()
    {
        while (_isCapturing) ; // 等待捕获完成
        lock (_captureLock)
        {
            string result = _captureBuffer.ToString();
            _captureBuffer.Clear();
            return result;
        }
    }

    /// <summary>打印日志（支持捕获模式）</summary>
    /// <param name="context">上下文</param>
    /// <param name="level">日志级别</param>
    /// <param name="message">消息</param>
    /// <param name="includeTimestamp">是否包含时间戳</param>
    /// <param name="capture">是否将此输出捕获到缓冲区（仅在捕获模式下有效）</param>
    /// <param name="end">是否在捕获完成后立即返回（仅当 capture=true 且 end=true 时，会停止捕获并返回全部内容）</param>
    public static void Print(string context, LogLevel level, string message, bool includeTimestamp = true, bool capture = false, bool end = false)
    {
        // 如果正在捕获模式且 capture=true，则写入缓冲区
        if (_isCapturing && capture)
        {
            string formatted = FormatPlain(context, level, message, includeTimestamp);
            lock (_captureLock)
            {
                _captureBuffer.AppendLine(formatted);
                if (end)
                {
                    _isCapturing = false;
                }
            }
        }
        // 正常模式：输出到控制台和日志文件
        LOStream.Log(context, level, message, includeTimestamp);
        COStream.Print(context, level, message, includeTimestamp);

        // ---- 服务器缓冲区（自动记录） ----
        // 检测 context 是否以 "/OUT" 结尾（如 "myserver/OUT"）
        if (!context.EndsWith("/OUT")) return;
        string serverName = context.Split("/")[0];
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            AppendToServerBuffer(serverName,$"[{level}] {message}");
        }
    }

    /// <summary>
    /// 生成纯文本格式（无 ANSI 颜色）
    /// </summary>
    private static string FormatPlain(string context, LogLevel level, string message, bool includeTimestamp)
    {
        var sb = new StringBuilder();
        sb.Append($"[{context}/{(level != LogLevel.NULL ? level : string.Empty)}] ");
        if (includeTimestamp)
            sb.Append($"[{DateTime.Now:HH:mm:ss}] ");
        sb.Append(message);
        return sb.ToString();
    }

    // 全局输入实例（线程安全）
    public static ConsoleI CIStream { get; private set; } = new ConsoleI();
    public static bool Scan(out List<string> lines)
    {
        bool ret = CIStream.TryRead(out lines);
        string input = string.Join(", ", lines);
        if(!String.IsNullOrEmpty(input)) LOStream.Log("Global/IO", LogLevel.DEBUG, $"Scan: {input}", includeTimestamp: true);
        return ret;
    }

    /// <summary>
    /// 清空指定服务器的缓冲区。
    /// </summary>
    public static void ClearServerBuffer(string serverName)
    {
        if (string.IsNullOrEmpty(serverName))
            return;

        if (_serverBuffers.TryGetValue(serverName, out var buffer))
        {
            lock (buffer)
            {
                buffer.Clear();
            }
        }
        // 如果不存在，无需创建
    }

    /// <summary>
    /// 获取指定服务器的缓冲区内容（不清空）。
    /// </summary>
    public static string GetServerBuffer(string serverName)
    {
        if (string.IsNullOrEmpty(serverName) || !_serverBuffers.TryGetValue(serverName, out var buffer))
            return string.Empty;

        lock (buffer)
        {
            return buffer.ToString();
        }
    }

    /// <summary>
    /// 将文本追加到指定服务器的缓冲区。
    /// </summary>
    public static void AppendToServerBuffer(string serverName, string text)
    {
        if (string.IsNullOrEmpty(serverName) || string.IsNullOrEmpty(text))
            return;

        var buffer = _serverBuffers.GetOrAdd(serverName, _ => new StringBuilder());
        lock (buffer)
        {
            buffer.AppendLine(text);
        }
    }

    /// <summary>
    /// 获取并清空指定服务器的缓冲区。
    /// </summary>
    public static string GetAndClearServerBuffer(string serverName)
    {
        if (string.IsNullOrEmpty(serverName) || !_serverBuffers.TryGetValue(serverName, out var buffer))
            return string.Empty;

        lock (buffer)
        {
            string content = buffer.ToString();
            buffer.Clear();
            return content;
        }
    }
}