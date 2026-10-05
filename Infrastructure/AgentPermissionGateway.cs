using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 代理命令授权网关实现：通过输入读取器向本地操作员提问，依据作答决定放行、
/// 拒绝或写入持久化白名单，并管理会话内一次性放行记录。
/// </summary>
public class AgentPermissionGateway : IAgentPermissionGateway
{
    /// <summary>输入读取器，用于在提问期间独占读取一行用户输入。</summary>
    private readonly IInputReader _input;
    /// <summary>输出写入器，用于展示提问与记录日志。</summary>
    private readonly IOutputWriter _output;
    /// <summary>配置存储，用于持久化“总是允许”白名单。</summary>
    private readonly IConfigurationStore _configStore;

    /// <summary>
    /// 会话内一次性放行记录：命令文本（忽略大小写） -> 剩余放行次数。
    /// 用于保证“先询问、后执行”两步流程不会重复提问。
    /// </summary>
    private readonly Dictionary<string, int> _sessionPasses = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>保护 <see cref="_sessionPasses"/> 的同步锁。</summary>
    private readonly object _passLock = new();

    /// <summary>等待操作员作答的最长时间，超时视为拒绝。</summary>
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// 初始化授权网关。
    /// </summary>
    /// <param name="input">输入读取器，提供独占读取能力。</param>
    /// <param name="output">输出写入器。</param>
    /// <param name="configStore">配置存储，用于读写白名单。</param>
    public AgentPermissionGateway(IInputReader input, IOutputWriter output, IConfigurationStore configStore)
    {
        _input = input;
        _output = output;
        _configStore = configStore;
    }

    /// <summary>
    /// 读取当前生效的授权配置；缺失时回退到内置默认值（不落盘）。
    /// </summary>
    /// <returns>代理授权配置对象。</returns>
    private AgentPermissions GetPermissions()
    {
        var config = _configStore.LoadConfig();
        return config.AgentPermissions ?? new AgentPermissions();
    }

    /// <summary>
    /// 判断命令是否已被白名单覆盖，强制询问命令始终返回 false。
    /// </summary>
    /// <param name="command">完整命令文本。</param>
    /// <returns>命中白名单时返回 true。</returns>
    public bool IsAllowedByPolicy(string command)
        => GetPermissions().IsAlwaysAllowed(command);

    /// <summary>
    /// 就一条命令向本地操作员请求授权。
    /// </summary>
    /// <param name="command">要执行的完整命令文本。</param>
    /// <param name="reason">模型给出的执行理由。</param>
    /// <param name="allowPrompt">是否允许弹出交互提问；为 false 时仅依据白名单裁决。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>授权裁决结果。</returns>
    public async Task<AgentPermissionResult> RequestAsync(
        string command,
        string? reason,
        bool allowPrompt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new AgentPermissionResult(AgentPermissionDecision.Denied, "命令为空，已拒绝");

        var permissions = GetPermissions();

        // 白名单命中：无需人工确认
        if (permissions.IsAlwaysAllowed(command))
            return new AgentPermissionResult(AgentPermissionDecision.AllowedByPolicy, "该命令已在允许列表中");

        // 非控制台来源（如服务器/玩家发起的请求）不得借用本地操作员的授权
        if (!allowPrompt)
        {
            var denyReason = permissions.IsAlwaysAsk(command)
                ? $"{AgentPermissions.GetCommandName(command)} 属于必须人工确认的高风险命令，无法自动授权"
                : "该命令不在允许列表中，且当前请求不是来自控制台，无法向操作员确认";
            _output.Write($"AI/Permission", LogLevel.Warning, $"自动拒绝代理命令（无法询问操作员）: {command}");
            return new AgentPermissionResult(AgentPermissionDecision.Denied, denyReason);
        }

        // 展示提问并等待作答
        var answer = await PromptAsync(command, reason, permissions, cancellationToken);

        switch (answer)
        {
            case "a":
            case "always":
                // “总是允许”只对非强制询问命令生效
                if (TryPersistAllow(command))
                {
                    _output.Write("AI/Permission", LogLevel.Success, $"已加入允许列表，后续同类命令不再询问: {command}");
                    return new AgentPermissionResult(AgentPermissionDecision.Allowed, "操作员已批准该命令，并加入允许列表");
                }
                _output.Write("AI/Permission", LogLevel.Warning,
                    $"{AgentPermissions.GetCommandName(command)} 必须每次确认，无法加入允许列表");
                return new AgentPermissionResult(AgentPermissionDecision.Allowed, "操作员已批准本次执行（该命令每次都需要确认）");

            case "y":
            case "yes":
                return new AgentPermissionResult(AgentPermissionDecision.Allowed, "操作员已批准该命令");

            case "n":
            case "no":
                return new AgentPermissionResult(AgentPermissionDecision.Denied, "操作员拒绝了该命令");

            case "":
                return new AgentPermissionResult(AgentPermissionDecision.Unavailable,
                    "等待操作员确认超时或输入被取消，命令未执行");

            default:
                // 无法识别的作答按拒绝处理，避免误放行
                return new AgentPermissionResult(AgentPermissionDecision.Denied,
                    $"无法识别的作答，按拒绝处理（命令未执行）: {command}");
        }
    }

    /// <summary>
    /// 向操作员展示命令与理由并独占读取一行作答，返回归一化后的答案关键字。
    /// </summary>
    /// <param name="command">待授权的命令。</param>
    /// <param name="reason">模型给出的理由。</param>
    /// <param name="permissions">当前授权配置，用于决定提示文案。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>归一化答案：y/yes、a/always、n/no，或空字符串表示超时取消。</returns>
    private async Task<string> PromptAsync(
        string command,
        string? reason,
        AgentPermissions permissions,
        CancellationToken cancellationToken)
    {
        var forced = permissions.IsAlwaysAsk(command);

        _output.Write("AI/Permission", LogLevel.Warning, "=============== 代理请求执行命令 ===============");
        _output.Write("AI/Permission", LogLevel.Warning, $"命令: {command}");
        if (!string.IsNullOrWhiteSpace(reason))
            _output.Write("AI/Permission", LogLevel.Info, $"理由: {reason}");
        if (forced)
            _output.Write("AI/Permission", LogLevel.Warning, "该命令属于高风险命令，每次执行都必须人工确认。");

        _output.Write("AI/Permission", LogLevel.Warning, forced
            ? "是否允许本次执行？[y=允许 / n=拒绝]"
            : "是否允许本次执行？[y=允许本次 / a=总是允许 / n=拒绝]");

        // 提示已通过输出写入器打印，此处独占读取一行作答
        var raw = await _input.ReadLineAsync(cancellationToken, PromptTimeout);
        return raw.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 将命令写入持久化白名单；强制询问命令会被拒绝。
    /// </summary>
    /// <param name="command">要持久化放行的命令。</param>
    /// <returns>成功写入时返回 true。</returns>
    private bool TryPersistAllow(string command)
    {
        var config = _configStore.LoadConfig();
        config.AgentPermissions ??= new AgentPermissions();

        if (!config.AgentPermissions.TryAddToAllowList(command))
            return false;

        try
        {
            _configStore.SaveConfig(config);
            return true;
        }
        catch (Exception ex)
        {
            // 持久化失败时回滚内存中的新增项，避免出现“看起来已允许但重启即失效”的假象
            config.AgentPermissions.AllowList.RemoveAll(
                p => string.Equals(p.Trim(), command.Trim(), StringComparison.OrdinalIgnoreCase));
            _output.Write("AI/Permission", LogLevel.Error, $"写入允许列表失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 注册一条会话内一次性放行记录。
    /// </summary>
    /// <param name="command">已获批准的命令文本。</param>
    public void GrantSessionPass(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        var key = command.Trim();

        lock (_passLock)
        {
            _sessionPasses[key] = _sessionPasses.GetValueOrDefault(key) + 1;
        }
    }

    /// <summary>
    /// 消费一条会话内一次性放行记录。
    /// </summary>
    /// <param name="command">即将执行的命令文本。</param>
    /// <returns>成功消费时返回 true。</returns>
    public bool TryConsumeSessionPass(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var key = command.Trim();

        lock (_passLock)
        {
            if (!_sessionPasses.TryGetValue(key, out var count) || count <= 0)
                return false;

            if (count == 1)
                _sessionPasses.Remove(key);
            else
                _sessionPasses[key] = count - 1;

            return true;
        }
    }
}
