using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

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
    public static AppConfig LoadConfig()
    {
        string userConfigPath = GetUserConfigPath();
        if (File.Exists(userConfigPath))
        {
            try
            {
                string json = File.ReadAllText(userConfigPath);
                AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json, _readOptions);
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

    public static void SaveConfig(AppConfig config)
    {
        string userConfigPath = GetUserConfigPath();
        string tempPath = userConfigPath + ".tmp";

        try
        {
            string json = JsonSerializer.Serialize(config, _writeOptions);
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

    public static string? GetApiKey()
    {
        return Environment.GetEnvironmentVariable("MSL_CLI_API_KEY");
    }

    /// <summary>
    /// 获取用户配置文件的完整路径（跨平台）
    /// </summary>
    private static string GetUserConfigPath()
    {
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string configDir = Path.Combine(appDataPath, _appName);
        Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, _configFileName);
    }

    /// <summary>
    /// 获取默认配置文件的完整路径（程序安装目录）
    /// </summary>
    private static string GetDefaultConfigPath()
    {
        string baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, _defaultConfigFileName);
    }

    /// <summary>
    /// 从程序目录加载默认配置
    /// 如果默认配置文件也不存在，则返回硬编码的兜底默认值
    /// </summary>
    private static AppConfig LoadDefaultConfig(string defaultConfigPath)
    {
        if (File.Exists(defaultConfigPath))
        {
            try
            {
                string json = File.ReadAllText(defaultConfigPath);
                AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json, _readOptions);
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