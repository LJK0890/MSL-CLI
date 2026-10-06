using MSL_CLI.Core.Ports;
using System.Collections.Concurrent;

namespace MSL_CLI.CLI;

/// <summary>
/// 控制台输入读取器，通过后台线程持续读取控制台输入并放入队列。
/// 队列中的每一行按“先发给交互请求、其次触发事件”的顺序分发：
/// 当有组件通过 <see cref="ReadLineAsync"/> 请求独占输入时，该行会作为交互应答回传，
/// 而不会触发 <see cref="OnInputReceived"/>；否则该行照常触发命令事件。
/// </summary>
public class ConsoleInputReader : IInputReader, IDisposable
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
    // 是否已释放
    private bool _disposed;
    // 是否已停止读取（StopReading 幂等）
    private bool _stopped;

    // 保护 _interactive 的同步锁，确保同一时刻只有一个交互请求处于挂起状态
    private readonly object _interactiveLock = new();
    // 当前挂起的交互请求；为 null 表示没有组件在独占输入
    private TaskCompletionSource<string>? _interactive;
    // 实际读取一行的委托（默认读取控制台，可注入以便测试）
    private readonly Func<string?> _readLine;

    /// <summary>
    /// 每当从控制台读取到一行有效输入时触发，参数为该行内容。
    /// </summary>
    public event EventHandler<string>? OnInputReceived;

    /// <summary>
    /// 初始化 <see cref="ConsoleInputReader"/> 的新实例，启动队列处理任务并创建后台读取线程。
    /// </summary>
    /// <param name="readLine">
    /// 可选的读行委托，用于替换默认的 <see cref="Console.ReadLine"/>；
    /// 返回 null 表示输入流已结束，读取循环随之退出。
    /// </param>
    public ConsoleInputReader(Func<string?>? readLine = null)
    {
        _readLine = readLine ?? Console.ReadLine;
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
        if (_stopped) return;
        _stopped = true;

        _running = false;
        try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { return; }
        _cts.Cancel();
        // 唤醒仍在等待交互应答的调用方，避免其永久阻塞
        lock (_interactiveLock)
        {
            _interactive?.TrySetResult(string.Empty);
        }
        _readerThread.Join(1000);
        try { _processTask.Wait(2000); } catch { }
    }

    /// <summary>
    /// 请求独占读取控制台的一行输入，用于需要向用户提问并等待作答的场景。
    /// 在请求挂起期间，读取到的行会作为本方法的返回值，而不会触发 <see cref="OnInputReceived"/>。
    /// 若已有其他交互请求挂起，或发生超时/取消，则返回空字符串。
    /// </summary>
    /// <param name="cancellationToken">取消令牌，取消时本方法立即返回空字符串。</param>
    /// <param name="timeout">可选的等待超时；超时后返回空字符串并解除独占。</param>
    /// <returns>用户输入的整行文本；超时、取消或冲突时为空字符串。</returns>
    public async Task<string> ReadLineAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 抢占交互槽位：已有挂起请求时直接放弃，避免两个提问互相抢答
        lock (_interactiveLock)
        {
            if (_interactive != null)
                return string.Empty;
            _interactive = tcs;
        }

        // 链接外部取消令牌与超时，两者都会让本次请求提前结束
        using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
        using var linked = timeoutCts != null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        using var registration = linked.Token.Register(() => tcs.TrySetResult(string.Empty));

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            // 无论正常应答还是超时取消，都必须释放槽位
            lock (_interactiveLock)
            {
                if (ReferenceEquals(_interactive, tcs))
                    _interactive = null;
            }
        }
    }

    /// <summary>
    /// 后台读取循环：持续读取输入行并加入队列。读取返回 null 时视为输入流结束并退出循环。
    /// </summary>
    private void ReadLoop()
    {
        while (_running)
        {
            var line = _readLine();
            if (line == null)
                break;
            if (!_running)
                break;

            try
            {
                _queue.Add(line, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (InvalidOperationException) { break; }
        }
    }

    /// <summary>
    /// 后台队列处理循环：从队列取出输入行，优先投递给挂起的交互请求，否则触发
    /// <see cref="OnInputReceived"/> 事件。
    /// </summary>
    private void ProcessQueue()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable(_cts.Token))
            {
                var dispatched = false;
                lock (_interactiveLock)
                {
                    // 必须在同一把锁内完成投递：否则“超时释放槽位 → 新提问装上新的 waiter”
                    // 之间到达的这一行会被当作控制台命令派发，而它其实是上一条提问的作答
                    if (_interactive != null && _interactive.TrySetResult(line))
                        dispatched = true;
                }

                if (dispatched)
                    continue;

                OnInputReceived?.Invoke(this, line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"输入队列处理异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 释放输入读取器：停止读取线程并释放队列与取消令牌源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { StopReading(); } catch { /* 忽略停止异常 */ }
        try { _queue.Dispose(); } catch { /* 忽略释放异常 */ }
        try { _cts.Dispose(); } catch { /* 忽略释放异常 */ }
    }
}
