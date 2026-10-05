using System.Text.RegularExpressions;

namespace MSL_CLI.Core.Domain;

/// <summary>
/// 玩家聊天行的解析结果。
/// </summary>
/// <param name="Player">玩家名。</param>
/// <param name="Message">聊天正文。</param>
public readonly record struct PlayerChatMessage(string Player, string Message);

/// <summary>
/// 玩家消息中 AI 触发指令的解析结果。
/// </summary>
/// <param name="ConfigName">AI 配置名，"default" 表示使用全局默认配置。</param>
/// <param name="IsAgent">是否为代理模式。</param>
/// <param name="Content">交给 AI 的内容。</param>
public readonly record struct AiTriggerCommand(string ConfigName, bool IsAgent, string Content);

/// <summary>
/// Minecraft 服务端聊天行解析器：识别玩家聊天、过滤非聊天行，并解析 AI 触发指令。
/// 纯逻辑，不依赖任何基础设施。
/// </summary>
public static class MinecraftChatParser
{
    /// <summary>
    /// 匹配聊天正文中的 <c>&lt;玩家名&gt; 消息</c>。
    /// 只在行尾锚定，因此日志前缀（如 <c>[21:08:29]</c>、<c>[Server thread/INFO]</c>）
    /// 不会被纳入尖括号判定。
    /// </summary>
    private static readonly Regex ChatLinePattern =
        new(@"<(?<player>[^<>]+)>\s*(?<message>.*)$", RegexOptions.Compiled);

    /// <summary>
    /// 解析服务器返回的玩家聊天行，形如：
    /// <c>[21:08:29] [Server thread/INFO] [net.minecraft.server.MinecraftServer/]: &lt;Sparky_0890&gt; 点任务啊</c>。
    /// 只看最后一个 <c>": "</c> 之后的内容，因此日志前缀里的方括号不会干扰识别。
    /// </summary>
    /// <param name="rawLine">服务器输出的一整行。</param>
    /// <param name="chat">解析出的玩家名与聊天正文。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryParsePlayerChat(string rawLine, out PlayerChatMessage chat)
    {
        chat = default;

        if (string.IsNullOrWhiteSpace(rawLine))
            return false;

        // 只取最后一个 ": " 之后的内容，避免日志前缀里的冒号干扰
        var separator = rawLine.LastIndexOf(": ", StringComparison.Ordinal);
        if (separator < 0)
            return false;

        var payload = rawLine.Substring(separator + 2).Trim();
        if (payload.Length == 0)
            return false;

        var match = ChatLinePattern.Match(payload);
        if (!match.Success)
            return false;

        var player = match.Groups["player"].Value.Trim();
        var message = match.Groups["message"].Value.Trim();

        if (player.Length == 0 || message.Length == 0)
            return false;

        chat = new PlayerChatMessage(player, message);
        return true;
    }

    /// <summary>
    /// 判断聊天正文中有几个尖括号对。多于一对时（例如 <c>&lt;a&gt; &lt;b&gt;</c>）
    /// 视为普通聊天，不作为 AI 命令转发。
    /// </summary>
    /// <param name="message">聊天正文。</param>
    /// <returns>正文中的尖括号对数量。</returns>
    public static int CountAngleBracketPairs(string message)
        => string.IsNullOrEmpty(message) ? 0 : CountPairs(message);

    /// <summary>
    /// 解析玩家消息中的 AI 触发指令，支持三种格式：
    /// <c>$chat [配置名] 消息</c>、<c>$agent [配置名] 指令</c>、<c>@ai &lt;配置名&gt; chat|agent 内容</c>。
    /// </summary>
    /// <param name="message">玩家聊天正文（不含玩家名）。</param>
    /// <param name="command">解析出的触发指令。</param>
    /// <returns>是 AI 触发命令返回 true。</returns>
    public static bool TryParseAiCommand(string message, out AiTriggerCommand command)
    {
        command = default;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        var trimmed = message.Trim();

        // 格式1/2：$chat [配置名] 消息 / $agent [配置名] 指令
        if (StartWithToken(trimmed, "$chat", out var chatRest))
        {
            if (!TrySplitConfigAndContent(chatRest, out var configName, out var content))
                return false;

            command = new AiTriggerCommand(configName, false, content);
            return true;
        }

        if (StartWithToken(trimmed, "$agent", out var agentRest))
        {
            if (!TrySplitConfigAndContent(agentRest, out var configName, out var content))
                return false;

            command = new AiTriggerCommand(configName, true, content);
            return true;
        }

        // 格式3：@ai <配置名> chat|agent 内容
        if (StartWithToken(trimmed, "@ai", out var aiRest))
        {
            var parts = aiRest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return false;

            var configName = parts[0];
            var mode = parts[1].ToLowerInvariant();
            var content = parts[2].Trim();

            if (content.Length == 0)
                return false;

            if (mode == "chat")
            {
                command = new AiTriggerCommand(configName, false, content);
                return true;
            }

            if (mode == "agent")
            {
                command = new AiTriggerCommand(configName, true, content);
                return true;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// 解析玩家对授权提问的作答：只有明确的 y/yes、a/always、n/no 才算有效作答。
    /// </summary>
    /// <param name="message">玩家聊天正文。</param>
    /// <param name="answer">归一化后的答案关键字。</param>
    /// <returns>是有效作答返回 true。</returns>
    public static bool TryParsePermissionAnswer(string message, out string answer)
    {
        answer = string.Empty;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        var normalized = message.Trim().ToLowerInvariant();
        if (normalized is "y" or "yes" or "n" or "no" or "a" or "always")
        {
            answer = normalized;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 统计文本中 &lt;&gt; 成对出现的次数（支持嵌套）。
    /// </summary>
    /// <param name="text">待统计文本。</param>
    /// <returns>成对出现的数量。</returns>
    private static int CountPairs(string text)
    {
        var count = 0;
        var depth = 0;

        foreach (var c in text)
        {
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                if (depth > 0)
                {
                    count++;
                    depth--;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// 判断文本是否以指定指令开头（后接空白或结束），并返回其后的剩余文本。
    /// </summary>
    /// <param name="text">待判断文本。</param>
    /// <param name="token">指令前缀，如 "$chat"。</param>
    /// <param name="rest">指令之后的剩余文本。</param>
    /// <returns>匹配返回 true。</returns>
    private static bool StartWithToken(string text, string token, out string rest)
    {
        rest = string.Empty;

        if (!text.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            return false;

        // 必须紧跟空白或结束，避免 "$chatx" 之类被误判
        if (text.Length > token.Length && !char.IsWhiteSpace(text[token.Length]))
            return false;

        rest = text.Substring(token.Length).TrimStart();
        return true;
    }

    /// <summary>
    /// 把「可选配置名 + 内容」拆开：首个词不是 "default" 且后面还有内容时视为配置名。
    /// </summary>
    /// <param name="rest">指令之后的文本。</param>
    /// <param name="configName">解析出的配置名。</param>
    /// <param name="content">解析出的内容。</param>
    /// <returns>内容非空时返回 true。</returns>
    private static bool TrySplitConfigAndContent(string rest, out string configName, out string content)
    {
        configName = "default";
        content = rest.Trim();

        if (content.Length == 0)
            return false;

        var parts = content.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && !parts[0].Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            configName = parts[0];
            content = parts[1].Trim();
        }

        return content.Length > 0;
    }
}
