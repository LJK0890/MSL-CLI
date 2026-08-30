namespace MSL_CLI.Core.Ports;

/// <summary>
/// 服务器进程接口，封装底层服务器进程的启动、停止、等待退出与输入输出交互。
/// </summary>
public interface IServerProcess : IDisposable
{
    /// <summary>
    /// 底层操作系统进程的进程 ID。
    /// </summary>
    int Id { get; }
    /// <summary>
    /// 进程的启动时间。
    /// </summary>
    DateTime StartTime { get; }
    /// <summary>
    /// 进程是否已经退出。
    /// </summary>
    bool HasExited { get; }
    /// <summary>
    /// 启动一个新的服务器进程。
    /// </summary>
    /// <param name="fileName">要启动的可执行文件路径。</param>
    /// <param name="arguments">启动命令行参数。</param>
    /// <param name="workingDirectory">进程的工作目录。</param>
    void Start(string fileName, string arguments, string workingDirectory);
    /// <summary>
    /// 异步等待进程退出，最多等待指定的毫秒数。
    /// </summary>
    /// <param name="millisecondsTimeout">最长等待时间（毫秒）。</param>
    /// <returns>在超时时间内进程是否已退出。</returns>
    Task<bool> WaitForExitAsync(int millisecondsTimeout); // 返回 bool 表示是否退出
    /// <summary>
    /// 强制终止进程。
    /// </summary>
    /// <param name="entireProcessTree">是否同时终止整个进程树（含子进程）。</param>
    void Kill(bool entireProcessTree);
    /// <summary>
    /// 异步向进程的标准输入写入文本。
    /// </summary>
    /// <param name="text">要写入的文本内容。</param>
    Task WriteStandardInputAsync(string text);
    /// <summary>
    /// 当进程的标准输出产生新内容时触发，事件参数为输出的文本。
    /// </summary>
    event EventHandler<string> OutputReceived;
    /// <summary>
    /// 当进程退出时触发。
    /// </summary>
    event EventHandler Exited;
}
