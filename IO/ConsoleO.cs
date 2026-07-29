using MSL_CLI.IO;
using System.Collections.Concurrent;

using System.Text;

/// <summary>
/// 异步日志输出器（生产者-消费者模式）
/// </summary>
public class ConsoleO : IDisposable
{
    private readonly ConcurrentQueue<LogEntry> _logQueue = new();
    private readonly ManualResetEventSlim _logSignal = new(false);
    private readonly Thread _writerThread;
    private bool _disposed = false;
    private bool _isRunning = true;

    public ConsoleO()
    {
        _writerThread = new Thread(ProcessLogQueue)
        {
            IsBackground = true,
            Name = "ConsoleO-Writer"
        };
        _writerThread.Start();
    }

    /// <summary>
    /// 将日志消息加入队列（生产者）
    /// </summary>
    public void Print(string context, IO.LogLevel level, string message, bool includeTimestamp=true)
    {
        if (_disposed) return;

        _logQueue.Enqueue(new LogEntry
        {
            Context = context,
            Level = level,
            Message = message,
            Timestamp = includeTimestamp ? DateTime.Now : null
        });
        _logSignal.Set(); // 唤醒消费者线程
    }

    /// <summary>
    /// 后台线程：从队列提取日志并格式化输出（消费者）
    /// </summary>
    private void ProcessLogQueue()
    {
        while (_isRunning)
        {
            _logSignal.Wait();     // 等待新日志
            _logSignal.Reset();    // 重置信号

            // 批量处理当前队列中的所有日志
            while (_logQueue.TryDequeue(out LogEntry? entry))
            {
                if (entry == null) continue;

                string formatted = FormatLog(entry);

                // 根据级别选择输出流（错误级别以上输出到 stderr）
                if (entry.Level >= IO.LogLevel.ERROR)
                    Console.Error.WriteLine(formatted);
                else
                    Console.WriteLine(formatted);
            }
        }

        // 退出前处理剩余日志
        while (_logQueue.TryDequeue(out LogEntry? entry))
        {
            if (entry == null) continue;
            string formatted = FormatLog(entry);
            if (entry.Level >= IO.LogLevel.ERROR)
                Console.Error.WriteLine(formatted);
            else
                Console.WriteLine(formatted);
        }
    }

    /// <summary>
    /// 格式化日志消息（并应用 ANSI 颜色）
    /// </summary>
    private string FormatLog(LogEntry entry)
    {
        // 获取该级别的样式
        string style = IO.Style.GetStyle(entry.Level);
        string reset = IO.Style.Reset;

        var sb = new StringBuilder();

        // 添加颜色前缀
        sb.Append(style);

        // 日志前缀（上下文/级别）
        sb.Append($"[{entry.Context}/{entry.Level}] ");

        // 时间戳（可选）
        if (entry.Timestamp.HasValue)
        {
            sb.Append($"[{entry.Timestamp.Value:HH:mm:ss}] ");
        }

        // 日志消息本体
        sb.Append(entry.Message);

        // 添加颜色重置后缀
        sb.Append(reset);

        return sb.ToString();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _isRunning = false;          // 停止接收新日志
            _logSignal.Set();            // 唤醒线程处理剩余日志
            _writerThread.Join();        // 等待线程结束（确保日志不丢失）
            _logSignal.Dispose();        // 释放信号量
        }

        _disposed = true;
    }

    /// <summary>
    /// 日志条目结构
    /// </summary>
    private class LogEntry
    {
        public string Context { get; set; } = string.Empty;
        public IO.LogLevel Level { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime? Timestamp { get; set; }
    }
}