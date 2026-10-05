using System.Diagnostics;
using System.Text;
using System.Text.Json;
using McQuery.Net;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 单个 Minecraft 服务器的管理器：负责进程启停、命令发送、输出缓冲与 AI 命令处理。
/// </summary>
public class ServerManager : IServer, IDisposable
{
    // 服务器名称
    private readonly string _name;
    // 服务器目录路径
    private readonly string _path;
    // server.properties 配置
    private readonly ServerProperties _properties;
    // 控制台输出写入器
    private readonly IOutputWriter _output;
    // 服务器进程
    private readonly IServerProcess _process;
    // 服务器启动参数
    private readonly ServerArgument _argument;
    // AI 服务（用于 $chat/$agent 等 AI 命令）
    private readonly IAgentService _agentService;
    // 全局应用配置
    private readonly AppConfig _appConfig;
    // 当前运行状态
    private ServerStatus _status = ServerStatus.Stopped;
    // 服务器输出缓冲区
    private readonly StringBuilder _buffer = new();
    // 保护状态访问的锁
    private readonly Lock _statusLock = new();
    // 保护缓冲区访问的锁
    private readonly Lock _bufferLock = new();

    /// <summary>
    /// 服务器名称。
    /// </summary>
    public string Name => _name;
    /// <summary>
    /// 服务器目录路径。
    /// </summary>
    public string Path => _path;
    /// <summary>
    /// 当前运行状态（线程安全读取）。
    /// </summary>
    public ServerStatus Status { get { lock (_statusLock) return _status; } }
    /// <summary>
    /// 服务器属性（server.properties）配置。
    /// </summary>
    public ServerProperties Properties => _properties;
    /// <summary>
    /// 服务器启动参数。
    /// </summary>
    public ServerArgument Argument => _argument;

    /// <summary>
    /// 创建服务器管理器实例，并订阅进程输出与退出事件。
    /// </summary>
    /// <param name="name">服务器名称。</param>
    /// <param name="path">服务器目录路径。</param>
    /// <param name="output">控制台输出写入器。</param>
    /// <param name="process">服务器进程实例。</param>
    /// <param name="agentService">AI 服务。</param>
    /// <param name="appConfig">全局应用配置。</param>
    public ServerManager(
        string name,
        string path,
        IOutputWriter output,
        IServerProcess process,
        IAgentService agentService,
        AppConfig appConfig)
    {
        _name = name;
        _path = path;
        _output = output;
        _process = process;
        _agentService = agentService;
        _appConfig = appConfig;
        _properties = new ServerProperties(System.IO.Path.Combine(path, "server.properties"), output);
        _argument = new ServerArgument(name, path, output);
        _process.OutputReceived += OnOutputReceived;
        _process.Exited += (s, e) => {
            lock (_statusLock) _status = ServerStatus.Stopped;
            _output.Write(_name, LogLevel.Info, "进程退出");
        };
    }

    /// <summary>
    /// 启动服务器：仅当当前状态为 Stopped 时执行，启动后置为 Running。
    /// </summary>
    public async Task StartAsync()
    {
        // 仅在 Stopped 状态下允许启动，并立即置为 Starting 防止重复启动
        lock (_statusLock)
        {
            if (_status != ServerStatus.Stopped) return;
            _status = ServerStatus.Starting;
        }
        _output.Write(_name, LogLevel.Info, "正在启动...");
        _process.Start(_argument.JavaPath, _argument.GetJavaArgs(), _path);
        // 短暂等待进程启动完成后置为 Running
        await Task.Delay(1000);
        lock (_statusLock) _status = ServerStatus.Running;
        _output.Write(_name, LogLevel.Success, "已启动");
    }

    /// <summary>
    /// 停止服务器：先发送 "stop" 命令并等待退出，超时则强制终止；force 为 true 时直接强制终止。
    /// </summary>
    /// <param name="force">为 true 时跳过优雅停止，直接强制终止进程树。</param>
    public async Task StopAsync(bool force = false)
    {
        lock (_statusLock)
        {
            if (_status == ServerStatus.Stopped || _status == ServerStatus.Stopping) return;
            _status = ServerStatus.Stopping;
        }
        _output.Write(_name, LogLevel.Info, "正在停止...");
        if (!force)
        {
            // 优雅停止：发送 stop 命令并等待最多 30 秒
            await _process.WriteStandardInputAsync("stop");
            bool exited = await _process.WaitForExitAsync(30000);
            if (!exited)
            {
                // 超时未退出则强制终止进程树
                _output.Write(_name, LogLevel.Warning, "超时，强制终止");
                _process.Kill(true);
            }
        }
        else
        {
            // 强制模式：直接终止整个进程树
            _process.Kill(true);
        }
        lock (_statusLock) _status = ServerStatus.Stopped;
        _output.Write(_name, LogLevel.Success, "已停止");
    }

    /// <summary>
    /// 向运行中的服务器发送命令；服务器未运行时输出警告。
    /// </summary>
    /// <param name="command">要发送的命令文本。</param>
    public async Task SendCommandAsync(string command)
    {
        if (Status == ServerStatus.Running)
            await _process.WriteStandardInputAsync(command);
        else
            _output.Write(_name, LogLevel.Warning, "服务器未运行");
    }

    /// <summary>
    /// 通过 Query 协议获取服务器运行状态信息。
    /// </summary>
    /// <returns>服务器信息字典；Query 未启用、服务器未运行或查询失败时返回 null。</returns>
    public async Task<Dictionary<string, string>?> GetQueryInfoAsync()
    {
        if (!_properties.EnableQuery)
        {
            _output.Write(_name, LogLevel.Warning, "Query未启用");
            return null;
        }
        if (Status != ServerStatus.Running) return null;
        int port = _properties.QueryPort;
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port);
        try
        {
            var factory = new McQueryClientFactory();
            using var client = factory.Get();
            var full = await client.GetFullStatusAsync(endpoint);
            return new Dictionary<string, string>
            {
                ["motd"] = full.Motd ?? "",
                ["version"] = full.Version ?? "",
                ["game_type"] = full.GameType ?? "",
                ["map"] = full.Map ?? "",
                ["numplayers"] = full.NumPlayers.ToString(),
                ["maxplayers"] = full.MaxPlayers.ToString(),
                ["hostport"] = full.HostPort.ToString(),
                ["hostip"] = full.HostIp ?? "",
                ["players"] = string.Join(", ", full.PlayerList)
            };
        }
        catch (Exception ex)
        {
            // 查询失败（如端口未监听）时记录错误并返回 null
            _output.Write(_name, LogLevel.Error, $"Query失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取输出缓冲区内容（不清空）。
    /// </summary>
    /// <returns>缓冲区当前文本。</returns>
    public string GetBufferContent()
    {
        lock (_bufferLock) return _buffer.ToString();
    }

    /// <summary>
    /// 获取输出缓冲区内容并清空缓冲区。
    /// </summary>
    /// <returns>清空前的缓冲区文本。</returns>
    public string GetAndClearBufferContent()
    {
        lock (_bufferLock)
        {
            string content = _buffer.ToString();
            _buffer.Clear();
            return content;
        }
    }

    /// <summary>
    /// 获取底层进程对象（用于状态命令展示）。
    /// </summary>
    /// <returns>底层进程对象；非 ServerProcess 实现或未启动时为 null。</returns>
    public object? GetProcess()
    {
        return (_process as ServerProcess)?.GetProcess();
    }

    /// <summary>
    /// 释放服务器进程资源。
    /// </summary>
    public void Dispose()
    {
        try { _process.Dispose(); }
        catch { /* 忽略释放异常 */ }
    }

    // ---------- AI 命令解析与处理 ----------
    // 解析 AI 触发命令（$chat/$agent/@ai），输出配置名、模式、消息与玩家名
    private bool TryParseAICommand(string raw, out string configName, out bool isAgent, out string message, out string player)
    {
        throw new NotImplementedException("AI命令解析尚未实现");
        configName = "default";
        isAgent = false;
        message = string.Empty;
        player = "Server";
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        // 格式1: $chat [config] <message>  或 $agent [config] <instruction>
        if (raw.StartsWith("$chat", StringComparison.OrdinalIgnoreCase))
        {
            isAgent = false;
            var rest = raw.Substring(5).TrimStart();
            return ParseConfigAndMessage(rest, out configName, out message);
        }
        if (raw.StartsWith("$agent", StringComparison.OrdinalIgnoreCase))
        {
            isAgent = true;
            var rest = raw.Substring(6).TrimStart();
            return ParseConfigAndMessage(rest, out configName, out message);
        }

        // 格式2: @ai <config> chat <message>  或 @ai <config> agent <instruction>
        if (raw.StartsWith("@ai", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Substring(3).Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return false;
            configName = parts[0];
            var mode = parts[1].ToLowerInvariant();
            if (mode == "chat")
            {
                isAgent = false;
                message = parts[2];
                return true;
            }
            else if (mode == "agent")
            {
                isAgent = true;
                message = parts[2];
                return true;
            }
            else
                return false;
        }

        return false;
    }

    // 解析 "$chat/$agent" 剩余部分：可选配置名 + 消息
    private bool ParseConfigAndMessage(string rest, out string configName, out string message)
    {
        throw new NotImplementedException();
        configName = "default";
        message = rest.Trim();

        if (string.IsNullOrEmpty(message))
            return false;

        // 与 AICommand 一致：首个非 "default" 词视为配置名
        var parts = message.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0] != "default")
        {
            configName = parts[0];
            message = parts[1];
        }
        // 否则全部作为消息，使用默认配置（由 HandleAICommandAsync 解析为 DefaultAIConfig）

        return !string.IsNullOrEmpty(message);
    }

    // 调用 AI 服务处理消息，并将回复发送到游戏内与控制台
    private async Task HandleAICommandAsync(string configName, bool isAgent, string message, string player)
    {
        throw new NotImplementedException("AI命令处理尚未实现");
        try
        {
            // 与 AICommand 一致：configName 为 "default" 时改用 AppConfig 配置的默认 AI 配置
            if (configName.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                var defaultName = _appConfig.DefaultAIConfig;
                if (string.IsNullOrWhiteSpace(defaultName))
                {
                    _output.Write(_name, LogLevel.Error, "未设置默认AI配置");
                    return;
                }
                configName = defaultName;
            }

            var (model, response) = isAgent
                ? await _agentService.AgentAsync(configName, message, (this, player))
                : await _agentService.ChatAsync(configName, message, (this, player));

            // 发送到游戏内
            string sayCommand = $"tellraw {(player == "server" ? "@a" : player)} {{\"text\":\"[AI] {response}\",\"color\":\"aqua\"}}";
            await SendCommandAsync(sayCommand);

            // 同时打印到控制台（避免丢失）
            _output.Write(_name, LogLevel.Info, $"[AI回复] {response}");
        }
        catch (Exception ex)
        {
            _output.Write(_name, LogLevel.Error, $"AI处理失败: {ex.Message}");
        }
    }

    // ---------- 输出处理 ----------
    private void OnOutputReceived(object? sender, string data)
    {
        if (string.IsNullOrEmpty(data))
            return;

        // 检测 AI 触发命令
        // if (!data.Contains("[AI]") && TryParseAICommand(data, out string configName, out bool isAgent, out string message, out string player))
        // {
        //    _ = Task.Run(async () => await HandleAICommandAsync(configName, isAgent, message, player));
        // }

        // 普通日志处理
        var match = System.Text.RegularExpressions.Regex.Match(data, @"^\[[^/]*/([A-Z]+)\]");
        LogLevel level = LogLevel.Info;
        if (match.Success)
        {
            string levelStr = match.Groups[1].Value.ToUpperInvariant();
            level = levelStr switch
            {
                "DEBUG" => LogLevel.Debug,
                "INFO" => LogLevel.Info,
                "WARN" or "WARNING" => LogLevel.Warning,
                "ERROR" => LogLevel.Error,
                _ => LogLevel.Info
            };
        }
        lock (_bufferLock)
        {
            _buffer.AppendLine(data);
        }
        _output.Write($"{_name}/OUT", level, data, false);
    }
}
