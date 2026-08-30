using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Core.UseCases;

/// <summary>
/// 服务器编排器，负责对服务器执行启停等高层操作。
/// </summary>
public class ServerOrchestrator
{
    private readonly IServerRegistry _registry;
    private readonly IOutputWriter _output;

    /// <summary>
    /// 初始化服务器编排器。
    /// </summary>
    /// <param name="registry">服务器注册表，用于按名称查找服务器。</param>
    /// <param name="output">输出写入器，用于输出编排过程中的日志信息。</param>
    public ServerOrchestrator(IServerRegistry registry, IOutputWriter output)
    {
        _registry = registry;
        _output = output;
    }

    /// <summary>
    /// 异步启动指定名称的服务器。
    /// </summary>
    /// <param name="name">要启动的服务器名称。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task StartServerAsync(string name)
    {
        var server = _registry.GetServer(name);
        // 服务器不存在时记录警告并直接返回
        if (server == null) { _output.Write("Orchestrator", LogLevel.Warning, $"服务器 {name} 不存在"); return; }
        await server.StartAsync();
    }

    /// <summary>
    /// 异步停止指定名称的服务器。
    /// </summary>
    /// <param name="name">要停止的服务器名称。</param>
    /// <param name="force">是否强制停止（跳过正常关服流程）。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task StopServerAsync(string name, bool force = false)
    {
        var server = _registry.GetServer(name);
        // 服务器不存在时记录警告并直接返回
        if (server == null) { _output.Write("Orchestrator", LogLevel.Warning, $"服务器 {name} 不存在"); return; }
        await server.StopAsync(force);
    }
}
