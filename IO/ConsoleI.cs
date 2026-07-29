using System.Collections.Concurrent;

namespace MSL_CLI.IO;

/// <summary>
/// 异步非阻塞控制台输入器（生产者-消费者模式）
/// 与 ConsoleO 对应，用于处理标准输入
/// </summary>
public class ConsoleI : IDisposable
{
    // 存储读取到的输入行
    private readonly ConcurrentQueue<string> _inputQueue = new();
    // 用于通知有新输入的信号量
    private readonly ManualResetEventSlim _inputSignal = new(false);
    // 后台读取线程
    private readonly Thread _readerThread;

    private bool _disposed = false;
    private bool _isRunning = true;

    public ConsoleI()
    {
        _readerThread = new Thread(ReadInputLoop)
        {
            IsBackground = true,
            Name = "ConsoleI-Reader"
        };
        _readerThread.Start();
    }

    /// <summary>
    /// 后台线程：循环读取控制台输入（生产者）
    /// </summary>
    private void ReadInputLoop()
    {
        try
        {
            while (_isRunning)
            {
                // 注意：Console.ReadLine() 是阻塞的。
                string? line = Console.In.ReadLine();

                if (!_isRunning) break;

                if (line != null)
                {
                    _inputQueue.Enqueue(line);
                    _inputSignal.Set(); // 唤醒等待输入的消费者
                }
            }
        }
        catch (Exception ex) when (_disposed || !_isRunning)
        {
            // 关闭时的预期异常，忽略
        }
        catch (Exception ex)
        {
            // 实际应用中可能需要记录此异常到 ConsoleO
            Console.Error.WriteLine($"[ConsoleI Error] {ex.Message}");
        }
    }

    /// <summary>
    /// 尝试非阻塞地读取所有当前可用的输入行
    /// </summary>
    /// <param name="lines">输出的字符串列表，如果没有输入则为空列表</param>
    /// <returns>如果读取到至少一行输入则返回 true，否则返回 false</returns>
    public bool TryRead(out List<string> lines)
    {
        lines = new List<string>();

        // 一次性取出队列中所有当前可用的行
        while (_inputQueue.TryDequeue(out string? line))
        {
            lines.Add(line);
        }

        return lines.Count > 0;
    }

    /// <summary>
    /// 等待并读取输入行（可选超时），返回期间收到的所有行
    /// </summary>
    /// <param name="lines">输出的字符串列表</param>
    /// <param name="millisecondsTimeout">超时时间（-1 为无限等待）</param>
    /// <returns>如果读取到至少一行输入则返回 true</returns>
    public bool Read(out List<string> lines, int millisecondsTimeout = -1)
    {
        lines = new List<string>();

        // 先检查队列里有没有现成的
        while (_inputQueue.TryDequeue(out string? line))
        {
            lines.Add(line);
        }

        if (lines.Count > 0)
        {
            return true;
        }

        // 如果没有现成的，则等待信号
        if (_inputSignal.Wait(millisecondsTimeout))
        {
            // 收到信号后，取出所有可用的行
            while (_inputQueue.TryDequeue(out string? line))
            {
                lines.Add(line);
            }
            return lines.Count > 0;
        }

        // 超时
        return false;
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

            // 尝试中断阻塞的 ReadLine
            _inputSignal.Set(); // 唤醒可能在 Wait 的线程

            // 给线程一点时间退出
            if (_readerThread.IsAlive)
            {
                _readerThread.Join(500);
            }

            _inputSignal.Dispose();
        }
        _disposed = true;
    }
}