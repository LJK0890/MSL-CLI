using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 基于阻塞队列与后台任务的异步文件日志写入器，
/// 将日志异步写入 %APPDATA%\{appName}\Log-{时间戳}.txt，支持优雅释放。
/// </summary>
public class FileOutputWriter : IOutputWriter, IDisposable
{
    /// <summary>
    /// 待写入的日志条目阻塞队列，生产者为 Write 调用，消费者为后台写入任务。
    /// </summary>
    private readonly BlockingCollection<LogEntry> _queue = new();

    /// <summary>
    /// 后台写入任务，持续消费队列并将日志写入文件。
    /// </summary>
    private readonly Task _writerTask;

    /// <summary>
    /// 日志文件完整路径。
    /// </summary>
    private readonly string _logFilePath;

    /// <summary>
    /// 用于通知后台任务停止的取消令牌源。
    /// </summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// 标记对象是否已释放，防止重复释放或释放后继续写入。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 初始化文件输出写入器，创建日志目录并启动后台写入任务。
    /// </summary>
    /// <param name="appName">应用名称，用于在 %APPDATA% 下定位日志目录，默认为 "MSL_CLI"。</param>
    public FileOutputWriter(string appName = "MSL_CLI")
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, appName);
        Directory.CreateDirectory(dir);
        _logFilePath = Path.Combine(dir, $"Log-{DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture)}.txt");
        _writerTask = Task.Run(ProcessQueue);
    }

    /// <summary>
    /// 将一条日志消息加入写入队列（异步落盘，不阻塞调用方）。
    /// </summary>
    /// <param name="context">日志上下文，通常为服务器名称或来源标识。</param>
    /// <param name="level">日志级别。</param>
    /// <param name="message">日志消息文本。</param>
    /// <param name="includeTimestamp">是否记录时间戳，默认记录。</param>
    public void Write(string context, LogLevel level, string message, bool includeTimestamp = true)
    {
        // 已释放后忽略新的写入请求。
        // 这里用 TryAdd 而不是 Add：Dispose 可能刚刚释放队列，
        // Add 会抛 ObjectDisposedException —— 而写入日志是“绝不能抛异常”的路径
        // （它可能运行在进程输出事件线程上，异常会直接终止进程）。
        if (_disposed) return;

        try
        {
            _queue.TryAdd(new LogEntry
            {
                Context = context,
                Level = level,
                Message = message,
                Timestamp = includeTimestamp ? DateTime.Now : null
            });
        }
        catch (ObjectDisposedException) { /* 与 Dispose 竞争，丢弃本条日志 */ }
        catch (InvalidOperationException) { /* 队列已完成添加，丢弃本条日志 */ }
    }

    /// <summary>
    /// 后台任务主体：持续从队列取出日志条目并追加写入日志文件，
    /// 取消时在 finally 中将队列剩余条目尽力写完。
    /// </summary>
    private async Task ProcessQueue()
    {
        try
        {
            foreach (var entry in _queue.GetConsumingEnumerable(_cts.Token))
            {
                // 单条写入失败不能让整个消费者退出：否则本次会话后续的所有日志都会被静默丢弃
                try
                {
                    var line = FormatEntry(entry);
                    await File.AppendAllTextAsync(_logFilePath, line + Environment.NewLine, Encoding.UTF8, _cts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // 忽略单条日志的写入失败（例如日志文件被占用），继续处理后续日志
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // 取队列本身失败，但无法再记录，忽略
        }
        finally
        {
            // 确保剩余日志写入
            while (_queue.TryTake(out var entry))
            {
                try
                {
                    var line = FormatEntry(entry);
                    File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// 将日志条目格式化为单行文本：[上下文/级别] [时间] 消息。
    /// </summary>
    /// <param name="entry">日志条目。</param>
    /// <returns>格式化后的日志行文本。</returns>
    private string FormatEntry(LogEntry entry)
    {
        // 无时间戳时回退为当前时间；Debug 级别以 "DEBUG" 文本展示
        var timestamp = entry.Timestamp?.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                        ?? DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var levelStr = entry.Level != LogLevel.Debug ? entry.Level.ToString() : "DEBUG";
        return $"[{entry.Context}/{levelStr}] [{timestamp}] {entry.Message}";
    }

    /// <summary>
    /// 释放资源：停止后台任务并等待其将剩余日志写入文件。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        // 最多等待 5 秒让后台任务完成剩余写入
        try { _writerTask.Wait(5000); } catch { }
        _queue.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// 内部日志条目模型，保存一条日志的上下文、级别、消息与时间戳。
    /// </summary>
    private class LogEntry
    {
        /// <summary>
        /// 日志上下文（服务器名称或来源标识）。
        /// </summary>
        public string Context { get; set; } = string.Empty;

        /// <summary>
        /// 日志级别。
        /// </summary>
        public LogLevel Level { get; set; }

        /// <summary>
        /// 日志消息文本。
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 日志时间戳；为 null 表示不记录。
        /// </summary>
        public DateTime? Timestamp { get; set; }
    }
}
