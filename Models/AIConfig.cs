using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSL_CLI.Models;

[JsonConverter(typeof(AIConfigConverter))]
internal class AIConfig
{
    public string Url { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool UseApiKeyEnv { get; set; } = true;
    public string ApiKey { get; set; } = string.Empty;
    public string? ApiKeyEnv { get; set; }
}

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

        if (config.UseApiKeyEnv && !string.IsNullOrEmpty(config.ApiKeyEnv))
        {
            config.ApiKey = Environment.GetEnvironmentVariable(config.ApiKeyEnv) ?? string.Empty;
        }

        return config;
    }

    public override void Write(Utf8JsonWriter writer, AIConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteString(nameof(AIConfig.Url), value.Url);
        writer.WriteString(nameof(AIConfig.Model), value.Model);
        writer.WriteBoolean(nameof(AIConfig.UseApiKeyEnv), value.UseApiKeyEnv);

        if (value.UseApiKeyEnv)
        {
            writer.WriteString(nameof(AIConfig.ApiKey), string.Empty);
            writer.WriteString(nameof(AIConfig.ApiKeyEnv), value.ApiKeyEnv);
        }
        else
        {
            writer.WriteString(nameof(AIConfig.ApiKey), value.ApiKey);
        }

        writer.WriteEndObject();
    }
}