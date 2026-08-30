using Microsoft.Extensions.DependencyInjection;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 服务器注册表：维护所有服务器的字典、高亮选择与整体生命周期管理。
/// </summary>
public class ServerRegistry : IServerRegistry, IDisposable
{
    // 服务器字典，键为服务器名称
    private readonly Dictionary<string, IServer> _servers = new();
    // 当前高亮（选中）的服务器名称
    private string? _highlighted;
    // 控制台输出写入器
    private readonly IOutputWriter _output;
    // 服务提供器，用于为每个服务器创建独立的进程实例
    private readonly IServiceProvider _serviceProvider;
    // AI 服务
    private readonly IAgentService _agentService;
    // 全局应用配置
    private readonly AppConfig _appConfig;

    /// <summary>
    /// 根据配置创建服务器注册表，并为每个服务器路径创建对应的 ServerManager。
    /// </summary>
    /// <param name="config">全局应用配置。</param>
    /// <param name="output">控制台输出写入器。</param>
    /// <param name="serviceProvider">服务提供器。</param>
    /// <param name="agentService">AI 服务。</param>
    public ServerRegistry(
        AppConfig config,
        IOutputWriter output,
        IServiceProvider serviceProvider,
        IAgentService agentService)
    {
        _output = output;
        _serviceProvider = serviceProvider;
        _agentService = agentService;
        _appConfig = config;    // 保存用于重建
        foreach (var kv in config.ServerPaths)
        {
            var sm = new ServerManager(
                kv.Key,
                kv.Value,
                output,
                CreateProcess(),
                agentService,
                config);
            _servers[kv.Key] = sm;
        }
    }

    // 每个服务器拥有独立的进程实例，避免多个服务器共享同一个进程/输出事件
    private IServerProcess CreateProcess() => _serviceProvider.GetRequiredService<IServerProcess>();

    // 在 Reload 中也传递
    /// <summary>
    /// 按新配置重建服务器列表：仍在运行且仍存在于新配置中的服务器实例被复用，其余重新创建，被移除的旧实例会被释放。
    /// </summary>
    /// <param name="newConfig">新的全局配置。</param>
    public void Reload(AppConfig newConfig)
    {
        var oldServers = _servers.Values.ToList();
        var currentRunning = oldServers.Where(s => s.Status == ServerStatus.Running).ToDictionary(s => s.Name);
        _servers.Clear();
        foreach (var kv in newConfig.ServerPaths)
        {
            IServer sm;
            if (currentRunning.TryGetValue(kv.Key, out var runningServer))
            {
                sm = runningServer;
            }
            else
            {
                sm = new ServerManager(
                    kv.Key,
                    kv.Value,
                    _output,
                    CreateProcess(),
                    _agentService,
                    newConfig);
            }
            _servers[kv.Key] = sm;
        }
        // 释放被移除且不再复用的服务器（含其进程句柄）
        foreach (var old in oldServers)
        {
            if (!_servers.Values.Contains(old) && old is IDisposable d)
                d.Dispose();
        }
        if (_highlighted != null && !_servers.ContainsKey(_highlighted))
            _highlighted = null;
    }

    /// <summary>
    /// 所有服务器（键为服务器名称）。
    /// </summary>
    public IReadOnlyDictionary<string, IServer> All => _servers;
    /// <summary>
    /// 当前高亮的服务器名称；无高亮时为 null。
    /// </summary>
    public string? HighlightedServerName => _highlighted;

    /// <summary>
    /// 按名称获取服务器。
    /// </summary>
    /// <param name="name">服务器名称。</param>
    /// <returns>对应的服务器；不存在时返回 null。</returns>
    public IServer? GetServer(string name) => _servers.GetValueOrDefault(name);
    /// <summary>
    /// 获取当前高亮的服务器；无高亮时返回 null。
    /// </summary>
    /// <returns>当前高亮的服务器实例。</returns>
    public IServer? GetHighlightedServer() => _highlighted != null ? GetServer(_highlighted) : null;

    /// <summary>
    /// 切换当前高亮的服务器。
    /// </summary>
    /// <param name="name">要高亮的服务器名称。</param>
    /// <returns>切换成功返回 true，名称不存在时返回 false。</returns>
    public bool SwitchHighlight(string name)
    {
        if (!_servers.ContainsKey(name)) return false;
        _highlighted = name;
        return true;
    }

    /// <summary>
    /// 获取所有运行中的服务器列表。
    /// </summary>
    /// <returns>状态为 Running 的服务器列表。</returns>
    public List<IServer> GetRunningServers()
        => _servers.Values.Where(s => s.Status == ServerStatus.Running).ToList();

    /// <summary>
    /// 优雅停止所有未停止的服务器。
    /// </summary>
    public async Task StopAllAsync()
    {
        foreach (var s in _servers.Values.Where(s => s.Status != ServerStatus.Stopped))
            await s.StopAsync(false);
    }

    // 从配置构建服务器字典
    private void BuildFromConfig(AppConfig config)
    {
        _servers.Clear();
        foreach (var kv in config.ServerPaths)
        {
            var sm = new ServerManager(kv.Key, kv.Value, _output, CreateProcess(), _agentService, config);
            _servers[kv.Key] = sm;
        }
        // 如果当前高亮服务器不存在，重置
        if (_highlighted != null && !_servers.ContainsKey(_highlighted))
            _highlighted = null;
    }

    // 程序退出时由 DI 容器调用，释放所有服务器及其进程句柄
    public void Dispose()
    {
        foreach (var s in _servers.Values)
            (s as IDisposable)?.Dispose();
        _servers.Clear();
    }
}
