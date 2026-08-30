using MSL_CLI.Core.Ports;

namespace MSL_CLI.Core.Domain;

/// <summary>
/// 命令执行参数上下文，封装命令原始文本及命令处理所需的服务依赖。
/// </summary>
public class CommandArgs
{
    /// <summary>
    /// 命令的原始文本。
    /// </summary>
    public string Raw { get; }
    /// <summary>
    /// 服务器注册表服务，用于查找与管理服务器实例。
    /// </summary>
    public IServerRegistry ServerRegistry { get; }
    /// <summary>
    /// AI 代理服务，用于处理 AI 相关命令。
    /// </summary>
    public IAgentService AgentService { get; }
    /// <summary>
    /// 配置存储服务，用于读写应用配置。
    /// </summary>
    public IConfigurationStore ConfigStore { get; }
    /// <summary>
    /// 命令解析器（可选），用于进一步解析命令。
    /// </summary>
    public ICommandParser? Parser { get; set; }

    /// <summary>
    /// 初始化命令参数上下文。
    /// </summary>
    /// <param name="raw">命令的原始文本。</param>
    /// <param name="registry">服务器注册表服务。</param>
    /// <param name="agent">AI 代理服务。</param>
    /// <param name="config">配置存储服务。</param>
    public CommandArgs(string raw, IServerRegistry registry, IAgentService agent, IConfigurationStore config)
    {
        Raw = raw;
        ServerRegistry = registry;
        AgentService = agent;
        ConfigStore = config;
    }
}
