using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $ai 命令：AI 相关配置与调用的唯一入口。
/// 子命令：<c>chat</c>/<c>agent</c>（调用）、<c>default</c>（默认实例）、
/// <c>add</c>/<c>rm</c>（增删实例）、<c>cfg</c>（实例字段与 AI 相关配置）、
/// <c>perm</c>（代理命令授权）。覆盖不到的配置再用“第二路径” <c>$app cfg</c>。
/// </summary>
public class AICommand : ICommand, IArgValidatingCommand
{
    /// <summary>
    /// 命令名称 "$ai"。
    /// </summary>
    public string Name => "$ai";

    /// <summary>
    /// 命令用途说明，用于帮助信息展示。
    /// </summary>
    public string Description => "AI命令，用法: $ai chat|agent [配置名] <消息/指令> | $ai default|add|rm|cfg|perm ...";

    /// <summary>各子命令对应的子动作（无子动作的为 null）。</summary>
    private static readonly Dictionary<string, string[]?> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chat"] = null,
        ["agent"] = null,
        ["default"] = null,
        ["add"] = null,
        ["rm"] = null,
        ["remove"] = null,
        ["del"] = null,
        ["delete"] = null,
        ["cfg"] = new[] { "get", "getall", "set", "rm", "remove" },
        ["perm"] = new[] { "get", "add", "rm", "remove", "ask", "noask" }
    };

    /// <summary>
    /// 参数校验钩子：检查子命令及其子动作是否合法、必填参数是否齐全。
    /// </summary>
    /// <param name="rawArgs">$ai 之后的参数文本。</param>
    /// <param name="args">命令参数上下文（本命令校验不需要，可为 null）。</param>
    /// <param name="error">不合法时的说明文本。</param>
    /// <returns>参数合法时返回 true。</returns>
    public bool TryValidateArgs(string rawArgs, CommandArgs? args, out string error)
    {
        error = string.Empty;

        var tokens = rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            error = "$ai 缺少子命令，可用: chat、agent、default、add、rm、cfg、perm";
            return false;
        }

        var sub = tokens[0].ToLowerInvariant();
        if (!Actions.ContainsKey(sub))
        {
            error = $"$ai 未知子命令 '{tokens[0]}'，可用: chat、agent、default、add、rm、cfg、perm。";
            return false;
        }

        // default 允许不带参数（查看当前默认配置）
        if (sub == "default") return true;

        // add 需要 配置名 + Url + Model（ApiKeyEnv 可选）
        if (sub == "add")
        {
            if (tokens.Length < 4)
            {
                error = "用法: $ai add <配置名> <Url> <Model> [ApiKeyEnv]";
                return false;
            }
            return true;
        }

        // rm 需要配置名
        if (sub is "rm" or "remove" or "del" or "delete")
        {
            if (tokens.Length < 2)
            {
                error = "用法: $ai rm <配置名>";
                return false;
            }
            return true;
        }

        // cfg / perm：校验子动作与必填参数
        if (Actions[sub] is { } subActions)
        {
            // perm 允许不带子动作（查看当前 AllowList / AlwaysAskCommands）
            if (tokens.Length < 2 && sub == "perm") return true;

            if (tokens.Length < 2)
            {
                error = $"$ai {sub} 缺少子动作，可用: {string.Join("、", subActions)}。";
                return false;
            }

            var action = tokens[1].ToLowerInvariant();
            if (!subActions.Contains(action))
            {
                error = $"$ai {sub} 未知子动作 '{tokens[1]}'，可用: {string.Join("、", subActions)}。";
                return false;
            }

            if (sub == "cfg" && action == "set" && tokens.Length < 4)
            {
                error = "用法: $ai cfg set <路径> <值>，例如 $ai cfg set ds.MaxIterations 32";
                return false;
            }

            // cfg get / getall 允许省略路径（输出总览）；rm/remove 必须给出路径
            if (sub == "cfg" && (action == "rm" || action == "remove") && tokens.Length < 3)
            {
                error = "用法: $ai cfg rm <路径>，例如 $ai cfg rm ds";
                return false;
            }

            if (sub == "perm" && action != "get" && tokens.Length < 3)
            {
                error = $"用法: $ai perm {action} <授权范围>，例如 $ai perm add \"$server ck op\"";
                return false;
            }

            return true;
        }

        if (tokens.Length < 2 || string.IsNullOrWhiteSpace(tokens[1]))
        {
            error = sub == "chat"
                ? "用法: $ai chat [配置名] <消息>"
                : "用法: $ai agent [配置名] <指令>";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 取出授权范围：命令 + 子命令（+ 子动作），如 <c>$ai agent</c>、<c>$ai cfg set</c>、<c>$ai perm add</c>。
    /// </summary>
    /// <param name="rawArgs">$ai 之后的参数文本。</param>
    /// <param name="args">命令参数上下文（本命令判定不需要，可为 null）。</param>
    /// <returns>授权范围。</returns>
    public string GetPermissionScope(string rawArgs, CommandArgs? args)
    {
        var tokens = rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return Name;

        var sub = tokens[0].ToLowerInvariant();
        var canonical = sub switch
        {
            "rm" or "remove" or "del" or "delete" => "rm",
            _ => sub
        };

        if (!Actions.ContainsKey(sub)) return Name;

        // 有子动作的子命令：范围到子动作一层
        if (Actions[sub] is { } subActions)
        {
            if (tokens.Length < 2) return $"{Name} {canonical}";
            var action = tokens[1].ToLowerInvariant();
            if (!subActions.Contains(action)) return $"{Name} {canonical}";
            var canonicalAction = action == "remove" ? "rm" : action;
            return $"{Name} {canonical} {canonicalAction}";
        }

        return $"{Name} {canonical}";
    }

    /// <summary>
    /// 执行 $ai 命令：根据子命令分发到 AI 对话、AI 代理或默认配置管理逻辑。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入、服务器注册表、AI 服务、配置存储等上下文。</param>
    /// <param name="output">可选的输出写入器，用于向终端输出日志与结果信息。</param>
    /// <returns>命令执行结果，包含退出码与输出文本。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        // 未提供任何参数时，直接输出用法提示
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var msg = "用法: $ai chat|agent [配置名] <消息/指令> | $ai default [模型名]";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // 将原始输入拆分为子命令与剩余内容（最多拆两段）
        var parts = args.Raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts[0].ToLowerInvariant();
        string rest = parts.Length > 1 ? parts[1].Trim() : "";

        // $ai default [模型名]：查看或更改默认 AI 配置（直接写入配置文件）
        if (sub == "default")
        {
            return await HandleDefaultAsync(rest, args, output);
        }

        // $ai add|rm：AI 实例的新增与删除（即时生效，无需重启）
        if (sub is "add" or "rm" or "remove" or "del" or "delete")
        {
            return await HandleInstanceAsync(sub, rest, args, output);
        }

        // $ai cfg|perm：AI 相关配置的统一读写入口
        if (sub == "cfg") return AIConfigHandler.HandleConfig(rest, args, output);
        if (sub == "perm") return AIConfigHandler.HandlePerm(rest, args, output);

        // 仅支持 chat 与 agent 两个子命令，其余视为未知子命令
        if (sub != "chat" && sub != "agent")
        {
            var msg = $"未知子命令 '{parts[0]}'，可用: chat、agent、default、add、rm、cfg、perm";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // chat 与 agent 均要求提供消息/指令内容
        if (string.IsNullOrEmpty(rest))
        {
            var msg = sub == "chat"
                ? "用法: $ai chat [配置名] <消息>"
                : "用法: $ai agent [配置名] <指令>";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // 解析可选的配置名与消息：首个词只有在确实是已登记的 AI 配置名时才当作配置名，
        // 否则整段文本都是消息（否则 "$ai chat 你好 世界" 会把 "你好" 当成配置名而报错）
        var restParts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string configName = "default";
        string content = rest;
        var configured = args.ConfigStore.LoadConfig().AIConfigs;
        if (restParts.Length == 2 &&
            !restParts[0].Equals("default", StringComparison.OrdinalIgnoreCase) &&
            configured.ContainsKey(restParts[0]))
        {
            configName = restParts[0];
            content = restParts[1];
        }

        // 当 configName 为 "default" 时，改用 AppConfig 中配置的默认 AI 配置名
        if (configName.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            var defaultName = args.ConfigStore.LoadConfig().DefaultAIConfig;
            if (!string.IsNullOrWhiteSpace(defaultName))
                configName = defaultName;
            else
            {
                var msg = "未设置默认AI配置";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }
        }

        // AI 服务可能未注入（仅做参数校验时允许为空），这里显式判空而不是解引用
        var agent = args.AgentService;
        if (agent == null)
        {
            var msg = "AI 服务不可用（未初始化）";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // 根据子命令调用对应的 AI 服务（chat 对话 / agent 代理执行），异常统一捕获并返回失败结果
        try
        {
            var (model, response) = sub == "chat"
                ? await agent.ChatAsync(configName, content)
                : await agent.AgentAsync(configName, content);
            output?.Write($"AI-{sub}/{model}", LogLevel.Success, response);
            return new CommandResult(1, response);
        }
        catch (Exception ex)
        {
            var msg = $"AI调用失败: {ex.Message}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }
    }

    /// <summary>
    /// 处理 $ai add / $ai rm：新增或删除 AI 实例，保存后立即热更新到运行中的 AI 服务。
    /// </summary>
    /// <param name="sub">子命令（add 或 rm 及其别名）。</param>
    /// <param name="rest">子命令之后的参数文本。</param>
    /// <param name="args">命令参数，包含配置存储与 AI 服务。</param>
    /// <param name="output">可选的输出写入器。</param>
    /// <returns>命令执行结果。</returns>
    private static Task<CommandResult> HandleInstanceAsync(string sub, string rest, CommandArgs args, IOutputWriter? output)
        => Task.FromResult(sub == "add"
            ? AddInstance(rest, args, output)
            : RemoveInstance(rest, args, output));

    /// <summary>
    /// 删除一个 AI 实例；若它正是默认配置则一并清空，并提示重新指定。
    /// </summary>
    /// <param name="rest">rm 之后的参数文本。</param>
    /// <param name="args">命令参数，包含配置存储与 AI 服务。</param>
    /// <param name="output">可选的输出写入器。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult RemoveInstance(string rest, CommandArgs args, IOutputWriter? output)
    {
        var name = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name))
            return Fail(output, "用法: $ai rm <配置名>");

        var config = args.ConfigStore.LoadConfig();
        if (!config.AIConfigs.Remove(name))
        {
            var available = config.AIConfigs.Count == 0 ? "（无）" : string.Join(", ", config.AIConfigs.Keys);
            return Fail(output, $"AI 配置 '{name}' 不存在。已配置: {available}");
        }

        var notes = new List<string>();
        if (string.Equals(config.DefaultAIConfig, name, StringComparison.Ordinal))
        {
            config.DefaultAIConfig = string.Empty;
            notes.Add("它同时是当前默认 AI 配置，已一并清空；请用 $ai default <配置名> 重新指定。");
        }
        if (config.AIConfigs.Count == 0)
            notes.Add("当前已没有任何 AI 配置，$ai 相关功能将不可用。");

        args.ConfigStore.SaveConfig(config);
        args.AgentService?.ReloadConfig(config);

        var msg = $"已删除 AI 配置 '{name}'（当前共 {config.AIConfigs.Count} 个实例）。";
        foreach (var note in notes) msg += "\n" + note;
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>
    /// 新增一个 AI 实例：校验配置名、Url 与模型名，写入配置并热更新。
    /// 未指定 ApiKeyEnv 时给出补密钥的提示；尚无默认配置时自动把它设为默认。
    /// </summary>
    /// <param name="rest">add 之后的参数文本。</param>
    /// <param name="args">命令参数，包含配置存储与 AI 服务。</param>
    /// <param name="output">可选的输出写入器。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult AddInstance(string rest, CommandArgs args, IOutputWriter? output)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return Fail(output, "用法: $ai add <配置名> <Url> <Model> [ApiKeyEnv]\n" +
                                 "  例如: $ai add ds https://api.deepseek.com/v1 deepseek-chat DEEPSEEK_API_KEY\n" +
                                 "  不写 ApiKeyEnv 时，稍后用 $ai cfg set <配置名>.ApiKey <密钥> 填写。");

        var name = parts[0];
        if (!IsValidConfigName(name, out var nameError)) return Fail(output, nameError);

        var url = parts[1];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Fail(output, $"Url 无效: {url}（需为 http/https 开头的绝对地址，例如 https://api.openai.com/v1）");
        }

        var model = parts[2];
        var apiKeyEnv = parts.Length > 3 ? parts[3] : null;

        var config = args.ConfigStore.LoadConfig();
        if (config.AIConfigs.ContainsKey(name))
            return Fail(output, $"AI 配置 '{name}' 已存在。如需删除请先执行 $ai rm {name}");

        config.AIConfigs[name] = new AIConfig
        {
            Url = url,
            Model = model,
            UseApiKeyEnv = !string.IsNullOrWhiteSpace(apiKeyEnv),
            ApiKeyEnv = apiKeyEnv
        };

        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(apiKeyEnv))
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(apiKeyEnv)))
                notes.Add($"注意: 环境变量 '{apiKeyEnv}' 当前为空，请先设置密钥后再调用，否则会报“ApiKey 未设置”。");
        }
        else
        {
            notes.Add($"尚未设置密钥: 用 $ai cfg set {name}.ApiKey <密钥> 填写（明文存盘），" +
                      $"或改用 $ai cfg set {name}.ApiKeyEnv <环境变量名> 并把 UseApiKeyEnv 设为 true。");
        }

        if (string.IsNullOrWhiteSpace(config.DefaultAIConfig))
        {
            config.DefaultAIConfig = name;
            notes.Add("当前没有默认 AI 配置，已把它设为默认（$ai 未指定配置名时使用）。");
        }

        args.ConfigStore.SaveConfig(config);
        args.AgentService?.ReloadConfig(config);

        var msg = $"已添加 AI 配置 '{name}'：Url={url}，Model={model}，" +
                  (string.IsNullOrWhiteSpace(apiKeyEnv) ? "密钥形式=直接填写" : $"密钥环境变量={apiKeyEnv}") +
                  $"（当前共 {config.AIConfigs.Count} 个实例，已即时生效）。";
        foreach (var note in notes) msg += "\n" + note;

        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>
    /// 校验 AI 配置名：非空、不含空白与点号（点号是配置路径分隔符），且不是保留子命令名。
    /// </summary>
    /// <param name="name">配置名。</param>
    /// <param name="error">不合法时的说明文本。</param>
    /// <returns>合法时返回 true。</returns>
    private static bool IsValidConfigName(string name, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "AI 配置名不能为空";
            return false;
        }

        if (name.Any(char.IsWhiteSpace) || name.Contains('.'))
        {
            error = $"AI 配置名 '{name}' 不能包含空白或点号（点号是配置路径分隔符）";
            return false;
        }

        string[] reserved = { "chat", "agent", "default", "add", "rm", "remove", "del", "delete" };
        if (reserved.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            error = $"'{name}' 是保留子命令名，不能作为 AI 配置名（配置名 \"default\" 会被当作“使用默认配置”处理）";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 处理 $ai default 子命令：无参数时显示当前默认 AI 配置，带参数时校验并设置默认 AI 配置。
    /// </summary>
    /// <param name="rest">default 子命令后的剩余内容，为空表示仅查看当前默认配置。</param>
    /// <param name="args">命令参数，用于读取与保存配置。</param>
    /// <param name="output">可选的输出写入器。</param>
    /// <returns>命令执行结果。</returns>
    private async Task<CommandResult> HandleDefaultAsync(string rest, CommandArgs args, IOutputWriter? output)
    {
        var config = args.ConfigStore.LoadConfig();

        // $ai default：获取当前默认 AI 配置
        if (string.IsNullOrWhiteSpace(rest))
        {
            var current = string.IsNullOrWhiteSpace(config.DefaultAIConfig)
                ? "(未设置)"
                : config.DefaultAIConfig;
            var msg = $"当前默认AI配置: {current}";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }

        // $ai default <模型名>：校验配置名是否存在并写入
        if (!config.AIConfigs.ContainsKey(rest))
        {
            var available = config.AIConfigs.Keys.Count == 0
                ? "（无已配置的 AI 配置）"
                : string.Join(", ", config.AIConfigs.Keys);
            var msg = $"AI配置 '{rest}' 不存在，可用: {available}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // 校验通过后将指定配置写入默认项并持久化到配置文件
        config.DefaultAIConfig = rest;
        args.ConfigStore.SaveConfig(config);
        var successMsg = $"默认AI配置已设置为: {rest}";
        output?.Write("Command", LogLevel.Success, successMsg);
        return new CommandResult(1, successMsg);
    }

    /// <summary>
    /// 输出错误信息并构造失败结果。
    /// </summary>
    /// <param name="output">可选的输出写入器。</param>
    /// <param name="message">错误信息。</param>
    /// <returns>退出码为 0 的失败结果。</returns>
    private static CommandResult Fail(IOutputWriter? output, string message)
    {
        output?.Write("Command", LogLevel.Error, message);
        return new CommandResult(0, message);
    }
}
