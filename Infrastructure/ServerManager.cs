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
    // AI 服务的惰性工厂（用于 $chat/$agent 等 AI 命令）
    private readonly Func<IAgentService> _agentServiceFactory;
    // 授权网关的惰性工厂：用于把玩家的授权作答路由到对应提问
    private readonly Func<IAgentPermissionGateway>? _permissionGatewayFactory;
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
    /// <param name="agentServiceFactory">
    /// AI 服务的惰性工厂。使用工厂而非实例，以避免“注册表 → 服务器 → AI 服务 → 注册表”的构造循环。
    /// </param>
    /// <param name="appConfig">全局应用配置。</param>
    /// <param name="permissionGatewayFactory">
    /// 可选的授权网关工厂。使用工厂而非直接注入，是为了避免
    /// “注册表 → 服务器 → 网关 → 注册表”的构造循环，只有真正需要路由玩家作答时才解析。
    /// </param>
    public ServerManager(
        string name,
        string path,
        IOutputWriter output,
        IServerProcess process,
        Func<IAgentService> agentServiceFactory,
        AppConfig appConfig,
        Func<IAgentPermissionGateway>? permissionGatewayFactory = null)
    {
        _name = name;
        _path = path;
        _output = output;
        _process = process;
        _agentServiceFactory = agentServiceFactory;
        _appConfig = appConfig;
        _permissionGatewayFactory = permissionGatewayFactory;
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

    /// <summary>
    /// 调用 AI 服务处理玩家消息，并把回复发送到游戏内与控制台。
    /// </summary>
    /// <param name="configName">AI 配置名，"default" 表示使用全局默认配置。</param>
    /// <param name="isAgent">是否为代理模式。</param>
    /// <param name="message">玩家消息内容。</param>
    /// <param name="player">发起请求的玩家名。</param>
    private async Task HandleAICommandAsync(string configName, bool isAgent, string message, string player)
    {
        try
        {
            // configName 为 "default" 时改用 AppConfig 里的默认 AI 配置
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

            var agent = _agentServiceFactory();
            var (model, response) = isAgent
                ? await agent.AgentAsync(configName, WithPlayerContext(message, player), (this, player))
                : await agent.ChatAsync(configName, message, (this, player));

            // 回复发到游戏内；文本用 JSON 序列化，避免引号/换行破坏 tellraw 语法
            var textJson = JsonSerializer.Serialize($"[AI] {response}");
            await SendCommandAsync($"tellraw {player} {{\"text\":{textJson},\"color\":\"aqua\"}}");

            // 同时打印到控制台（避免丢失）
            _output.Write(_name, LogLevel.Info, $"[AI回复 → {player}] {response}");
        }
        catch (Exception ex)
        {
            _output.Write(_name, LogLevel.Error, $"AI处理失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 给玩家消息加上来源标注，使代理知道该请求来自哪个服务器的哪个玩家，
    /// 从而能用 <c>$server ck op &lt;服务器&gt; &lt;玩家&gt;</c> 判断其是否为管理员。
    /// </summary>
    /// <param name="message">玩家消息内容。</param>
    /// <param name="player">玩家名。</param>
    /// <returns>带来源标注的消息。</returns>
    private string WithPlayerContext(string message, string player)
        => $"[来自服务器 '{_name}' 的玩家 '{player}'] {message}";

    // ---------- 输出处理 ----------
    private void OnOutputReceived(object? sender, string data)
    {
        if (string.IsNullOrEmpty(data))
            return;

        // 玩家聊天：优先判定是否为授权作答，其次判定是否为 AI 触发命令
        // 自己发出的 tellraw 回显（含 [AI]）不再触发，避免自回环
        if (!data.Contains("[AI]", StringComparison.Ordinal) &&
            MinecraftChatParser.TryParsePlayerChat(data, out var chat))
        {
            // 1. 该玩家是否正在等待授权提问？是则把作答路由过去
            if (_permissionGatewayFactory?.Invoke().SubmitPlayerResponse(this, chat.Player, chat.Message) == true)
            {
                // 已作为授权作答消费，不再当作 AI 命令
            }
            else if (MinecraftChatParser.CountAngleBracketPairs(chat.Message) <= 1 &&
                     MinecraftChatParser.TryParseAiCommand(chat.Message, out var trigger))
            {
                _ = Task.Run(() => HandleAICommandAsync(trigger.ConfigName, trigger.IsAgent, trigger.Content, chat.Player));
            }
        }

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
