using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $server 命令：服务器管理的唯一入口，按动作分发到各处理器。
/// 动作：cfg / arg / ck / buf / ls / bp / query / status / stop / run / send。
/// </summary>
public class ServerCommand : ICommand, IArgValidatingCommand
{
    /// <summary>命令名称：$server。</summary>
    public string Name => "$server";

    /// <summary>命令描述。</summary>
    public string Description =>
        "服务器管理。用法: $server <动作> ...（add/rm/del/lk/ulk/hl/cfg/arg/ck/buf/ls/bp/query/status/stop/run/send），详见 $help $server";

    /// <summary>各动作对应的子动作（无子动作的为 null）。</summary>
    private static readonly Dictionary<string, string[]?> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["add"] = null,
        ["rm"] = null,
        ["remove"] = null,
        ["del"] = null,
        ["delete"] = null,
        ["lk"] = null,
        ["lock"] = null,
        ["ulk"] = null,
        ["unlock"] = null,
        ["hd"] = null,
        ["hide"] = null,
        ["uhd"] = null,
        ["show"] = null,
        ["hl"] = null,
        ["file"] = ServerFileHandler.Flags,
        ["cfg"] = new[] { "get", "getall", "set", "rm", "remove" },
        ["arg"] = new[] { "get", "set", "rm", "remove" },
        ["ck"] = new[] { "wl", "whitelist", "op", "bp", "banplayer", "bip", "banip" },
        ["buf"] = new[] { "read", "update" },
        ["bp"] = null,
        ["ls"] = null,
        ["query"] = null,
        ["status"] = null,
        ["stop"] = null,
        ["run"] = null,
        ["send"] = null
    };

    /// <summary>
    /// 子动作别名到规范写法的映射，用于授权范围归一化
    /// （避免 <c>$server ck whitelist</c> 绕过针对 <c>$server ck wl</c> 的强制询问条目）。
    /// </summary>
    private static readonly Dictionary<string, string> SubActionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["remove"] = "rm",
        ["whitelist"] = "wl",
        ["banplayer"] = "bp",
        ["banip"] = "bip"
    };

    /// <summary>受锁定影响（锁定期间被拒绝）的动作。</summary>
    private static readonly string[] LockAffectedActions = { "run", "stop", "send", "rm", "remove", "del", "delete" };

    /// <summary>用于提示与帮助的动作清单（合并别名，保持可读）。</summary>
    private const string ActionList =
        "add、rm（移除引用）、del（连目录删除）、lk/ulk（锁定/解锁）、hd/uhd（隐藏/显示输出）、hl（高亮）、" +
        "file（服务器目录内文件操作，见 $help $server）、cfg、arg、ck、buf、bp、ls、query、status、stop、run、send";

    /// <summary>
    /// 参数校验钩子：检查动作是否有效，以及需要子动作的动作是否给了合法子动作。
    /// </summary>
    /// <param name="rawArgs">$server 之后的参数文本。</param>
    /// <param name="args">命令参数上下文（本命令校验不需要，可为 null）。</param>
    /// <param name="error">不合法时的说明文本。</param>
    /// <returns>参数合法时返回 true。</returns>
    public bool TryValidateArgs(string rawArgs, CommandArgs? args, out string error)
    {
        error = string.Empty;

        var parts = rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = $"$server 缺少动作。可用动作: {ActionList}";
            return false;
        }

        var action = parts[0].ToLowerInvariant();
        if (!Actions.TryGetValue(action, out var subActions))
        {
            // 服务器名开头的文件操作：$server <服务器名> file -r <路径> ...
            var isServerName = args != null &&
                               args.ServerRegistry.All.Keys.Any(k => k.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
            if (isServerName)
            {
                if (parts.Length < 2)
                {
                    error = $"用法: $server {parts[0]} file <选项> <参数...>，选项: {string.Join("、", ServerFileHandler.Flags)}";
                    return false;
                }

                if (!parts[1].Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"$server {parts[0]} 只支持 file 子命令，" +
                            $"例如 $server {parts[0]} file -r banned-ips.json";
                    return false;
                }

                return ServerFileHandler.Validate(parts, 2, out error);
            }

            error = $"$server 未知动作 '{parts[0]}'，可用: {ActionList}。" +
                    "可先用 $help $server 查看用法。";
            return false;
        }

        // 有子动作的动作：必须给出合法子动作
        if (subActions != null)
        {
            if (parts.Length < 2)
            {
                error = $"$server {action} 缺少子动作，可用: {string.Join("、", subActions)}。";
                return false;
            }

            if (!subActions.Contains(parts[1], StringComparer.OrdinalIgnoreCase))
            {
                error = $"$server {action} 未知子动作 '{parts[1]}'，可用: {string.Join("、", subActions)}。" +
                        $"可先用 $help $server 查看用法。";
                return false;
            }

            // file 还需要校验各选项的参数数量
            if (action == "file") return ServerFileHandler.Validate(parts, 1, out error);

            return true;
        }

        // 无子动作的动作：按各自语义校验目标
        return action switch
        {
            "add" => RequireArgs(parts, 3, "$server add <名称> <路径>", out error),
            "rm" or "remove" => RequireArgs(parts, 2, "$server rm <名称>", out error),
            "del" or "delete" => RequireArgs(parts, 2, "$server del <名称> confirm", out error),
            "lk" or "lock" => RequireArgs(parts, 2, "$server lk <名称>", out error),
            "ulk" or "unlock" => RequireArgs(parts, 2, "$server ulk <名称>", out error),
            "hd" or "hide" => RequireArgs(parts, 2, "$server hd <名称>", out error),
            "uhd" or "show" => RequireArgs(parts, 2, "$server uhd <名称>", out error),
            "hl" => true,
            "bp" or "run" or "status" or "query" => true,
            "ls" => true,
            "stop" or "send" => TryResolveTarget(parts, 1, args, out _, out error),
            _ => true
        };
    }

    /// <summary>
    /// 校验 token 数量是否达到要求，不足时给出用法提示。
    /// </summary>
    /// <param name="parts">参数 token 列表。</param>
    /// <param name="required">需要的 token 数（含动作本身）。</param>
    /// <param name="usage">用法提示。</param>
    /// <param name="error">不合法时的说明文本。</param>
    /// <returns>数量足够时返回 true。</returns>
    private static bool RequireArgs(string[] parts, int required, string usage, out string error)
    {
        if (parts.Length >= required)
        {
            error = string.Empty;
            return true;
        }

        error = $"用法: {usage}";
        return false;
    }

    /// <summary>
    /// 取出授权范围：命令 + 动作 + （存在时的）子动作，
    /// 例如 <c>$server cfg</c>、<c>$server ck</c>、<c>$server buf</c>、<c>$server cfg get</c>。
    /// 参数不足以判定时逐级回退，最终回退到命令名。
    /// </summary>
    /// <param name="rawArgs">$server 之后的参数文本。</param>
    /// <param name="args">命令参数上下文（本命令判定不需要，可为 null）。</param>
    /// <returns>授权范围。</returns>
    public string GetPermissionScope(string rawArgs, CommandArgs? args)
    {
        var parts = rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Name;

        var action = parts[0].ToLowerInvariant();
        if (!Actions.TryGetValue(action, out var subActions))
        {
            // 服务器名开头的文件操作 → 授权范围到文件选项，如 $server file write
            if (parts.Length >= 3 && parts[1].Equals("file", StringComparison.OrdinalIgnoreCase) &&
                ServerFileHandler.ScopeName(parts[2]) is { } name)
            {
                return $"{Name} file {name}";
            }

            return Name;
        }

        var scope = $"{Name} {action}";
        if (subActions == null)
        {
            // 顶层别名归一化：remove→rm、delete→del，
            // 否则 $server delete 会绕开针对 $server del 的强制询问条目
            var canonical = action switch
            {
                "remove" => "rm",
                "delete" => "del",
                "lock" => "lk",
                "unlock" => "ulk",
                "hide" => "hd",
                "show" => "uhd",
                _ => action
            };
            return $"{Name} {canonical}";
        }

        if (parts.Length < 2) return scope;
        if (!subActions.Contains(parts[1], StringComparer.OrdinalIgnoreCase)) return scope;

        // file 的选项映射为可读范围名（-r → read）
        if (action == "file" && ServerFileHandler.ScopeName(parts[1]) is { } flagName)
            return $"{scope} {flagName}";

        // 其余子动作的别名同样归一化，使授权范围只与动作语义相关，与拼写无关
        var subAction = parts[1].ToLowerInvariant();
        var canonicalSub = SubActionAliases.TryGetValue(subAction, out var alias) ? alias : subAction;
        return $"{scope} {canonicalSub}";
    }

    /// <summary>
    /// 解析 <c>stop</c>/<c>send</c> 的目标参数：首个 token 必须是 <c>all</c> 或已配置的服务器名。
    /// 与处理器保持一致（处理器同样不支持省略服务器名回退到高亮），避免“校验通过但执行失败”。
    /// </summary>
    /// <param name="parts">参数 token 列表。</param>
    /// <param name="index">目标所在下标。</param>
    /// <param name="args">命令参数上下文，可为 null。</param>
    /// <param name="serverName">解析出的服务器名或 "all"。</param>
    /// <param name="error">不合法时的说明文本（一定非空）。</param>
    /// <returns>目标合法时返回 true。</returns>
    private static bool TryResolveTarget(string[] parts, int index, CommandArgs? args, out string serverName, out string error)
    {
        serverName = string.Empty;
        error = string.Empty;

        if (parts.Length <= index)
        {
            var action = parts.Length > 0 ? parts[0].ToLowerInvariant() : "stop";
            error = action == "send"
                ? "用法: $server send <服务器名|all> <命令>"
                : $"用法: $server {action} <服务器名|all> [-f]";
            return false;
        }

        var target = parts[index];
        if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            serverName = "all";
            return true;
        }

        // 没有上下文（拿不到注册表）时不判断，避免误拦
        var registry = args?.ServerRegistry;
        if (registry == null)
        {
            serverName = target;
            return true;
        }

        if (registry.GetServer(target) != null)
        {
            serverName = target;
            return true;
        }

        error = $"未找到服务器 '{target}'（可用 $server ls 查看已配置的服务器）";
        return false;
    }

    /// <summary>
    /// 执行 $server 命令：解析动作并把剩余参数交给对应处理器。
    /// 简单动作复用已有的功能类；cfg/arg/ck/buf 交给各自独立的处理器。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入、服务器注册表与命令解析器。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        var parts = args.Raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Task.FromResult(Fail(output, Help()));

        var action = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        // 服务器名开头的文件操作：$server <服务器名> file -r <路径> ...
        if (!Actions.ContainsKey(action))
        {
            var canonical = args.ServerRegistry.All.Keys
                .FirstOrDefault(k => k.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
            if (canonical != null)
            {
                var subParts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (subParts.Length == 0 || !subParts[0].Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    // 旧写法（$server <名> read ...）或漏写 file 时给出明确指路
                    var hint = $"服务器名后面只支持 file 子命令，例如 $server {canonical} file -r banned-ips.json" +
                               $"（选项: {string.Join("、", ServerFileHandler.Flags)}）";
                    output?.Write("Command", LogLevel.Error, hint);
                    return Task.FromResult(new CommandResult(0, hint));
                }

                var fileArgs = subParts.Length > 1 ? subParts[1] : string.Empty;
                return Task.FromResult(ServerFileHandler.Handle(canonical, fileArgs, args, output));
            }
        }

        // 锁定闸门：锁定的服务器禁止 run/stop/send/rm/del
        var lockError = CheckLock(action, rest, args);
        if (lockError != null)
        {
            output?.Write("Command", LogLevel.Error, lockError);
            return Task.FromResult(new CommandResult(0, lockError));
        }

        return action switch
        {
            // 登记 / 移除 / 删除 / 锁定 / 隐藏 / 高亮
            "add" => Task.FromResult(ServerLifecycleHandler.Handle("add", rest, args, output)),
            "rm" or "remove" => Task.FromResult(ServerLifecycleHandler.Handle("rm", rest, args, output)),
            "del" or "delete" => Task.FromResult(ServerLifecycleHandler.Handle("del", rest, args, output)),
            "lk" or "lock" => Task.FromResult(ServerLockHandler.Handle("lk", rest, args, output)),
            "ulk" or "unlock" => Task.FromResult(ServerLockHandler.Handle("ulk", rest, args, output)),
            "hd" or "hide" => Task.FromResult(ServerVisibilityHandler.Handle("hd", rest, args, output)),
            "uhd" or "show" => Task.FromResult(ServerVisibilityHandler.Handle("uhd", rest, args, output)),
            "hl" => Task.FromResult(Highlight(rest, args, output)),
            // 文件操作：省略服务器名时用高亮服务器
            "file" => Task.FromResult(ServerFileHandler.HandleForHighlighted(rest, args, output)),
            // 配置 / 启动参数 / 名单 / 缓冲区：各自独立的处理器
            "cfg" => Task.FromResult(ServerConfigHandler.Handle(rest, args, output)),
            "arg" => Task.FromResult(ServerArgsHandler.Handle(rest, args, output)),
            "ck" => Task.FromResult(ServerCheckHandler.Handle(rest, args, output)),
            "buf" => Task.FromResult(ServerBufferHandler.Handle(rest, args, output)),
            "bp" => ServerBackupCommand.Make(rest, args, output),
            // 多服务器：目标可为 all
            "ls" => Task.FromResult(ListServers(args, output)),
            "query" => ServerQueryHandler.RunAsync(rest, args, output),
            "status" => Task.FromResult(ServerStatusHandler.Run(rest, args, output)),
            "stop" => ServerStopHandler.RunAsync(rest, args, output),
            // 单服务器
            "run" => ServerRunHandler.RunAsync(rest, args, output),
            "send" => ServerSendHandler.RunAsync(rest, args, output),
            _ => Task.FromResult(Fail(output, Help()))
        };
    }

    /// <summary>
    /// 处理 ls 动作：列出所有已配置的服务器及其状态与锁定/隐藏标记。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult ListServers(CommandArgs args, IOutputWriter? output)
    {
        var registry = args.ServerRegistry;
        var config = args.ConfigStore.LoadConfig();
        var sb = new System.Text.StringBuilder();

        if (registry.All.Count == 0)
        {
            sb.AppendLine("没有配置任何服务器");
        }
        else
        {
            var highlighted = registry.HighlightedServerName;
            sb.AppendLine($"已配置的服务器 ({registry.All.Count} 台):");
            foreach (var kv in registry.All)
            {
                var marks = new List<string>();
                if (string.Equals(kv.Key, highlighted, StringComparison.OrdinalIgnoreCase)) marks.Add("高亮");
                if (ServerLock.IsLocked(config, kv.Key)) marks.Add("已锁定");
                if (config.HiddenServers.Any(n => string.Equals(n, kv.Key, StringComparison.OrdinalIgnoreCase))) marks.Add("输出已隐藏");
                var suffix = marks.Count == 0 ? string.Empty : $"  [{string.Join(" / ", marks)}]";
                sb.AppendLine($"  {kv.Key,-16} {kv.Value.Status}{suffix}");
            }
        }

        output?.Write("Command", LogLevel.Success, sb.ToString());
        return new CommandResult(1, sb.ToString());
    }

    /// <summary>
    /// 处理 hl 动作：不带参数显示当前高亮，带参数切换高亮（原 $hl 命令）。
    /// </summary>
    /// <param name="rest">hl 之后的参数文本（可选的服务器名）。</param>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult Highlight(string rest, CommandArgs args, IOutputWriter? output)
    {
        var registry = args.ServerRegistry;

        if (string.IsNullOrWhiteSpace(rest))
        {
            var current = registry.HighlightedServerName;
            var msg = current == null ? "当前没有高亮服务器" : $"当前高亮服务器: {current}";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }

        var name = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (registry.SwitchHighlight(name))
        {
            var msg = $"已切换到服务器 '{name}'";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }

        var failMsg = $"未找到服务器 '{name}'";
        output?.Write("Command", LogLevel.Error, failMsg);
        return new CommandResult(0, failMsg);
    }

    /// <summary>
    /// 锁定闸门：动作针对的服务器被锁定时返回拒绝说明。
    /// <c>all</c> 目标不在这里拦截，由各处理器跳过锁定服务器并提示；
    /// 缺名/名字非法的情形交给处理器给出更准确的报错。
    /// </summary>
    /// <param name="action">已小写的动作名。</param>
    /// <param name="rest">动作之后的参数文本。</param>
    /// <param name="args">命令参数，包含注册表与配置存储。</param>
    /// <returns>拒绝说明；允许执行时返回 null。</returns>
    private static string? CheckLock(string action, string rest, CommandArgs args)
    {
        // 锁定只禁止 run/stop/send/rm/del；其余操作（配置、查看、备份、文件等）不受影响
        if (!LockAffectedActions.Contains(action, StringComparer.OrdinalIgnoreCase)) return null;

        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var token = parts.Length > 0 ? parts[0] : null;

        // all 目标由处理器跳过锁定服务器；名字不是服务器时交给处理器报错
        if (token == null || token.Equals("all", StringComparison.OrdinalIgnoreCase)) return null;

        // 以配置文件中的 ServerPaths 判定“是不是服务器名”，而不是当前注册表：
        // 配置被外部改动且尚未 $app reload 时，注册表里可能还没有这台服务器，
        // 但它在配置中已被锁定——此时必须照旧拦截，否则 $server del 会删掉被锁定的服务器目录。
        var config = args.ConfigStore.LoadConfig();
        var name = config.ServerPaths.Keys.FirstOrDefault(k => k.Equals(token, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;

        return ServerLock.IsLocked(config, name) ? ServerLock.LockedMessage(name) : null;
    }

    /// <summary>
    /// 输出错误信息并构造失败结果。
    /// </summary>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <param name="message">错误信息。</param>
    /// <returns>退出码为 0 的失败结果。</returns>
    private static CommandResult Fail(IOutputWriter? output, string message)
    {
        output?.Write("Command", LogLevel.Error, message);
        return new CommandResult(0, message);
    }

    /// <summary>
    /// 生成 $server 用法文本。
    /// </summary>
    /// <returns>用法说明。</returns>
    private static string Help() =>
        "用法: $server <动作> ...\n" +
        "  add <名称> <路径>                           登记新服务器（路径须已存在，支持 %VAR% 环境变量）\n" +
        "  rm <名称>                                   只移除配置引用（保留服务器目录）\n" +
        "  del <名称> confirm                          移除引用并删除整个服务器目录（不可恢复）\n" +
        "  lk <名称> / ulk <名称>                      锁定 / 解锁（锁定后 run/stop/send/rm/del 一律拒绝）\n" +
        "  hd <名称> / uhd <名称>                      隐藏 / 显示该服务器在控制台的输出（缓冲区不受影响）\n" +
        "  hl [名称]                                   查看 / 切换高亮服务器\n" +
        "  file <选项> <参数...>                        服务器目录内文件操作，省略服务器名时用高亮服务器\n" +
        "        -r <路径> / -w <路径> <内容> / -a <路径> <内容>\n" +
        "        -rm <路径> / -cp <源> <目标> / -mv <源> <目标> / -ls [路径]\n" +
        "  cfg get|getall|set|rm <服务器名> [键] [值]   服务器配置（server.properties）\n" +
        "  arg get|set|rm <服务器名> <参数> [值...]     启动参数（javaPath/jvmArgs/jarArgs/appendArgs）\n" +
        "  ck wl|op|bp|bip <服务器名> [名称]            名单检查（白名单/OP/封禁玩家/封禁IP）\n" +
        "  buf read|update <服务器名>                   读取 / 读取并清空输出缓冲区\n" +
        "  ls                                          列出所有服务器及状态\n" +
        "  bp <服务器名> [备注]                         备份世界目录\n" +
        "  query <服务器名|all>                         查询 Query 信息\n" +
        "  status [服务器名|all]                        查看进程状态\n" +
        "  stop <服务器名|all> [-f]                     停止服务器（-f 跳过优雅关闭，直接强制终止）\n" +
        "  run <服务器名>                               启动服务器\n" +
        "  send <服务器名|all> <命令>                   发送 Minecraft 命令\n" +
        "用 $help $server 查看详细说明。";
}
