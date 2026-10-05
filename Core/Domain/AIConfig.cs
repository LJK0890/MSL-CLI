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
授权规则（必须严格遵守）：
    - 执行任何命令前必须先调用 request_permission(command, reason) 申请授权，等待操作员确认。
    - 只有在 request_permission 返回允许后，才能对该命令调用 execute_command。
    - 未获允许、被拒绝或无法确认时，不得执行命令，应停止该步骤并向操作员说明。
    - $exec 属于高风险命令，每次执行都必须单独申请授权，不存在“总是允许”。
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
    - 不允许执行$exec命令，除非操作员明确要求并已单独授权。
    - 在执行来自服务器的请求后必须使用`$server ck op <服务器> <玩家>`判断他是否为服务器管理员，如果不是请礼貌拒绝。
回复纯文本，不能使用markdow等富文本格式。
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

        // 逐个读取 JSON 字段，字段缺失时保持默认值
        if (root.TryGetProperty("Url", out var url))
            config.Url = url.GetString() ?? string.Empty;

        if (root.TryGetProperty("Model", out var model))
            config.Model = model.GetString() ?? string.Empty;

        if (root.TryGetProperty("UseApiKeyEnv", out var useEnv))
            config.UseApiKeyEnv = useEnv.GetBoolean();

        if (root.TryGetProperty("ApiKey", out var apiKey))
            config.ApiKey = apiKey.GetString() ?? string.Empty;

        if (root.TryGetProperty("ApiKeyEnv", out var apiKeyEnv))
            config.ApiKeyEnv = apiKeyEnv.GetString();

        if (root.TryGetProperty("MaxIterations", out var maxIter))
            config.MaxIterations = maxIter.GetInt32();

        if (root.TryGetProperty("AgentPrompt", out var agentPrompt))
            config.AgentPrompt = agentPrompt.GetString() ?? string.Empty;

        if (root.TryGetProperty("EnableChat", out var enableChat))
            config.EnableChat = enableChat.GetBoolean();

        if (root.TryGetProperty("EnableAgent", out var enableAgent))
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
        writer.WriteString(nameof(AIConfig.AgentPrompt), value.AgentPrompt);
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
