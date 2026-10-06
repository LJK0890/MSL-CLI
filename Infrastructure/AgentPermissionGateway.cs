using System.Collections.Concurrent;
using System.Text.Json;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 代理命令授权网关实现。
/// 控制台来源通过输入读取器向本地操作员提问；玩家来源先校验其是否为服务器管理员，
/// 是则通过 tellraw 把提问发到游戏内并等待该玩家在聊天中作答，否则直接拒绝。
/// 同时管理“总是允许”白名单与会话内一次性放行。
/// </summary>
public class AgentPermissionGateway : IAgentPermissionGateway
{
    /// <summary>输入读取器，用于在控制台提问期间独占读取一行用户输入。</summary>
    private readonly IInputReader _input;
    /// <summary>输出写入器，用于展示提问与记录日志。</summary>
    private readonly IOutputWriter _output;
    /// <summary>配置存储，用于持久化“总是允许”白名单。</summary>
    private readonly IConfigurationStore _configStore;
    /// <summary>命令解析器，用于在执行/授权前校验命令是否存在。</summary>
    private readonly ICommandParser _commandParser;
    /// <summary>服务器注册表，供命令的参数校验钩子使用。</summary>
    private readonly IServerRegistry _serverRegistry;

    /// <summary>
    /// 会话内一次性放行记录：命令文本（忽略大小写） -> 剩余放行次数。
    /// 用于保证“先询问、后执行”两步流程不会重复提问。
    /// </summary>
    private readonly Dictionary<string, int> _sessionPasses = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>保护 <see cref="_sessionPasses"/> 的同步锁。</summary>
    private readonly object _passLock = new();

    /// <summary>
    /// 待作答的游戏内提问：服务器名/玩家名 -> 等待中的作答通道。
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingPrompt> _pendingPrompts = new(StringComparer.Ordinal);

    /// <summary>等待授权方作答的最长时间，超时视为拒绝。</summary>
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// 一次游戏内提问的等待状态。
    /// </summary>
    private sealed class PendingPrompt
    {
        /// <summary>等待玩家作答的任务源。</summary>
        public TaskCompletionSource<string> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// 初始化授权网关。
    /// </summary>
    /// <param name="input">输入读取器，提供控制台独占读取能力。</param>
    /// <param name="output">输出写入器。</param>
    /// <param name="configStore">配置存储，用于读写白名单。</param>
    /// <param name="commandParser">命令解析器，用于校验命令是否存在。</param>
    /// <param name="serverRegistry">服务器注册表，供命令的参数校验钩子使用。</param>
    public AgentPermissionGateway(
        IInputReader input,
        IOutputWriter output,
        IConfigurationStore configStore,
        ICommandParser commandParser,
        IServerRegistry serverRegistry)
    {
        _input = input;
        _output = output;
        _configStore = configStore;
        _commandParser = commandParser;
        _serverRegistry = serverRegistry;
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
    /// 这里同样解析出“命令 + 动作”的授权范围，避免只按命令名匹配时
    /// 被 <c>$server</c> 这类粗粒度白名单条目放行掉强制询问的动作（如 <c>$server del</c>）。
    /// </summary>
    /// <param name="command">完整命令文本。</param>
    /// <returns>命中白名单时返回 true。</returns>
    public bool IsAllowedByPolicy(string command)
    {
        // 命令不存在或参数不合法时一律不放行（不能只因为范围解析失败就退化成“按命令名命中白名单”）
        var validation = CommandInvocationValidator.Validate(_commandParser, command, BuildValidationContext(command));
        if (!validation.IsValid) return false;

        return GetPermissions().IsAlwaysAllowed(command, validation.PermissionScope);
    }

    /// <summary>
    /// 就一条命令请求授权。
    /// </summary>
    /// <param name="command">要执行的完整命令文本。</param>
    /// <param name="reason">模型给出的执行理由。</param>
    /// <param name="requester">授权请求的来源。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>授权裁决结果。</returns>
    public async Task<AgentPermissionResult> RequestAsync(
        string command,
        string? reason,
        IAgentPermissionGateway.Requester requester,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new AgentPermissionResult(AgentPermissionDecision.Denied, "命令为空，已拒绝");

        // 兜底防线：不存在的命令一律不提问、不执行，也不会被写入白名单。
        // 正常情况下 AgentService 已提前拦截，这里保证任何调用方都绕不过去。
        var validation = CommandInvocationValidator.Validate(_commandParser, command, BuildValidationContext(command));
        if (!validation.IsValid)
        {
            _output.Write("AI/Permission", LogLevel.Warning,
                $"拒绝授权请求（命令不存在）: {AgentPermissions.Normalize(command)}");
            return new AgentPermissionResult(AgentPermissionDecision.Denied, validation.Message);
        }

        var permissions = GetPermissions();

        // ---------- 玩家来源：先判定是否为管理员 ----------
        // 注意顺序：管理员校验必须早于白名单判定，否则玩家被取消管理员后
        // 仍能凭历史白名单继续授权（越权）。
        if (!requester.FromConsole)
        {
            var server = requester.Server;
            var player = requester.Player;

            if (server == null || string.IsNullOrEmpty(player))
                return new AgentPermissionResult(AgentPermissionDecision.Denied,
                    "无法确定请求来源，命令未执行");

            // 非管理员直接拒绝，不产生任何提问，也不享受白名单
            if (!IsOperator(server, player))
            {
                _output.Write("AI/Permission", LogLevel.Warning,
                    $"自动拒绝：玩家 '{player}' 不是服务器 '{server.Name}' 的管理员（命令: {command}）");
                return new AgentPermissionResult(AgentPermissionDecision.Denied,
                    $"玩家 '{player}' 不是服务器 '{server.Name}' 的管理员，无权授权命令执行");
            }

            // 管理员且该“命令 + 子动作”已在允许列表中：无需再问
            if (permissions.IsAlwaysAllowed(command, validation.PermissionScope))
                return new AgentPermissionResult(AgentPermissionDecision.AllowedByPolicy,
                    $"'{validation.PermissionScope}' 已在允许列表中");

            // 管理员：把提问发到游戏内并等待其作答
            var playerAnswer = await PromptPlayerAsync(server, player, command, validation.PermissionScope, reason, permissions, cancellationToken);

            // 该玩家已有待回答的提问：直接拒绝，不再进入作答判定
            if (playerAnswer == null)
                return new AgentPermissionResult(AgentPermissionDecision.Denied,
                    $"玩家 '{player}' 已有待确认的提问，本次请求直接拒绝");

            return await ResolveAnswerAsync(playerAnswer, command, validation.PermissionScope, "管理员", server, player);
        }

        // ---------- 控制台来源 ----------
        // 白名单命中：无需人工确认
        if (permissions.IsAlwaysAllowed(command, validation.PermissionScope))
            return new AgentPermissionResult(AgentPermissionDecision.AllowedByPolicy,
                $"'{validation.PermissionScope}' 已在允许列表中");

        var consoleAnswer = await PromptConsoleAsync(command, validation.PermissionScope, reason, permissions, cancellationToken);
        return await ResolveAnswerAsync(consoleAnswer, command, validation.PermissionScope, "操作员", null, null);
    }

    // ---------- 提问 ----------

    /// <summary>
    /// 向本地操作员展示命令与理由并独占读取一行作答。
    /// </summary>
    /// <param name="command">待授权的命令。</param>
    /// <param name="reason">模型给出的理由。</param>
    /// <param name="permissions">当前授权配置，用于决定提示文案。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>归一化答案：y/yes、a/always、n/no，或空字符串表示超时取消。</returns>
    private async Task<string> PromptConsoleAsync(
        string command,
        string scope,
        string? reason,
        AgentPermissions permissions,
        CancellationToken cancellationToken)
    {
        var forced = permissions.IsAlwaysAsk(command, scope);

        _output.Write("AI/Permission", LogLevel.Warning, "=============== 代理请求执行命令 ===============");
        _output.Write("AI/Permission", LogLevel.Warning, $"命令: {command}");
        _output.Write("AI/Permission", LogLevel.Info, $"授权范围: {scope}（批准后该范围的任意参数都不再询问）");
        if (!string.IsNullOrWhiteSpace(reason))
            _output.Write("AI/Permission", LogLevel.Info, $"理由: {reason}");
        if (forced)
            _output.Write("AI/Permission", LogLevel.Warning, "该命令属于高风险命令，每次执行都必须人工确认。");

        _output.Write("AI/Permission", LogLevel.Warning, forced
            ? "是否允许本次执行？[y=允许 / n=拒绝]"
            : "是否允许本次执行？[y=允许本次 / a=总是允许 / n=拒绝]");

        var raw = await _input.ReadLineAsync(cancellationToken, PromptTimeout);
        // 契约只保证返回字符串，这里仍按可能为 null 处理，避免超时变成 NRE
        return (raw ?? string.Empty).Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 把授权提问发到游戏内，并等待指定玩家在聊天中作答。
    /// </summary>
    /// <param name="server">玩家所在服务器。</param>
    /// <param name="player">玩家名。</param>
    /// <param name="command">待授权的命令。</param>
    /// <param name="command">待授权的命令。</param>
    /// <param name="scope">授权范围（命令 + 子动作）。</param>
    /// <param name="reason">模型给出的理由。</param>
    /// <param name="permissions">当前授权配置，用于决定提示文案。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>归一化答案；超时或取消时为空字符串；该玩家已有待作答提问时返回 null。</returns>
    private async Task<string?> PromptPlayerAsync(
        IServer server,
        string player,
        string command,
        string scope,
        string? reason,
        AgentPermissions permissions,
        CancellationToken cancellationToken)
    {
        var forced = permissions.IsAlwaysAsk(command, scope);
        var key = PromptKey(server.Name, player);
        var pending = new PendingPrompt();

        // 同一玩家已有待作答提问时，直接拒绝，避免两个提问互相抢答
        if (!_pendingPrompts.TryAdd(key, pending))
        {
            _output.Write("AI/Permission", LogLevel.Warning,
                $"玩家 '{player}' 已有待确认的提问，本次请求直接拒绝");
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(PromptTimeout);
        using var registration = timeoutCts.Token.Register(() => pending.Completion.TrySetResult(string.Empty));

        try
        {
            _output.Write("AI/Permission", LogLevel.Warning,
                $"代理请求执行命令，正在向管理员 '{player}'（服务器 {server.Name}）确认");
            _output.Write("AI/Permission", LogLevel.Warning, $"命令: {command}");
            _output.Write("AI/Permission", LogLevel.Info, $"授权范围: {scope}");

            // 提问广播到游戏内，便于在线管理员都看到；作答只认发起该提问的玩家
            var hint = forced
                ? "该命令为高风险命令，每次都必须确认。"
                : $"输入 y 允许本次 / a 总是允许（范围 {scope}）/ n 拒绝";
            await SendTellrawAsync(server,
                $"§e[AI] 等待 §f{player}§e 确认执行命令: §f{command}§e。{hint}");

            return await pending.Completion.Task;
        }
        finally
        {
            _pendingPrompts.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// 提交一名玩家对授权提问的作答。
    /// </summary>
    /// <param name="server">作答玩家所在服务器。</param>
    /// <param name="player">作答玩家名。</param>
    /// <param name="message">玩家发送的聊天文本。</param>
    /// <returns>该作答被接纳时返回 true。</returns>
    public bool SubmitPlayerResponse(IServer server, string player, string message)
    {
        var key = PromptKey(server.Name, player);

        // 没有待作答的提问：不是授权作答，交给其他逻辑处理
        if (!_pendingPrompts.TryGetValue(key, out var pending))
            return false;

        // 只有明确的 y/yes、a/always、n/no 才算作答，其余文本不当作答
        if (!MinecraftChatParser.TryParsePermissionAnswer(message, out var answer))
            return false;

        if (!pending.Completion.TrySetResult(answer))
            return false;

        _output.Write("AI/Permission", LogLevel.Info,
            $"玩家 '{player}'（服务器 {server.Name}）作答: {answer}");
        return true;
    }

    // ---------- 裁决 ----------

    /// <summary>
    /// 把归一化答案翻译成裁决结果，并在必要时持久化白名单。
    /// </summary>
    /// <param name="answer">归一化答案关键字。</param>
    /// <param name="command">待授权的命令。</param>
    /// <param name="scope">授权范围（命令 + 子动作），用于写入白名单。</param>
    /// <param name="approverLabel">授权方称谓，用于提示文案。</param>
    /// <param name="server">玩家来源服务器；控制台来源为 null。</param>
    /// <param name="player">玩家名；控制台来源为 null。</param>
    /// <returns>授权裁决结果。</returns>
    private async Task<AgentPermissionResult> ResolveAnswerAsync(
        string answer,
        string command,
        string scope,
        string approverLabel,
        IServer? server,
        string? player)
    {
        switch (answer)
        {
            case "a":
            case "always":
                // “总是允许”只对非强制询问命令生效；记录的是“命令 + 子动作”范围
                if (TryPersistAllow(scope, command))
                {
                    _output.Write("AI/Permission", LogLevel.Success,
                        $"{approverLabel}已批准并加入允许列表，后续该范围不再询问: {scope}");
                    await NotifyAsync(server, player, $"已加入允许列表: {scope}", LogLevel.Success);
                    return new AgentPermissionResult(AgentPermissionDecision.Allowed,
                        $"{approverLabel}已批准 '{scope}'，并加入允许列表");
                }

                _output.Write("AI/Permission", LogLevel.Warning,
                    $"{AgentPermissions.GetCommandName(command)} 必须每次确认，无法加入允许列表");
                return new AgentPermissionResult(AgentPermissionDecision.Allowed,
                    $"{approverLabel}已批准本次执行（该命令每次都需要确认）");

            case "y":
            case "yes":
                await NotifyAsync(server, player, "已批准，正在执行...", LogLevel.Success);
                return new AgentPermissionResult(AgentPermissionDecision.Allowed, $"{approverLabel}已批准该命令");

            case "n":
            case "no":
                await NotifyAsync(server, player, "已拒绝，命令未执行", LogLevel.Warning);
                return new AgentPermissionResult(AgentPermissionDecision.Denied, $"{approverLabel}拒绝了该命令");

            case "":
                await NotifyAsync(server, player, "等待确认超时，命令未执行", LogLevel.Warning);
                return new AgentPermissionResult(AgentPermissionDecision.Unavailable,
                    $"等待{approverLabel}确认超时或输入被取消，命令未执行");

            default:
                // 无法识别的作答按拒绝处理，避免误放行
                return new AgentPermissionResult(AgentPermissionDecision.Denied,
                    $"无法识别的作答，按拒绝处理（命令未执行）: {command}");
        }
    }

    // ---------- 辅助 ----------

    /// <summary>
    /// 构造待作答提问的字典键。
    /// </summary>
    /// <param name="serverName">服务器名。</param>
    /// <param name="player">玩家名。</param>
    /// <returns>忽略大小写的复合键。</returns>
    private static string PromptKey(string serverName, string player)
        => $"{serverName}\u0000{player}".ToLowerInvariant();

    /// <summary>
    /// 判断玩家是否为指定服务器的管理员（读取该服务器目录下的 ops.json）。
    /// </summary>
    /// <param name="server">目标服务器。</param>
    /// <param name="player">玩家名。</param>
    /// <returns>是管理员返回 true。</returns>
    private bool IsOperator(IServer server, string player)
    {
        try
        {
            var opsFile = Path.Combine(server.Path, "ops.json");
            if (!File.Exists(opsFile)) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(opsFile));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                if (!elem.TryGetProperty("name", out var nameElem)) continue;

                var name = nameElem.GetString();
                if (!string.IsNullOrEmpty(name) && name.Equals(player, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            // 名单不可读时按“非管理员”处理，失败关闭
            _output.Write("AI/Permission", LogLevel.Error,
                $"读取服务器 '{server.Name}' 的 ops.json 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 向游戏内玩家发送一条 tellraw 消息。
    /// </summary>
    /// <param name="server">目标服务器。</param>
    /// <param name="text">消息文本。</param>
    private async Task SendTellrawAsync(IServer server, string text)
    {
        try
        {
            var json = JsonSerializer.Serialize(text);
            await server.SendCommandAsync($"tellraw @a {{\"text\":{json}}}");
        }
        catch (Exception ex)
        {
            _output.Write("AI/Permission", LogLevel.Error, $"发送游戏内提示失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 向游戏内玩家发送状态提示（仅玩家来源时生效）。
    /// </summary>
    /// <param name="server">来源服务器；为 null 时忽略。</param>
    /// <param name="player">玩家名；为 null 时忽略。</param>
    /// <param name="text">提示文本。</param>
    /// <param name="level">日志级别，用于控制台记录。</param>
    private async Task NotifyAsync(IServer? server, string? player, string text, LogLevel level)
    {
        if (server == null || string.IsNullOrEmpty(player)) return;

        try
        {
            var json = JsonSerializer.Serialize($"§e[AI] {text}");
            await server.SendCommandAsync($"tellraw {player} {{\"text\":{json}}}");
        }
        catch (Exception ex)
        {
            _output.Write("AI/Permission", LogLevel.Error, $"发送游戏内提示失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 将“命令 + 子动作”范围写入持久化白名单；强制询问命令会被拒绝。
    /// </summary>
    /// <param name="scope">要持久化放行的范围，如 <c>$server cfg</c>。</param>
    /// <param name="command">触发本次授权的完整命令，用于强制询问判定。</param>
    /// <returns>成功写入时返回 true。</returns>
    private bool TryPersistAllow(string scope, string command)
    {
        if (string.IsNullOrWhiteSpace(scope)) return false;

        var pattern = scope.Trim();
        var config = _configStore.LoadConfig();
        config.AgentPermissions ??= new AgentPermissions();

        if (!config.AgentPermissions.TryAddToAllowList(pattern, command))
            return false;

        try
        {
            _configStore.SaveConfig(config);
        }
        catch (Exception ex)
        {
            // 持久化失败时回滚内存中的新增项，避免出现“看起来已允许但重启即失效”的假象
            config.AgentPermissions.AllowList.RemoveAll(
                p => string.Equals(p.Trim(), pattern, StringComparison.OrdinalIgnoreCase));
            _output.Write("AI/Permission", LogLevel.Error, $"写入允许列表失败: {ex.Message}");
            return false;
        }

        // 回读确认：只有磁盘上真的能看到这条记录才对外声明“已持久化”
        var confirmed = _configStore.LoadConfig().AgentPermissions.IsAlwaysAllowed(command, pattern);
        if (confirmed)
        {
            _output.Write("AI/Permission", LogLevel.Success,
                $"已写入允许列表并保存到配置文件。按“命令 + 子动作”匹配（忽略大小写、不比较参数），已记录: \"{pattern}\"");
        }
        else
        {
            _output.Write("AI/Permission", LogLevel.Warning,
                $"允许列表写入后回读未命中，请检查配置文件: \"{pattern}\"");
        }

        return confirmed;
    }

    /// <summary>
    /// 注册一条会话内一次性放行记录。
    /// </summary>
    /// <param name="command">已获批准的命令文本；普通命令按“命令 + 子动作”范围记录，
    /// 强制逐次询问的命令按完整命令记录。</param>
    public void GrantSessionPass(string command)
    {
        var key = ResolveSessionKey(command);
        if (key.Length == 0) return;

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
        var key = ResolveSessionKey(command);
        if (key.Length == 0) return false;

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

    /// <summary>
    /// 计算会话放行记录的键。
    /// 普通命令按“命令 + 子动作”范围记录（同一范围的不同参数共享一次授权，与白名单粒度一致）；
    /// 强制逐次询问的命令（<c>$app exec</c> / <c>$server del</c>）**必须按完整命令匹配**，
    /// 否则“批准一次 $app exec echo hi”会把“$app exec 任意命令”一起放行，
    /// 与“每次执行都必须单独确认”的承诺相矛盾。
    /// </summary>
    /// <param name="command">完整命令文本。</param>
    /// <returns>放行记录的键；无法解析时为空字符串。</returns>
    private string ResolveSessionKey(string command)
    {
        var scope = ResolveScope(command);
        if (scope.Length == 0) return string.Empty;

        return GetPermissions().IsAlwaysAsk(command, scope)
            ? AgentPermissions.Normalize(command)
            : scope;
    }

    /// <summary>
    /// 构造供命令自行校验参数用的上下文。
    /// 必须带上服务器注册表：<c>ServerCommand.TryValidateArgs</c> 需要它才能识别
    /// <c>$server &lt;服务器名&gt; file …</c> 这种“以服务器名开头”的写法；
    /// 不传上下文会把该写法误判为“未知动作”，导致代理永远无法获得授权。
    /// </summary>
    /// <param name="command">完整命令文本。</param>
    /// <returns>命令参数上下文。</returns>
    private CommandArgs BuildValidationContext(string command)
        => new(
            CommandInvocationValidator.ExtractRawArgs(command),
            _serverRegistry,
            null,
            _configStore)
        {
            Parser = _commandParser
        };

    /// <summary>
    /// 解析命令的授权范围：由命令自己的钩子给出“命令 + 子动作”，否则退化为命令名。
    /// </summary>
    /// <param name="command">完整命令文本。</param>
    /// <returns>授权范围。</returns>
    private string ResolveScope(string command)
        => CommandInvocationValidator.ResolvePermissionScope(
            _commandParser,
            command,
            BuildValidationContext(command));
}
