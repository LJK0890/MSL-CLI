using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using static MSL_CLI.IO.IO;
using MSL_CLI.Models;
using McQuery.Net; // 新增引用

namespace MSL_CLI.Services;

internal class ServerManager
{
    private ServerConfig serverConfig;
    private ServerArgument serverArgument;
    private string serverPath;
    private string serverName;
    private Process? serverProcess;
    private ServerStatus status = ServerStatus.Stopped;
    private readonly object lockObject = new object();

    public ServerStatus Status
    {
        get { lock (lockObject) return status; }
        private set { lock (lockObject) status = value; }
    }

    public ServerManager(string name, string filePath)
    {
        serverPath = filePath;
        serverName = name;
        Print($"{name}", LogLevel.INFO, $"初始化服务器: {filePath}", includeTimestamp: true);

        serverConfig = new ServerConfig(name, Path.Combine(filePath, "server.properties"));
        serverArgument = new ServerArgument(name, filePath);

        Print($"{name}", LogLevel.INFO, $"服务器初始化完成。", includeTimestamp: true);
    }

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
            string arguments = serverArgument.GetStartArguments();
            string javaPath = serverArgument.GetType().GetField("javaPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(serverArgument)?.ToString() ?? "java";

            var startInfo = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = arguments.Replace(javaPath, "").Trim(),
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

            serverProcess.OutputDataReceived += (s, e) => OnOutputReceived(e.Data);
            serverProcess.ErrorDataReceived += (s, e) => OnOutputReceived(e.Data);
            serverProcess.Exited += (s, e) => OnProcessExited();

            if (serverProcess.Start())
            {
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

    public async Task StopAsync(bool force = false)
    {
        if (Status == ServerStatus.Stopped || Status == ServerStatus.Stopping)
            return;

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
                serverProcess.Kill(true);
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
                await serverProcess.StandardInput.WriteLineAsync("stop");
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

    private void OnOutputReceived(string? data)
    {
        if (string.IsNullOrEmpty(data))
            return;

        // 匹配形如 [xxx/LEVEL] 的开头
        var match = System.Text.RegularExpressions.Regex.Match(data, @"^\[[^\/]+\/([A-Z]+)\].*");
        if (match.Success)
        {
            string matchLevel = match.Groups[1].Value;
            Print($"{serverName}/OUT", ParseLevel(matchLevel), data.Replace($"/{matchLevel}",""), includeTimestamp: false);
        }
        else
        {
            // 如果格式不匹配，降级为原逻辑
            Print($"{serverName}/OUT", LogLevel.INFO, data, includeTimestamp: false);
        }
    }

    private void OnProcessExited()
    {
        Status = ServerStatus.Stopped;
        Print($"{serverName}", LogLevel.INFO, "服务器进程已退出。", includeTimestamp: true);
    }

    /// <summary>
    /// 获取服务器的 Query 详细信息（使用 McQuery.Net 库）
    /// </summary>
    public async Task<Dictionary<string, string>?> GetServerQueryInfoAsync()
    {
        if (!serverConfig.EnableQuery)
        {
            Print($"{serverName}/Query", LogLevel.WARNING, "配置中未启用 Query (enable-query=false)。", includeTimestamp: true);
            return null;
        }

        if (Status != ServerStatus.Running || serverProcess == null || serverProcess.HasExited)
        {
            Print($"{serverName}/Query", LogLevel.WARNING, "服务器未运行，无法获取 Query 信息。", includeTimestamp: true);
            return null;
        }

        int targetPort = serverConfig.QueryPort;
        Print($"{serverName}/Query", LogLevel.INFO, $"使用 query.port = {targetPort}", includeTimestamp: true);

        var endpoint = new IPEndPoint(IPAddress.Loopback, targetPort);

        try
        {
            // 使用 McQuery.Net 客户端
            IMcQueryClientFactory factory = new McQueryClientFactory();
            using var client = factory.Get();

            // 获取完整状态（包含玩家列表）
            var fullStatus = await client.GetFullStatusAsync(endpoint);

            // 将结果转换为 Dictionary<string, string> 保持与原接口一致
            var result = new Dictionary<string, string>
            {
                ["motd"] = fullStatus.Motd ?? string.Empty,
                ["version"] = fullStatus.Version ?? string.Empty,
                ["game_type"] = fullStatus.GameType ?? string.Empty,
                ["map"] = fullStatus.Map ?? string.Empty,
                ["numplayers"] = fullStatus.NumPlayers.ToString(),
                ["maxplayers"] = fullStatus.MaxPlayers.ToString(),
                ["hostport"] = fullStatus.HostPort.ToString(),
                ["hostip"] = fullStatus.HostIp ?? string.Empty,
                ["players"] = string.Join(", ", fullStatus.PlayerList)
            };

            Print($"{serverName}/Query", LogLevel.SUCCESS, "Query 信息获取成功。", includeTimestamp: true);
            return result;
        }
        catch (Exception ex)
        {
            Print($"{serverName}/Query", LogLevel.ERROR, $"Query 查询失败: {ex.Message}", includeTimestamp: true);
            return null;
        }
    }
    private static LogLevel ParseLevel(string levelStr)
    {
        return levelStr.ToUpperInvariant() switch
        {
            "DEBUG" => LogLevel.DEBUG,
            "INFO" => LogLevel.INFO,
            "SUCCESS" => LogLevel.SUCCESS,
            "WARN" or "WARNING" => LogLevel.WARNING,
            "ERROR" => LogLevel.ERROR,
            "CRITICAL" => LogLevel.CRITICAL,
            "FATAL" => LogLevel.FATAL,
            _ => LogLevel.INFO // 默认
        };
    }
}

internal enum ServerStatus
{
    Stopped,
    Starting,
    Running,
    Stopping
}