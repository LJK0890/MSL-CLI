using MSL_CLI.Core.Ports;

/// <summary>
/// AI 代理服务接口，定义与 AI 模型进行聊天交互及代理任务执行的能力。
/// </summary>
public interface IAgentService
{
    /// <summary>
    /// 当前已登记的 AI 配置名集合（随 <see cref="ReloadConfig"/> 一起热更新）。
    /// 供游戏内 <c>$chat [配置名] 消息</c> 这类“配置名可选”的语法判定首个词究竟是不是配置名。
    /// </summary>
    IReadOnlyCollection<string> ConfigNames { get; }

    /// <summary>
    /// 以聊天模式向指定 AI 配置发送消息并获取回复。
    /// </summary>
    /// <param name="configName">AI 配置名称。</param>
    /// <param name="message">用户发送的聊天消息。</param>
    /// <param name="source">可选的来源信息（服务器实例及其上下文），用于让 AI 感知当前服务器。</param>
    /// <returns>包含所用模型名称与回复内容的元组。</returns>
    Task<(string model, string response)> ChatAsync(
        string configName,
        string message,
        (IServer,string)? source = null);   // 新增可选参数

    /// <summary>
    /// 以代理模式向指定 AI 配置发送指令，由 AI 自主执行多步任务后返回最终回复。
    /// </summary>
    /// <param name="configName">AI 配置名称。</param>
    /// <param name="instruction">代理任务的指令文本。</param>
    /// <param name="source">可选的来源信息（服务器实例及其上下文），用于让 AI 感知当前服务器。</param>
    /// <returns>包含所用模型名称与回复内容的元组。</returns>
    Task<(string model, string response)> AgentAsync(
        string configName,
        string instruction,
        (IServer, string)? source = null);   // 新增可选参数

    /// <summary>
    /// 用新的应用配置替换内存中的 AI 实例集合，
    /// 使 $ai add / $ai rm、$app cfg 修改、$app reload 等操作无需重启即可生效。
    /// </summary>
    /// <param name="newConfig">新的应用配置。</param>
    void ReloadConfig(MSL_CLI.Core.Domain.AppConfig newConfig);
}
