namespace MSL_CLI.Core.Ports;

/// <summary>
/// 代理命令授权的裁决结果。
/// </summary>
public enum AgentPermissionDecision
{
    /// <summary>允许本次执行。</summary>
    Allowed,
    /// <summary>已被持久化白名单覆盖，无需询问。</summary>
    AllowedByPolicy,
    /// <summary>执行被拒绝。</summary>
    Denied,
    /// <summary>询问被取消、超时或当前环境无法提问。</summary>
    Unavailable
}

/// <summary>
/// 一次授权裁决的完整结果。
/// </summary>
/// <param name="Decision">裁决结论。</param>
/// <param name="Message">可直接展示或回传给模型的说明文本。</param>
public readonly record struct AgentPermissionResult(AgentPermissionDecision Decision, string message)
{
    /// <summary>是否允许执行命令。</summary>
    public bool IsAllowed => Decision is AgentPermissionDecision.Allowed or AgentPermissionDecision.AllowedByPolicy;
}

/// <summary>
/// 代理命令授权网关，负责在执行代理发起的命令前向本地操作员请求许可，
/// 并管理“总是允许”白名单与会话内一次性放行。
/// </summary>
public interface IAgentPermissionGateway
{
    /// <summary>
    /// 判断命令是否已被持久化白名单覆盖（不产生任何提问）。
    /// </summary>
    /// <param name="command">完整命令文本。</param>
    /// <returns>无需询问时返回 true。</returns>
    bool IsAllowedByPolicy(string command);

    /// <summary>
    /// 就一条命令向本地操作员请求授权，并在得到“总是允许”时持久化白名单。
    /// </summary>
    /// <param name="command">要执行的完整命令文本。</param>
    /// <param name="reason">模型给出的执行理由，用于展示给操作员。</param>
    /// <param name="allowPrompt">是否允许弹出交互提问；为 false 时仅依据白名单裁决。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>授权裁决结果。</returns>
    Task<AgentPermissionResult> RequestAsync(
        string command,
        string? reason,
        bool allowPrompt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 消费一条会话内一次性放行记录，成功消费表示该命令此前已获操作员批准。
    /// </summary>
    /// <param name="command">完整命令文本。</param>
    /// <returns>存在并成功消费时返回 true。</returns>
    bool TryConsumeSessionPass(string command);

    /// <summary>
    /// 注册一条会话内一次性放行记录，供随后的执行阶段消费。
    /// </summary>
    /// <param name="command">完整命令文本。</param>
    void GrantSessionPass(string command);
}
