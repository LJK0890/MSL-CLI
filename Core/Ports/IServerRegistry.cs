using MSL_CLI.Core.Domain;

namespace MSL_CLI.Core.Ports;

/// <summary>
/// 服务器注册表接口，管理所有服务器实例的注册、查询以及高亮（当前选中）状态的维护。
/// </summary>
public interface IServerRegistry
{
    /// <summary>
    /// 所有已注册服务器实例的只读字典，键为服务器名称。
    /// </summary>
    IReadOnlyDictionary<string, IServer> All { get; }
    /// <summary>
    /// 当前高亮（选中）的服务器名称；未选中时为 null。
    /// </summary>
    string? HighlightedServerName { get; }
    /// <summary>
    /// 根据名称获取服务器实例。
    /// </summary>
    /// <param name="name">服务器名称。</param>
    /// <returns>匹配的服务器实例；不存在时返回 null。</returns>
    IServer? GetServer(string name);
    /// <summary>
    /// 获取当前高亮的服务器实例。
    /// </summary>
    /// <returns>当前高亮的服务器实例；未高亮时返回 null。</returns>
    IServer? GetHighlightedServer();
    /// <summary>
    /// 将高亮（选中）状态切换到指定名称的服务器。
    /// </summary>
    /// <param name="name">要切换到的服务器名称。</param>
    /// <returns>切换是否成功（目标服务器是否存在）。</returns>
    bool SwitchHighlight(string name);
    /// <summary>
    /// 获取所有当前正在运行的服务器实例列表。
    /// </summary>
    /// <returns>运行中服务器的列表。</returns>
    List<IServer> GetRunningServers();
    /// <summary>
    /// 异步停止所有正在运行的服务器。
    /// </summary>
    Task StopAllAsync();
    /// <summary>
    /// 根据新的应用配置重新加载服务器注册表。
    /// </summary>
    /// <param name="newConfig">新的应用配置。</param>
    void Reload(AppConfig newConfig);
}
