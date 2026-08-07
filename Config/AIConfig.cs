using static MSL_CLI.IO.IO;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSL_CLI.Config;

/// <summary>
/// AI 配置模型，存储单个 AI 服务的连接信息。
/// </summary>
[JsonConverter(typeof(AIConfigConverter))]
internal class AIConfig
{
    /// <summary>API 请求地址</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>使用的模型名称</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>是否从环境变量读取 ApiKey</summary>
    public bool UseApiKeyEnv { get; set; } = true;

    /// <summary>明文 ApiKey（当 UseApiKeyEnv=false 时使用）</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>环境变量名称（当 UseApiKeyEnv=true 时使用）</summary>
    public string? ApiKeyEnv { get; set; }

    public int MaxIterations { get; set; } = 16;

    public string Prompt { get; set; } = """
        
            你是一个Minecraft服务器管理助手，你可以：
            - 执行以$开头的普通命令（这些命令会立即返回结果）
            - 进行等待
            - 执行以$开头的特殊命令（$run/$runnow/$rn/$stop/$stopnow/$sn/$send/$sendn）
            - 执行多步耗时任务（如启动→发指令→停止），但是必须严格遵循“执行→等待→验证（失败则重试）”循环协议。
            命令摘要：
            {commandList}
            对每个特殊命令：
            1. execute_command(操作)
            2. sleep(等待)   // 启动或停止用较长等待，$send可短暂或跳过
            3. execute_command("$updatebuffer <服务器>")  // 读取并清空日志
            4. 分析日志：
                - 若未达预期（如$run未见Done、$stop未关闭、$send未生效）→ 回到步骤2重试（最多5次）
                - 若成功 → 进入下一操作
            5. 所有操作完成后汇报结果。

            规则：
            - 每次execute_command仅执行一条$命令。
            - 必须用$updatebuffer获取实时反馈，禁止仅凭sleep盲目推进。

            回复纯文本，不能使用markdow等富文本格式，简述当前步骤、日志状态及下一步决策。
        """;
}

/// <summary>
/// AIConfig 的自定义 JSON 转换器，支持从环境变量读取 ApiKey。
/// </summary>
internal class AIConfigConverter : JsonConverter<AIConfig>
{
    public override AIConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var config = new AIConfig();

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

        if (root.TryGetProperty("Prompt", out var prompt))
            config.Prompt = prompt.GetString() ?? string.Empty;

        // 如果启用了环境变量，则从环境变量中读取 ApiKey
        if (config.UseApiKeyEnv && !string.IsNullOrEmpty(config.ApiKeyEnv))
        {
            config.ApiKey = Environment.GetEnvironmentVariable(config.ApiKeyEnv) ?? string.Empty;
            Print("Global/AI", LogLevel.INFO, $"从环境变量 '{config.ApiKeyEnv}' 读取 ApiKey ({(string.IsNullOrEmpty(config.ApiKey) ? "失败" : "成功")})", includeTimestamp: true);
        }

        return config;
    }

    public override void Write(Utf8JsonWriter writer, AIConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteString(nameof(AIConfig.Url), value.Url);
        writer.WriteString(nameof(AIConfig.Model), value.Model);
        writer.WriteBoolean(nameof(AIConfig.UseApiKeyEnv), value.UseApiKeyEnv);
        writer.WriteNumber(nameof(AIConfig.MaxIterations), value.MaxIterations);
        writer.WriteString(nameof(AIConfig.Prompt), value.Prompt);

        // 根据配置决定写入 ApiKey 还是 ApiKeyEnv
        if (value.UseApiKeyEnv)
        {
            writer.WriteString(nameof(AIConfig.ApiKeyEnv), value.ApiKeyEnv);
        }
        else
        {
            writer.WriteString(nameof(AIConfig.ApiKey), value.ApiKey);
        }

        writer.WriteEndObject();
    }
}