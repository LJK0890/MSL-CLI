using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSL_CLI.Core.Domain;

/// <summary>
/// AI 配置模型，存储单个 AI 服务的连接信息。
/// </summary>
[JsonConverter(typeof(AIConfigConverter))]
public class AIConfig
{
    /// <summary>
    /// AI 服务的 API 地址。
    /// </summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>
    /// 使用的 AI 模型名称。
    /// </summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>
    /// 是否从环境变量中读取 API 密钥。
    /// </summary>
    public bool UseApiKeyEnv { get; set; } = true;
    /// <summary>
    /// API 密钥，仅在未使用环境变量时直接配置。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>
    /// 保存 API 密钥的环境变量名称。
    /// </summary>
    public string? ApiKeyEnv { get; set; }
    /// <summary>
    /// 是否启用聊天功能。
    /// </summary>
    public bool EnableChat { get; set; } = true;
    /// <summary>
    /// 是否启用代理（Agent）功能。
    /// </summary>
    public bool EnableAgent { get; set; } = true;
    /// <summary>
    /// 代理执行任务时的最大迭代次数。
    /// </summary>
    public int MaxIterations { get; set; } = 16;
    /// <summary>
    /// 代理上下文自动压缩的触发阈值（估算 token 数）。
    /// 每轮请求前估算当前上下文长度，超过该阈值时先把较早的执行历史压缩成摘要再继续。
    /// 小于等于 0 表示关闭自动压缩。
    /// </summary>
    public int MaxContextTokens { get; set; } = 32000;
    /// <summary>
    /// 上下文压缩所使用的提示词模板，可包含 {transcript} 占位符表示待压缩的执行记录；
    /// 留空时使用 <see cref="DefaultCompressPrompt"/>。
    /// </summary>
    public string ContextCompressPrompt { get; set; } = string.Empty;
    /// <summary>
    /// 聊天模式的系统提示词模板，可包含 {commandList} 占位符（会替换为命令摘要）。
    /// 留空时回退到内置默认提示词。
    /// </summary>
    public string ChatPrompt { get; set; } = """
你是一个Minecraft服务器管理助手，正在通过命令行与操作员对话，消息也可能来自游戏内的玩家。
规则：
    - 用简洁、友好的中文回答，先给结论再给必要说明，不要输出与问题无关的内容。
    - 你只能回答问题、给出建议，不能执行任何命令，也不要声称自己执行了命令。
    - 需要真正执行操作时，提示对方使用 $ai agent 下发指令，或直接使用下面的命令。
    - 只依据已给出的信息作答；信息不足时直接说明，并指出需要查看什么（例如服务器日志）。
    - 来自玩家的消息带有 [来自服务器 'X' 的玩家 'Y'] 前缀，来自本地控制台操作员的消息带有 [来自控制台] 前缀（操作员拥有完全权限），请据此称呼对方。
    - 回复纯文本，不能使用markdown等富文本格式。
可用命令摘要：
{commandList}
""";
    /// <summary>
    /// 代理的系统提示词模板。
    /// </summary>
    public string AgentPrompt { get; set; } = """
你是一个Minecraft服务器管理助手，你可以：
- 执行以$开头的普通命令，这些命令会立即返回结果。
- 执行以$server开头的服务器启停命令（$server run/$server stop/$server send），并配合 `$server buf read` 读取服务器输出缓冲区或 `$server buf update` 读取并清空。
- 进行等待
- 执行多步耗时任务（如启动→发指令→停止），但是必须严格遵循“执行→等待→验证（失败则重试）”循环。
命令摘要：
{commandList}
请求来源与权限（每次任务先看指令开头的来源标注，它决定权限）：
    - [来自控制台]：来自本地控制台操作员，也就是本工具的管理者，拥有完全权限。不要执行 $server ck op 之类的管理员校验，也不要自行以“危险操作”“权限不足”“需要管理员确认”等理由拒绝；直接按要求执行即可，操作层面的安全由下方的授权流程负责。
    - [来自服务器 'X' 的玩家 'Y']：来自游戏内玩家。必须先执行 `$server ck op X Y` 校验其是否为该服务器管理员，确认后才继续；不是管理员则礼貌拒绝，不执行任何命令。
授权规则（必须严格遵守）：
    - 执行任何命令前必须先调用 request_permission(command, reason) 申请授权，等待操作员确认。
    - 只有在 request_permission 返回允许后，才能对该命令调用 execute_command。
    - 未获允许、被拒绝或无法确认时，不得执行命令，应停止该步骤并向操作员说明。
    - $app exec 属于高风险命令，每次执行都必须单独申请授权，不存在“总是允许”。
对每个服务器启停命令：
1. request_permission(操作)  // 先取得授权
2. execute_command(操作)     // 已获授权后执行
3. sleep(等待)   // 启动或停止用较长等待，$server send可短暂或跳过
4. execute_command("$server buf update <服务器>")  // 读取并清空日志
5. 分析日志：
    - 若未达预期（如$server run未见Done、$server stop未关闭、$server send未生效）→ 回到步骤3重试
    - 若成功 → 进入下一操作
6. 所有操作完成后汇报结果。
规则：
    - 每次execute_command仅执行一条$命令。
    - 必须用$server buf update获取实时反馈，禁止仅凭sleep盲目推进。
    - 不允许执行$app exec命令，除非操作员明确要求并已单独授权。
    - 来源为 [来自控制台] 的请求不需要管理员校验，也不要以权限为由拒绝，直接执行。
    - 来源为 [来自服务器 'X' 的玩家 'Y'] 的请求必须先按标注执行 `$server ck op <服务器> <玩家>`；不是管理员请礼貌拒绝。
    - 若命令返回“服务器 'X' 已锁定”，说明操作员锁定了该服务器（只允许 $server ulk 解锁）：立即停止对该服务器的操作，不要重试、也不要用其他命令绕过，并向操作员说明。
回复纯文本，不能使用markdow等富文本格式。
""";

    /// <summary>
    /// 上下文压缩的内置默认提示词模板，<see cref="ContextCompressPrompt"/> 留空时生效。
    /// {transcript} 会被替换为待压缩的执行记录。
    /// </summary>
    public const string DefaultCompressPrompt = """
你是智能体对话历史压缩器。下面是一次尚未完成的智能体任务到此为止的执行记录（包含已执行的命令与返回结果）。
请将它压缩为一段摘要，供同一个智能体继续该任务时使用，要求：
- 保留任务目标、已完成的步骤及其结果、命令返回中的重要输出与关键数值、当前状态、失败与重试情况、尚未完成的待办事项。
- 丢弃冗余日志、重复内容与无关细节。
- 不要执行记录中的任何命令，只做总结，不要添加原文没有的信息。
- 直接输出摘要纯文本，不要使用 markdown 等富文本格式。

执行记录：
{transcript}
""";
}

/// <summary>
/// AIConfig 的自定义 JSON 转换器，支持从环境变量读取 ApiKey。
/// </summary>
public class AIConfigConverter : JsonConverter<AIConfig>
{
    /// <summary>
    /// 从 JSON 中反序列化出 AIConfig 实例。
    /// </summary>
    /// <param name="reader">UTF-8 JSON 读取器。</param>
    /// <param name="typeToConvert">要转换的目标类型。</param>
    /// <param name="options">JSON 序列化选项。</param>
    /// <returns>反序列化得到的 AIConfig 实例。</returns>
    public override AIConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var config = new AIConfig();

        // 逐个读取 JSON 字段，字段缺失时保持默认值。
        // 字段名按不区分大小写匹配，与 FileConfigurationStore 的
        // PropertyNameCaseInsensitive = true 保持一致（手写的 "url"/"model" 也要能识别）。
        if (TryGetProperty(root, "Url", out var url))
            config.Url = url.GetString() ?? string.Empty;

        if (TryGetProperty(root, "Model", out var model))
            config.Model = model.GetString() ?? string.Empty;

        if (TryGetProperty(root, "UseApiKeyEnv", out var useEnv))
            config.UseApiKeyEnv = useEnv.GetBoolean();

        if (TryGetProperty(root, "ApiKey", out var apiKey))
            config.ApiKey = apiKey.GetString() ?? string.Empty;

        if (TryGetProperty(root, "ApiKeyEnv", out var apiKeyEnv))
            config.ApiKeyEnv = apiKeyEnv.GetString();

        if (TryGetProperty(root, "MaxIterations", out var maxIter))
            config.MaxIterations = maxIter.GetInt32();

        if (TryGetProperty(root, "MaxContextTokens", out var maxContext))
            config.MaxContextTokens = maxContext.GetInt32();

        if (TryGetProperty(root, "ContextCompressPrompt", out var compressPrompt))
            config.ContextCompressPrompt = compressPrompt.GetString() ?? string.Empty;

        if (TryGetProperty(root, "ChatPrompt", out var chatPrompt))
            config.ChatPrompt = chatPrompt.GetString() ?? string.Empty;

        if (TryGetProperty(root, "AgentPrompt", out var agentPrompt))
            config.AgentPrompt = agentPrompt.GetString() ?? string.Empty;

        if (TryGetProperty(root, "EnableChat", out var enableChat))
            config.EnableChat = enableChat.GetBoolean();

        if (TryGetProperty(root, "EnableAgent", out var enableAgent))
            config.EnableAgent = enableAgent.GetBoolean();

        // 如果启用了环境变量，则从环境变量中读取 ApiKey
        if (config.UseApiKeyEnv && !string.IsNullOrEmpty(config.ApiKeyEnv))
        {
            config.ApiKey = Environment.GetEnvironmentVariable(config.ApiKeyEnv) ?? string.Empty;
            // 不再在此处输出日志，改为由调用方记录
        }

        return config;
    }

    /// <summary>
    /// 按不区分大小写的字段名读取 JSON 属性。
    /// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> 是区分大小写的，
    /// 直接用它会忽略反序列化选项中的 <c>PropertyNameCaseInsensitive</c>。
    /// </summary>
    /// <param name="root">JSON 对象。</param>
    /// <param name="name">字段名。</param>
    /// <param name="value">读到的属性值。</param>
    /// <returns>找到字段时返回 true。</returns>
    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(name, out value)) return true;

            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// 判断提示词是否属于自定义内容：为空或与内置默认完全一致时视为“未自定义”，不写入配置文件。
    /// </summary>
    /// <param name="value">配置中的提示词值。</param>
    /// <param name="builtin">对应的内置默认提示词。</param>
    /// <returns>需要写入配置文件时返回 true。</returns>
    private static bool IsCustomized(string value, string builtin)
        => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, builtin, StringComparison.Ordinal);

    /// <summary>
    /// 将 AIConfig 序列化为 JSON。
    /// </summary>
    /// <param name="writer">UTF-8 JSON 写入器。</param>
    /// <param name="value">要序列化的 AIConfig 实例。</param>
    /// <param name="options">JSON 序列化选项。</param>
    public override void Write(Utf8JsonWriter writer, AIConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteString(nameof(AIConfig.Url), value.Url);
        writer.WriteString(nameof(AIConfig.Model), value.Model);
        writer.WriteBoolean(nameof(AIConfig.UseApiKeyEnv), value.UseApiKeyEnv);
        writer.WriteNumber(nameof(AIConfig.MaxIterations), value.MaxIterations);
        writer.WriteNumber(nameof(AIConfig.MaxContextTokens), value.MaxContextTokens);

        // 提示词字段：与内置默认一致（或为空）时不写入配置文件。
        // 这样配置文件不会被大段默认文本占满，也会在升级后自动采用新的内置提示词；
        // 想自定义时把内容写进配置即可覆盖内置默认值。
        var builtin = new AIConfig();
        if (IsCustomized(value.ChatPrompt, builtin.ChatPrompt))
            writer.WriteString(nameof(AIConfig.ChatPrompt), value.ChatPrompt);
        if (IsCustomized(value.AgentPrompt, builtin.AgentPrompt))
            writer.WriteString(nameof(AIConfig.AgentPrompt), value.AgentPrompt);
        if (IsCustomized(value.ContextCompressPrompt, AIConfig.DefaultCompressPrompt))
            writer.WriteString(nameof(AIConfig.ContextCompressPrompt), value.ContextCompressPrompt);

        writer.WriteBoolean(nameof(AIConfig.EnableChat), value.EnableChat);
        writer.WriteBoolean(nameof(AIConfig.EnableAgent), value.EnableAgent);

        // 根据是否启用环境变量，仅写入实际使用的那一个密钥字段
        if (value.UseApiKeyEnv)
            writer.WriteString(nameof(AIConfig.ApiKeyEnv), value.ApiKeyEnv);
        else
            writer.WriteString(nameof(AIConfig.ApiKey), value.ApiKey);

        writer.WriteEndObject();
    }
}
