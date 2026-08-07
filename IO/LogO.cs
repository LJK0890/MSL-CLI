using System.Collections.Concurrent;
using System.Text;
using MSL_CLI.Config;
namespace MSL_CLI.IO;

/// <summary>
/// 异步文件日志记录器（生产者-消费者模式）
/// 负责将日志异步写入磁盘文件，避免阻塞主线程
/// </summary>
public class LogO : IDisposable
{
    // 日志队列
    private readonly ConcurrentQueue<LogEntry> _logQueue = new();
    // 信号量，用于通知后台线程有新日志
    private readonly ManualResetEventSlim _logSignal = new(false);
    // 后台写入线程
    private readonly Thread _writerThread;

    private bool _disposed = false;
    private bool _isRunning = true;

    // 配置文件路径（可以根据需要修改）
    private readonly string _logFilePath;
    // 自动刷新间隔（毫秒），防止日志积压在内存中
    private readonly int _flushIntervalMs;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="filePath">日志文件路径</param>
    /// <param name="flushIntervalMs">自动落盘间隔（毫秒）</param>
    public LogO(int flushIntervalMs = 1000)
    {
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string configDir = Path.Combine(appDataPath, AppConstants.AppName);
        _logFilePath = Path.Combine(configDir, $"Log-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.txt");
        _flushIntervalMs = flushIntervalMs;

        _writerThread = new Thread(ProcessLogQueue)
        {
            IsBackground = true,
            Name = "LogO-Writer"
        };
        _writerThread.Start();
    }

    /// <summary>
    /// 将日志消息加入队列（生产者）
    /// </summary>
    public void Log(string context, IO.LogLevel level, string message, bool includeTimestamp = true)
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
    /// 后台线程：从队列提取日志并写入文件（消费者）
    /// </summary>
    private void ProcessLogQueue()
    {
        while (_isRunning)
        {
            // 等待信号或超时（超时用于定期强制落盘，防止日志量极少时长时间不写入）
            bool signaled = _logSignal.Wait(_flushIntervalMs);

            // 如果收到信号则重置，如果是超时则继续执行写入逻辑
            if (signaled) _logSignal.Reset();

            // 批量处理当前队列中的所有日志
            var batch = new List<LogEntry>();
            while (_logQueue.TryDequeue(out LogEntry? entry))
            {
                if (entry != null) batch.Add(entry);
            }

            if (batch.Count > 0)
            {
                WriteBatchToFile(batch);
            }
        }

        // 退出前处理剩余日志
        var finalBatch = new List<LogEntry>();
        while (_logQueue.TryDequeue(out LogEntry? entry))
        {
            if (entry != null) finalBatch.Add(entry);
        }
        if (finalBatch.Count > 0) WriteBatchToFile(finalBatch);
    }

    /// <summary>
    /// 执行实际的磁盘写入操作
    /// </summary>
    private void WriteBatchToFile(List<LogEntry> entries)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var entry in entries)
            {
                // 格式化日志：[时间] [级别] [上下文] 内容
                // 注意：写入文件通常不需要 ANSI 颜色代码，除非是专门为了查看彩色日志文件
                // 这里为了纯净的文本日志，去除了颜色代码，仅保留文本格式
                string timestamp = entry.Timestamp.HasValue ? $"[{entry.Timestamp.Value.ToString("HH:mm:ss")}] " : String.Empty;
                sb.AppendLine($"[{entry.Context}/{(entry.Level != IO.LogLevel.NULL ? entry.Level : string.Empty)}] {timestamp}{entry.Message}");
            }

            // 使用 AppendAllText 是线程安全的（相对于文件流），但在高并发下 AppendAsync 性能更好
            // 这里为了简单和可靠性使用同步写入（在后台线程中执行，不会阻塞主线程）
            File.AppendAllText(_logFilePath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // 如果日志写入失败，为了防止死循环或崩溃，通常只能输出到控制台或忽略
            // 这里借用 Console.Error 提示日志系统本身的问题
            Console.Error.WriteLine($"[LogO Error] Failed to write log to file: {ex.Message}");
        }
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
            _isRunning = false;
            _logSignal.Set(); // 唤醒线程处理剩余日志
            if (_writerThread.IsAlive)
            {
                _writerThread.Join(1000); // 等待线程结束
            }
            _logSignal.Dispose();
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