using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSL_CLI;

internal class AIConfig
{
    public string Url = string.Empty;
    public string Model = string.Empty;
    public string? ApiKey;
    public string? ApiKeyEnv;
}
internal class AppConfig
{
    public bool EnableAI = true;
    public Dictionary<string, AIConfig> AIConfigs = new();
    public Dictionary<string, string> ServerPaths = new();
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
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
class GlobalManager
{
    private AppConfig appConfig;
    public GlobalManager()
    {
        appConfig = LoadConfig();
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
            File.Replace(tempPath, userConfigPath, null);
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
                // 默认配置文件也损坏，回退到硬编码兜底值
            }
        }

        // 硬编码兜底默认值（确保程序在任何情况下都能启动）
        return new AppConfig();
    }
}