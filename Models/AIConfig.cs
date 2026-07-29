using static MSL_CLI.IO.IO;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSL_CLI.Models;

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

        // 如果启用了环境变量，则从环境变量中读取 ApiKey
        if (config.UseApiKeyEnv && !string.IsNullOrEmpty(config.ApiKeyEnv))
        {
            config.ApiKey = Environment.GetEnvironmentVariable(config.ApiKeyEnv) ?? string.Empty;
            Output.Print("Global/AI", LogLevel.INFO, $"从环境变量 '{config.ApiKeyEnv}' 读取 ApiKey ({(string.IsNullOrEmpty(config.ApiKey) ? "失败" : "成功")})", includeTimestamp: true);
        }

        return config;
    }

    public override void Write(Utf8JsonWriter writer, AIConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteString(nameof(AIConfig.Url), value.Url);
        writer.WriteString(nameof(AIConfig.Model), value.Model);
        writer.WriteBoolean(nameof(AIConfig.UseApiKeyEnv), value.UseApiKeyEnv);

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