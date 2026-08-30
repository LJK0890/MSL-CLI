using System.Diagnostics;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 服务器进程的默认实现，封装 System.Diagnostics.Process 管理单个 Minecraft 服务器进程。
/// </summary>
public class ServerProcess : IServerProcess
{
    // 当前托管的底层进程对象；为 null 表示尚未启动或已释放
    private Process? _process;
    // 进程退出任务源：进程退出时置为完成，用于 WaitForExitAsync 等待
    private TaskCompletionSource<bool> _exitedTcs = new();

    /// <summary>
    /// 当前进程的进程 ID；未启动时为 -1。
    /// </summary>
    public int Id => _process?.Id ?? -1;
    /// <summary>
    /// 当前进程的启动时间；未启动时为默认值。
    /// </summary>
    public DateTime StartTime => _process?.StartTime ?? default;
    /// <summary>
    /// 当前进程是否已退出；未启动时视为已退出。
    /// </summary>
    public bool HasExited => _process?.HasExited ?? true;

    /// <summary>
    /// 进程输出事件，输出的一行文本通过该事件发布。
    /// </summary>
    public event EventHandler<string>? OutputReceived;
    /// <summary>
    /// 进程退出事件。
    /// </summary>
    public event EventHandler? Exited;

    // 将进程输出的数据行转发给 OutputReceived 事件
    private void OnOutput(string? data)
    {
        if (data != null) OutputReceived?.Invoke(this, data);
    }

    /// <summary>
    /// 启动服务器进程，并开始异步读取标准输出与标准错误。
    /// </summary>
    /// <param name="fileName">可执行文件路径（如 Java 路径）。</param>
    /// <param name="arguments">进程启动参数。</param>
    /// <param name="workingDirectory">进程工作目录（服务器目录）。</param>
    public void Start(string fileName, string arguments, string workingDirectory)
    {
        // 每次启动都重建内部 Process，支持“停止后再启动”，同时释放上一次的进程句柄
        DisposeInternalProcess();
        _exitedTcs = new TaskCompletionSource<bool>();

        var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        p.EnableRaisingEvents = true;
        p.OutputDataReceived += (s, e) => OnOutput(e.Data);
        p.ErrorDataReceived += (s, e) => OnOutput(e.Data);
        p.Exited += (s, e) =>
        {
            _exitedTcs.TrySetResult(true);
            Exited?.Invoke(this, EventArgs.Empty);
        };

        _process = p;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    /// <summary>
    /// 等待进程退出，最长等待指定毫秒数。
    /// </summary>
    /// <param name="millisecondsTimeout">最长等待毫秒数。</param>
    /// <returns>超时前进程已退出返回 true，超时返回 false。</returns>
    public async Task<bool> WaitForExitAsync(int millisecondsTimeout)
    {
        var completedTask = await Task.WhenAny(_exitedTcs.Task, Task.Delay(millisecondsTimeout));
        return completedTask == _exitedTcs.Task;
    }

    /// <summary>
    /// 终止进程；可选择是否连同整个进程树一起终止。
    /// </summary>
    /// <param name="entireProcessTree">为 true 时连子进程一并终止。</param>
    public void Kill(bool entireProcessTree) => _process?.Kill(entireProcessTree);

    /// <summary>
    /// 向进程标准输入写入一行文本（用于向服务器控制台发送命令）。
    /// </summary>
    /// <param name="text">要写入的命令文本。</param>
    public async Task WriteStandardInputAsync(string text)
    {
        if (_process is { HasExited: false })
            await _process.StandardInput.WriteLineAsync(text);
    }

    /// <summary>
    /// 获取当前托管的底层进程对象；未启动时为 null。
    /// </summary>
    /// <returns>底层 Process 实例。</returns>
    public Process? GetProcess() => _process;

    // 释放内部进程对象并置空引用
    private void DisposeInternalProcess()
    {
        if (_process != null)
        {
            try { _process.Dispose(); } catch { /* 忽略释放异常 */ }
            _process = null;
        }
    }

    /// <summary>
    /// 释放内部进程及其句柄。
    /// </summary>
    public void Dispose() => DisposeInternalProcess();
}
