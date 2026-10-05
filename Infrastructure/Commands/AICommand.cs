using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $ai 命令实现，提供 AI 对话（chat）、AI 代理执行（agent）以及默认 AI 配置管理（default）三个子命令。
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
    public string Description => "AI命令，用法: $ai chat|agent [配置名] <消息/指令> | $ai default [配置名]";

    /// <summary>
    /// 参数校验钩子：检查子命令是否为 chat / agent / default，且内容非空。
    /// </summary>
    /// <param name="rawArgs">$ai 之后的参数文本。</param>
    /// <param name="args">命令参数上下文（本命令校验不需要，可为 null）。</param>
    /// <param name="error">不合法时的说明文本。</param>
    /// <returns>参数合法时返回 true。</returns>
    public bool TryValidateArgs(string rawArgs, CommandArgs? args, out string error)
    {
        error = string.Empty;

        var parts = rawArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "$ai 缺少子命令，可用: chat、agent、default";
            return false;
        }

        var sub = parts[0].ToLowerInvariant();
        if (sub is not ("chat" or "agent" or "default"))
        {
            error = $"$ai 未知子命令 '{parts[0]}'，可用: chat、agent、default。";
            return false;
        }

        // default 允许不带参数（查看当前默认配置）
        if (sub == "default") return true;

        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            error = sub == "chat"
                ? "用法: $ai chat [配置名] <消息>"
                : "用法: $ai agent [配置名] <指令>";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 取出授权范围：命令 + 子命令（如 <c>$ai agent</c>）。
    /// </summary>
    /// <param name="rawArgs">$ai 之后的参数文本。</param>
    /// <param name="args">命令参数上下文（本命令判定不需要，可为 null）。</param>
    /// <returns>授权范围。</returns>
    public string GetPermissionScope(string rawArgs, CommandArgs? args)
    {
        var parts = rawArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Name;

        var sub = parts[0].ToLowerInvariant();
        return sub is "chat" or "agent" or "default" ? $"{Name} {sub}" : Name;
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

        // 仅支持 chat 与 agent 两个子命令，其余视为未知子命令
        if (sub != "chat" && sub != "agent")
        {
            var msg = $"未知子命令 '{parts[0]}'，可用: chat, agent, default";
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

        // 解析可选的配置名与消息（与 $agent/$chat 一致：首个非 default 词视为配置名）
        var restParts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string configName = "default";
        string content;
        if (restParts.Length == 2 && restParts[0] != "default")
        {
            configName = restParts[0];
            content = restParts[1];
        }
        else
        {
            content = rest;
        }

        // 当 configName 为 "default" 时，改用 AppConfig 中配置的默认 AI 配置名
        if (configName == "default")
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

        // 根据子命令调用对应的 AI 服务（chat 对话 / agent 代理执行），异常统一捕获并返回失败结果
        try
        {
            var (model, response) = sub == "chat"
                ? await args.AgentService.ChatAsync(configName, content)
                : await args.AgentService.AgentAsync(configName, content);
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
}
