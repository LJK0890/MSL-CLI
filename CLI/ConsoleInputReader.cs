using MSL_CLI.Core.Ports;
using System.Collections.Concurrent;

namespace MSL_CLI.CLI;

/// <summary>
/// 控制台输入读取器，通过后台线程读取控制台输入并放入队列，再由处理任务逐个触发事件。
/// </summary>
public class ConsoleInputReader : IInputReader
{
    // 存放控制台输入行的线程安全阻塞队列
    private readonly BlockingCollection<string> _queue = new();
    // 用于取消队列处理与读取操作的取消令牌源
    private readonly CancellationTokenSource _cts = new();
    // 后台处理队列的任务
    private readonly Task _processTask;
    // 读取控制台输入的后台线程
    private readonly Thread _readerThread;
    // 运行标志，置为 false 时读取循环退出
    private volatile bool _running = true;

    /// <summary>
    /// 每当从控制台读取到一行有效输入时触发，参数为该行内容。
    /// </summary>
    public event EventHandler<string>? OnInputReceived;

    /// <summary>
    /// 初始化 <see cref="ConsoleInputReader"/> 的新实例，启动队列处理任务并创建后台读取线程。
    /// </summary>
    public ConsoleInputReader()
    {
        _processTask = Task.Run(ProcessQueue);
        _readerThread = new Thread(ReadLoop) { IsBackground = true };
    }

    /// <summary>
    /// 启动后台线程开始读取控制台输入。
    /// </summary>
    public void StartReading()
    {
        _readerThread.Start();
    }

    /// <summary>
    /// 停止读取并等待后台任务退出，同时取消未完成的处理。
    /// </summary>
    public void StopReading()
    {
        _running = false;
        _queue.CompleteAdding();
        _cts.Cancel();
        _readerThread.Join(1000);
        try { _processTask.Wait(2000); } catch { }
    }

    /// <summary>
    /// 后台读取循环：持续读取控制台输入行并加入队列。
    /// </summary>
    private void ReadLoop()
    {
        while (_running)
        {
            var line = Console.ReadLine();
            if (line != null && _running)
            {
                _queue.Add(line, _cts.Token);
            }
        }
    }

    /// <summary>
    /// 后台队列处理循环：从队列取出输入行并触发 <see cref="OnInputReceived"/> 事件。
    /// </summary>
    private void ProcessQueue()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable(_cts.Token))
            {
                OnInputReceived?.Invoke(this, line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"输入队列处理异常: {ex.Message}");
        }
    }
}
