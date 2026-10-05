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
/// 代理命令授权网关，负责在执行代理发起的命令前向“有权授权的一方”请求许可，
/// 并管理“总是允许”白名单与会话内一次性放行。
/// </summary>
public interface IAgentPermissionGateway
{
    /// <summary>
    /// 授权请求的来源。
    /// </summary>
    /// <param name="Server">来源服务器；为 null 表示来自本地控制台。</param>
    /// <param name="Player">来源玩家名；为 null 表示来自本地控制台。</param>
    /// <param name="FromConsole">是否来自本地控制台。</param>
    public readonly record struct Requester(IServer? Server, string? Player, bool FromConsole)
    {
        /// <summary>构造一个来自本地控制台的请求来源。</summary>
        /// <returns>控制台来源。</returns>
        public static Requester Console => new(null, null, true);

        /// <summary>构造一个来自指定服务器玩家的请求来源。</summary>
        /// <param name="server">来源服务器。</param>
        /// <param name="player">玩家名。</param>
        /// <returns>玩家来源。</returns>
        public static Requester FromPlayer(IServer server, string player) => new(server, player, false);
    }

    /// <summary>
    /// 判断命令是否已被持久化白名单覆盖（不产生任何提问）。
    /// </summary>
    /// <param name="command">完整命令文本。</param>
    /// <returns>无需询问时返回 true。</returns>
    bool IsAllowedByPolicy(string command);

    /// <summary>
    /// 就一条命令请求授权。控制台来源向本地操作员提问；玩家来源先校验其是否为管理员，
    /// 是则把提问发到游戏内并等待该玩家作答，否则直接拒绝。
    /// </summary>
    /// <param name="command">要执行的完整命令文本。</param>
    /// <param name="reason">模型给出的执行理由，用于展示给授权方。</param>
    /// <param name="requester">授权请求的来源。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>授权裁决结果。</returns>
    Task<AgentPermissionResult> RequestAsync(
        string command,
        string? reason,
        Requester requester,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 提交一名玩家对授权提问的作答（由服务器聊天行驱动）。
    /// </summary>
    /// <param name="server">作答玩家所在服务器。</param>
    /// <param name="player">作答玩家名。</param>
    /// <param name="message">玩家发送的聊天文本。</param>
    /// <returns>该作答被接纳（存在对应提问且内容有效）时返回 true。</returns>
    bool SubmitPlayerResponse(IServer server, string player, string message);

    /// <summary>
    /// 消费一条会话内一次性放行记录，成功消费表示该命令此前已获批准。
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
