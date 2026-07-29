using System.Diagnostics;
using System.Net.Sockets; // 新增：用于UDP通信
using System.Text;
using System.Threading.Tasks; // 确保包含此命名空间
using static MSL_CLI.IO.IO;
using MSL_CLI.Models;
namespace MSL_CLI.Services;

/// <summary>
/// 单个服务器的管理器，负责加载该服务器的配置、启动参数以及进程生命周期管理。
/// </summary>
internal class ServerManager
{
    private ServerConfig serverConfig;
    private ServerArgument serverArgument;
    private string serverPath;
    private string serverName;
    private Process? serverProcess;
    private ServerStatus status = ServerStatus.Stopped;
    private readonly object lockObject = new object(); // 线程安全锁

    /// <summary>
    /// 服务器当前状态
    /// </summary>
    public ServerStatus Status
    {
        get { lock (lockObject) return status; }
        private set { lock (lockObject) status = value; }
    }

    /// <summary>
    /// 构造函数，加载指定路径的服务器配置。
    /// </summary>
    /// <param name="name">服务器名称</param>
    /// <param name="filePath">服务器根目录路径</param>
    public ServerManager(string name, string filePath)
    {
        serverPath = filePath;
        serverName = name;
        Print($"{name}", LogLevel.INFO, $"初始化服务器: {filePath}", includeTimestamp: true);

        // 加载 server.properties
        serverConfig = new ServerConfig(name, Path.Combine(filePath, "server.properties"));

        // 解析启动参数
        serverArgument = new ServerArgument(name, filePath);

        Print($"{name}", LogLevel.INFO, $"服务器初始化完成。", includeTimestamp: true);
    }

    /// <summary>
    /// 异步启动服务器
    /// </summary>
    public async Task StartAsync()
    {
        if (Status == ServerStatus.Running || Status == ServerStatus.Starting)
        {
            Print($"{serverName}", LogLevel.WARNING, "服务器已在运行或正在启动中。", includeTimestamp: true);
            return;
        }

        Status = ServerStatus.Starting;
        Print($"{serverName}", LogLevel.INFO, "正在启动服务器...", includeTimestamp: true);

        try
        {
            // 获取完整的启动命令
            string arguments = serverArgument.GetStartArguments();
            string javaPath = serverArgument.GetType().GetField("javaPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(serverArgument)?.ToString() ?? "java";

            var startInfo = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = arguments.Replace(javaPath, "").Trim(), // 移除重复的 java 路径部分
                WorkingDirectory = serverPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            serverProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            // 注册事件
            serverProcess.OutputDataReceived += (s, e) => OnOutputReceived(e.Data);
            serverProcess.ErrorDataReceived += (s, e) => OnOutputReceived(e.Data);
            serverProcess.Exited += (s, e) => OnProcessExited();

            if (serverProcess.Start())
            {
                // 开始异步读取
                serverProcess.BeginOutputReadLine();
                serverProcess.BeginErrorReadLine();
                Status = ServerStatus.Running;
                Print($"{serverName}", LogLevel.SUCCESS, "服务器已启动。", includeTimestamp: true);
            }
            else
            {
                Status = ServerStatus.Stopped;
                Print($"{serverName}", LogLevel.ERROR, "进程启动失败 (Start 返回 false)。", includeTimestamp: true);
            }
        }
        catch (Exception ex)
        {
            Status = ServerStatus.Stopped;
            Print($"{serverName}", LogLevel.ERROR, $"启动异常: {ex.Message}", includeTimestamp: true);
        }
    }

    /// <summary>
    /// 异步停止服务器
    /// </summary>
    /// <param name="force">是否强制终止进程</param>
    public async Task StopAsync(bool force = false)
    {
        if (Status == ServerStatus.Stopped || Status == ServerStatus.Stopping)
        {
            return;
        }

        Status = ServerStatus.Stopping;
        Print($"{serverName}", LogLevel.INFO, "正在停止服务器...", includeTimestamp: true);

        if (serverProcess == null || serverProcess.HasExited)
        {
            Status = ServerStatus.Stopped;
            return;
        }

        if (force)
        {
            try
            {
                serverProcess.Kill(true); // 杀树
                Print($"{serverName}", LogLevel.WARNING, "进程已强制终止。", includeTimestamp: true);
            }
            catch (Exception ex)
            {
                Print($"{serverName}", LogLevel.ERROR, $"强制终止失败: {ex.Message}", includeTimestamp: true);
            }
        }
        else
        {
            try
            {
                // 发送 stop 命令尝试优雅关闭
                await serverProcess.StandardInput.WriteLineAsync("stop");
                // 等待退出，超时时间 30秒
                if (!await Task.Run(() => serverProcess.WaitForExit(30000)))
                {
                    Print($"{serverName}", LogLevel.WARNING, "优雅关闭超时，正在强制终止...", includeTimestamp: true);
                    serverProcess.Kill(true);
                }
            }
            catch (Exception ex)
            {
                Print($"{serverName}", LogLevel.ERROR, $"停止异常: {ex.Message}", includeTimestamp: true);
                serverProcess.Kill(true);
            }
        }
    }

    /// <summary>
    /// 向服务器控制台发送命令
    /// </summary>
    public async Task SendCommandAsync(string command)
    {
        if (Status == ServerStatus.Running && serverProcess != null && !serverProcess.HasExited)
        {
            try
            {
                await serverProcess.StandardInput.WriteLineAsync(command);
            }
            catch (Exception ex)
            {
                Print($"{serverName}", LogLevel.ERROR, $"发送命令失败: {ex.Message}", includeTimestamp: true);
            }
        }
    }

    // --- 事件回调 ---

    private void OnOutputReceived(string? data)
    {
        if (!string.IsNullOrEmpty(data))
        {
            // 这里可以根据需要解析日志级别，目前统一作为 INFO 输出
            // 注意：Minecraft 服务端日志通常包含时间戳，这里直接透传
            Print($"{serverName}/OUT", LogLevel.INFO, data, includeTimestamp: false);
        }
    }

    private void OnProcessExited()
    {
        Status = ServerStatus.Stopped;
        Print($"{serverName}", LogLevel.INFO, "服务器进程已退出。", includeTimestamp: true);
        // 可以在这里添加自动重启逻辑
    }
}

/// <summary>
/// 服务器状态枚举
/// </summary>
internal enum ServerStatus
{
    Stopped,
    Starting,
    Running,
    Stopping
}