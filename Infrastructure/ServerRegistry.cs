using Microsoft.Extensions.DependencyInjection;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 服务器注册表：维护所有服务器的字典、高亮选择与整体生命周期管理。
/// </summary>
public class ServerRegistry : IServerRegistry, IDisposable
{
    /// <summary>
    /// 服务器字典，键为服务器名称。
    /// 采用“构建新字典后整体发布”的方式更新：读者（输出泵、状态查询、代理校验）
    /// 永远看到一份完整且不再变动的快照，避免遍历时被 <see cref="Reload"/> 修改而抛异常。
    /// </summary>
    private volatile Dictionary<string, IServer> _servers = new(StringComparer.Ordinal);
    // 当前高亮（选中）的服务器名称
    private string? _highlighted;
    // 控制台输出写入器
    private readonly IOutputWriter _output;
    // 服务提供器，用于为每个服务器创建独立的进程实例
    private readonly IServiceProvider _serviceProvider;
    // AI 服务的惰性工厂
    private readonly Func<IAgentService> _agentServiceFactory;

    // 隐藏输出名单的短时缓存（避免每条服务器输出都读一次配置文件）
    private readonly Lock _hiddenCacheLock = new();
    private List<string>? _hiddenCache;
    private DateTime _hiddenCacheExpiry = DateTime.MinValue;

    /// <summary>
    /// 根据配置创建服务器注册表，并为每个服务器路径创建对应的 ServerManager。
    /// </summary>
    /// <param name="config">全局应用配置。</param>
    /// <param name="output">控制台输出写入器。</param>
    /// <param name="serviceProvider">服务提供器。</param>
    /// <param name="agentServiceFactory">
    /// AI 服务的惰性工厂。直接用 IAgentService 注入会形成
    /// ICommandExecutor → IServerRegistry → IAgentService → IServerRegistry 的构造循环，故按需解析。
    /// </param>
    public ServerRegistry(
        AppConfig config,
        IOutputWriter output,
        IServiceProvider serviceProvider,
        Func<IAgentService> agentServiceFactory)
    {
        _output = output;
        _serviceProvider = serviceProvider;
        _agentServiceFactory = agentServiceFactory;
        var servers = new Dictionary<string, IServer>(StringComparer.Ordinal);
        foreach (var kv in config.ServerPaths)
        {
            var sm = new ServerManager(
                kv.Key,
                kv.Value,
                output,
                CreateProcess(),
                agentServiceFactory,
                config,
                GetPermissionGateway,
                IsOutputHidden);
            servers[kv.Key] = sm;
        }
        _servers = servers;
    }

    /// <summary>
    /// 判断服务器输出是否在控制台隐藏（<c>$server hd/uhd</c>）。
    /// 每次输出都会调用本方法，若每次都读盘会变成日志热路径上的同步文件 IO，
    /// 因此这里缓存隐藏名单 1 秒（对“开关立即生效”的体感没有影响）。
    /// </summary>
    /// <param name="name">服务器名。</param>
    /// <returns>隐藏时返回 true。</returns>
    private bool IsOutputHidden(string name)
    {
        if (_serviceProvider.GetService(typeof(IConfigurationStore)) is not IConfigurationStore store)
            return false;

        List<string> hidden;
        lock (_hiddenCacheLock)
        {
            if (_hiddenCache == null || DateTime.UtcNow >= _hiddenCacheExpiry)
            {
                _hiddenCache = store.LoadConfig().HiddenServers.ToList();
                _hiddenCacheExpiry = DateTime.UtcNow.AddSeconds(1);
            }
            hidden = _hiddenCache;
        }

        return hidden.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    // 每个服务器拥有独立的进程实例，避免多个服务器共享同一个进程/输出事件
    private IServerProcess CreateProcess() => _serviceProvider.GetRequiredService<IServerProcess>();

    /// <summary>
    /// 授权网关的惰性工厂：服务器需要把玩家的授权作答路由到对应提问时才解析。
    /// 使用工厂而非直接注入，避免“注册表 → 服务器 → 网关 → 注册表”的构造循环。
    /// </summary>
    /// <returns>授权网关实例。</returns>
    private IAgentPermissionGateway GetPermissionGateway()
        => _serviceProvider.GetRequiredService<IAgentPermissionGateway>();

    // 在 Reload 中也传递
    /// <summary>
    /// 按新配置重建服务器列表：仍在运行且仍存在于新配置中的服务器实例被复用，其余重新创建，被移除的旧实例会被释放。
    /// </summary>
    /// <param name="newConfig">新的全局配置。</param>
    public void Reload(AppConfig newConfig)
    {
        var oldServers = _servers.Values.ToList();
        var currentRunning = oldServers.Where(s => s.Status == ServerStatus.Running).ToDictionary(s => s.Name);

        // 先在新字典上完成全部改写，最后一次性发布
        var servers = new Dictionary<string, IServer>(StringComparer.Ordinal);
        foreach (var kv in newConfig.ServerPaths)
        {
            IServer sm;
            if (currentRunning.TryGetValue(kv.Key, out var runningServer))
            {
                sm = runningServer;
                // 复用运行中的实例时必须刷新其配置引用，
                // 否则它仍会用旧的 DefaultAIConfig 处理玩家触发的 AI 请求
                if (sm is ServerManager manager) manager.UpdateAppConfig(newConfig);
            }
            else
            {
                sm = new ServerManager(
                    kv.Key,
                    kv.Value,
                    _output,
                    CreateProcess(),
                    _agentServiceFactory,
                    newConfig,
                    GetPermissionGateway,
                    IsOutputHidden);
            }
            servers[kv.Key] = sm;
        }

        _servers = servers;

        // 释放被移除且不再复用的服务器（含其进程句柄；仍有进程在跑时会先终止进程树）
        foreach (var old in oldServers)
        {
            if (!servers.ContainsValue(old) && old is IDisposable d)
                d.Dispose();
        }

        if (_highlighted != null && !servers.ContainsKey(_highlighted))
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

    // 程序退出时由 DI 容器调用，释放所有服务器及其进程句柄
    public void Dispose()
    {
        var servers = _servers;
        _servers = new Dictionary<string, IServer>(StringComparer.Ordinal);
        foreach (var s in servers.Values)
            (s as IDisposable)?.Dispose();
    }
}
