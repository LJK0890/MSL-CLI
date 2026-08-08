using MSL_CLI.AI;
using MSL_CLI.Commands;
using MSL_CLI.Config;
using MSL_CLI.Server;
using System.Text.Json;
using static MSL_CLI.IO.IO;

namespace MSL_CLI.Services;

/// <summary>
/// 全局管理器，负责加载/保存应用程序配置，并管理所有 ServerManager 实例。
/// </summary>
public class GlobalManager : IDisposable
{
    private AppConfig appConfig;
    private bool disposed = false;
    private Dictionary<string, ServerManager> serverManagers;

    private readonly Config.Config _configService;
    private readonly CommandParser _commandParser;
    private string? _highlightedServerName = null;
    public bool ExitRequested { get; private set; } = false;

    public AIModule AI { get; private set; }

    public GlobalManager()
    {
        // 初始化服务
        _configService = new Config.Config();
        _commandParser = new CommandParser(this);

        // 加载配置
        appConfig = _configService.LoadConfig();

        // 初始化业务逻辑
        serverManagers = new Dictionary<string, ServerManager>();
        foreach (var serverPathKV in appConfig.ServerPaths)
        {
            var serverManager = new ServerManager(serverPathKV.Key, serverPathKV.Value);
            serverManagers.Add(serverPathKV.Key, serverManager);
        }

        AI = new AIModule(this);

        Print("Global/Config", LogLevel.INFO, $"已加载配置，包含 {appConfig.ServerPaths.Count} 个服务器路径。", includeTimestamp: true);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        if (disposing)
        {
            StopAllServers();

            if (appConfig != null)
            {
                try
                {
                    // 委托给服务保存
                    _configService.SaveConfig(appConfig);
                    Print("Global/Config", LogLevel.INFO, "配置已保存。", includeTimestamp: true);
                }
                catch (Exception ex)
                {
                    Print("Global/Config", LogLevel.ERROR, $"保存配置失败: {ex.Message}", includeTimestamp: true);
                }
            }
        }
        disposed = true;
    }

    ~GlobalManager()
    {
        Dispose(false);
    }

    /// <summary>
    /// 打印当前配置到控制台（用于调试）。
    /// </summary>
    public void PrintConfig(bool capture = false)
    {
        if (appConfig == null)
        {
            Print("Global/Config", LogLevel.WARNING, "配置未加载", includeTimestamp: true, capture: capture, false);
            return;
        }
        Print("Global/Config", LogLevel.WARNING, "EnableAI: " + appConfig.EnableAI, includeTimestamp: true, capture: capture, false);
        Print("Global/Config", LogLevel.WARNING, "AIConfigs:", includeTimestamp: true, capture: capture, false);
        foreach (var kvp in appConfig.AIConfigs)
        {
            string keyDisplay = string.IsNullOrEmpty(kvp.Value.ApiKey) ? "(empty)" : "****";
            Print("Global/AI", LogLevel.WARNING, $" {kvp.Key}:", includeTimestamp: true, capture: capture, false);
            Print("Global/AI", LogLevel.WARNING, $" Url={kvp.Value.Url}", includeTimestamp: true, capture: capture, false);
            Print("Global/AI", LogLevel.WARNING, $" Model={kvp.Value.Model}", includeTimestamp: true, capture: capture, false);
            Print("Global/AI", LogLevel.WARNING, $" ApiKey={keyDisplay}", includeTimestamp: true, capture: capture, false);
            Print("Global/AI", LogLevel.WARNING, $" UseApiKeyEnv={kvp.Value.UseApiKeyEnv}", includeTimestamp: true, capture: capture, false);
            Print("Global/AI", LogLevel.WARNING, $" ApiKeyEnv={kvp.Value.ApiKeyEnv}", includeTimestamp: true, capture: capture, false);
        }
        Print("Global/Config", LogLevel.WARNING, "ServerPaths:", includeTimestamp: true, capture: capture, false);
        foreach (var kvp in appConfig.ServerPaths)
        {
            Print("Global/Config", LogLevel.WARNING, $" {kvp.Key}: {kvp.Value}", includeTimestamp: true, capture: capture, false);
        }
        Print("Global/Config", LogLevel.SUCCESS, "", includeTimestamp: true, capture: capture, true);
    }

    public void MainLoop()
    {
        while (!ExitRequested)
        {
            InputParse();
        }
        Print("Global", LogLevel.INFO, "程序已退出。", true);
    }

    /// <summary>获取当前配置对象（只读）</summary>
    public AppConfig GetConfig() => appConfig;

    /// <summary>获取服务器管理器字典</summary>
    public Dictionary<string, ServerManager> GetServerManagers() => serverManagers;

    /// <summary>重新加载配置</summary>
    public void ReloadConfig()
    {
        // 重新加载配置（需要 Config 服务提供重新加载方法）
        appConfig = _configService.LoadConfig(); // 假设 Config 类有 LoadConfig 方法
                                                 // 重建服务器管理器（或更新现有）
        RebuildServerManagers();
    }

    private void RebuildServerManagers()
    {
        // 简单实现：清空并重新创建
        serverManagers.Clear();
        foreach (var kvp in appConfig.ServerPaths)
        {
            var serverManager = new ServerManager(kvp.Key, kvp.Value);
            serverManagers.Add(kvp.Key, serverManager);
        }
    }

    private void InputParse()
    {
        Scan(out List<string> lines);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("$"))
            {
                _commandParser.Execute(line);
            }
            else
            {
                _commandParser.Execute($"$sendn {line}");
            }
        }
    }

    public string? HighlightedServerName => _highlightedServerName;

    // 切换高亮服务器
    public bool SwitchHighlight(string serverName)
    {
        if (!serverManagers.ContainsKey(serverName))
            return false;
        _highlightedServerName = serverName;
        return true;
    }

    // 获取高亮服务器的 ServerManager（可能为 null）
    public ServerManager? GetHighlightedServer()
    {
        if (_highlightedServerName == null || !serverManagers.TryGetValue(_highlightedServerName, out var sm))
            return null;
        return sm;
    }

    // 请求退出
    public void RequestExit()
    {
        ExitRequested = true;
    }

    public Dictionary<string, string> GetCommandDescriptions() => _commandParser.GetCommandDescriptions();

    /// <summary>
    /// 同步停止所有服务器（内部调用异步方法并阻塞）
    /// </summary>
    public void StopAllServers()
    {
        StopAllServersAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 异步停止所有正在运行的服务器
    /// </summary>
    public async Task StopAllServersAsync()
    {
        // 获取当前状态不是 Stopped 的服务器
        var running = serverManagers.Where(kvp => kvp.Value.Status != ServerStatus.Stopped).ToList();
        if (running.Count == 0)
        {
            Print("Global", LogLevel.INFO, "没有正在运行的服务器需要停止。", true);
            return;
        }

        Print("Global", LogLevel.INFO, $"正在停止 {running.Count} 个服务器...", true);

        // 串行停止每个服务器（也可以并行，但串行更清晰，避免输出混乱）
        foreach (var kvp in running)
        {
            var server = kvp.Value;
            var name = kvp.Key;
            Print("Global", LogLevel.INFO, $"停止服务器: {name} ...", true);
            try
            {
                // 调用 StopAsync，内部已有超时和强制终止机制
                await server.StopAsync(false);
                Print("Global", LogLevel.SUCCESS, $"服务器 {name} 已停止。", true);
            }
            catch (Exception ex)
            {
                Print("Global", LogLevel.ERROR, $"停止服务器 {name} 失败: {ex.Message}，尝试强制终止...", true);
                try
                {
                    await server.StopAsync(true);
                }
                catch (Exception forceEx)
                {
                    Print("Global", LogLevel.ERROR, $"强制停止 {name} 也失败: {forceEx.Message}", true);
                }
            }
        }

        Print("Global", LogLevel.INFO, "所有服务器已停止。", true);
    }

    /// <summary>
    /// 获取指定服务器的 ServerProperties 实例
    /// </summary>
    public ServerProperties? GetServerProperties(string name)
    {
        if (serverManagers.TryGetValue(name, out var sm))
            return sm.ServerProperties;
        return null;
    }

    /// <summary>
    /// 获取当前高亮服务器的 ServerProperties 实例
    /// </summary>
    public ServerProperties? GetHighlightedServerProperties()
    {
        if (_highlightedServerName == null)
            return null;
        return GetServerProperties(_highlightedServerName);
    }

    /// <summary>
    /// 获取指定服务器的 OP 列表
    /// </summary>
    public List<string> GetServerOps(string serverName)
    {
        if (serverManagers.TryGetValue(serverName, out var sm))
            return sm.GetOps();
        return new List<string>();
    }

    /// <summary>
    /// 检查指定服务器的玩家是否为 OP
    /// </summary>
    public bool IsServerOp(string serverName, string playerName)
    {
        if (serverManagers.TryGetValue(serverName, out var sm))
            return sm.IsOp(playerName);
        return false;
    }

    /// <summary>
    /// 获取所有服务器名称列表
    /// </summary>
    public List<string> GetAllServerNames() => serverManagers.Keys.ToList();

    /// <summary>
    /// 获取所有正在运行的服务器管理器
    /// </summary>
    public List<ServerManager> GetRunningServers()
    {
        return serverManagers.Values.Where(sm => sm.Status == ServerStatus.Running).ToList();
    }
}