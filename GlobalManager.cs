using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSL_CLI;


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

internal class AppConfig
{
    public bool EnableAI { get; set; } = true;

    public Dictionary<string, AIConfig> AIConfigs { get; set; } = new();

    public Dictionary<string, string> ServerPaths { get; set; } = new();
}

internal static class AppConstants
{
    public static readonly string AppName = "MSL_CLI";
    public static readonly string ConfigFileName = "config.json";
    public static readonly string DefaultConfigFileName = "config.default.json";

    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}

class GlobalManager : IDisposable
{
    private AppConfig? appConfig;
    private bool disposed = false;

    public GlobalManager()
    {
        appConfig = LoadConfig();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        if (disposing)
        {
            if (appConfig != null)
            {
                try
                {
                    SaveConfig(appConfig);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[GlobalManager.Dispose] 保存配置失败: {ex.Message}");
                }
            }
        }
        disposed = true;
    }

    ~GlobalManager()
    {
        Dispose(false);
    }

    public void PrintConfig()
    {
        if (appConfig == null)
        {
            Console.WriteLine("配置未加载");
            return;
        }

        Console.WriteLine("EnableAI: " + appConfig.EnableAI);
        Console.WriteLine("AIConfigs:");
        foreach (var kvp in appConfig.AIConfigs)
        {
            string keyDisplay = string.IsNullOrEmpty(kvp.Value.ApiKey)
                ? "(empty)"
                : "****";

            Console.WriteLine($"  {kvp.Key}:");
            Console.WriteLine($"    Url={kvp.Value.Url}");
            Console.WriteLine($"    Model={kvp.Value.Model}");
            Console.WriteLine($"    ApiKey={keyDisplay}");
            Console.WriteLine($"    UseApiKeyEnv={kvp.Value.UseApiKeyEnv}");
            Console.WriteLine($"    ApiKeyEnv={kvp.Value.ApiKeyEnv}");
        }
        Console.WriteLine("ServerPaths:");
        foreach (var kvp in appConfig.ServerPaths)
        {
            Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
        }
    }

    private static AppConfig LoadConfig()
    {
        string userConfigPath = GetUserConfigPath();
        if (File.Exists(userConfigPath))
        {
            try
            {
                string json = File.ReadAllText(userConfigPath);
                AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json, AppConstants.ReadOptions);
                if (config != null)
                {
                    return config;
                }
            }
            catch (JsonException)
            {
                string backupPath = userConfigPath + ".bak";
                try { File.Copy(userConfigPath, backupPath, overwrite: true); } catch { /* 忽略备份失败 */ }
            }
        }

        string defaultConfigPath = GetDefaultConfigPath();
        AppConfig defaultConfig = LoadDefaultConfig(defaultConfigPath);
        SaveConfig(defaultConfig);
        return defaultConfig;
    }

    private static void SaveConfig(AppConfig config)
    {
        string userConfigPath = GetUserConfigPath();
        string tempPath = userConfigPath + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(config, AppConstants.WriteOptions);
            File.WriteAllText(tempPath, json);
            if (File.Exists(userConfigPath))
            {
                File.Replace(tempPath, userConfigPath, null);
            }
            else
            {
                File.Move(tempPath, userConfigPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* 忽略 */ }
            }
        }
    }

    private static string GetUserConfigPath()
    {
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string configDir = Path.Combine(appDataPath, AppConstants.AppName);
        Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, AppConstants.ConfigFileName);
    }

    private static string GetDefaultConfigPath()
    {
        string baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, AppConstants.DefaultConfigFileName);
    }

    private static AppConfig LoadDefaultConfig(string defaultConfigPath)
    {
        if (File.Exists(defaultConfigPath))
        {
            try
            {
                string json = File.ReadAllText(defaultConfigPath);
                AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json, AppConstants.ReadOptions);
                if (config != null) return config;
            }
            catch (JsonException)
            {
                // 默认配置文件也损坏，回退
            }
        }

        var defaultCfg = new AppConfig();
        try
        {
            string json = JsonSerializer.Serialize(defaultCfg, AppConstants.WriteOptions);
            string? dir = Path.GetDirectoryName(defaultConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(defaultConfigPath, json);
            return defaultCfg;
        }
        catch
        {
            // 创建失败（例如无写权限），回退
        }
        return defaultCfg;
    }
}