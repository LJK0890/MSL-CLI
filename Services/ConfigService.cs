using System.Text.Json;
using static MSL_CLI.IO.IO;
using MSL_CLI.Models;

namespace MSL_CLI.Services;

/// <summary>
/// 配置服务，专门负责配置的加载与保存，与业务逻辑解耦。
/// </summary>
internal class ConfigService
{
    private readonly string _userConfigPath;
    private readonly string _defaultConfigPath;

    public ConfigService()
    {
        _userConfigPath = GetUserConfigPath();
        _defaultConfigPath = GetDefaultConfigPath();
    }

    /// <summary>
    /// 加载配置。优先加载用户配置，若不存在或损坏则回退到默认配置。
    /// </summary>
    public AppConfig LoadConfig()
    {
        Print("Global/Config", LogLevel.INFO, $"尝试从用户配置加载: {_userConfigPath}", includeTimestamp: true);

        // 1. 尝试加载用户配置
        if (File.Exists(_userConfigPath))
        {
            try
            {
                string json = File.ReadAllText(_userConfigPath);
                AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json, AppConstants.ReadOptions);
                if (config != null)
                {
                    Print("Global/Config", LogLevel.INFO, "用户配置加载成功。", includeTimestamp: true);
                    return config;
                }
            }
            catch (JsonException ex)
            {
                Print("Global/Config", LogLevel.ERROR, $"用户配置 JSON 解析失败: {ex.Message}，将尝试备份并重建。", includeTimestamp: true);
                BackupCorruptedConfig(_userConfigPath);
            }
        }

        // 2. 加载或创建默认配置
        Print("Global/Config", LogLevel.INFO, $"尝试加载默认配置: {_defaultConfigPath}", includeTimestamp: true);
        AppConfig defaultConfig = LoadOrCreateDefaultConfig();

        // 自动保存一份新的用户配置
        SaveConfig(defaultConfig);
        Print("Global/Config", LogLevel.INFO, "已创建并保存新的用户配置。", includeTimestamp: true);

        return defaultConfig;
    }

    /// <summary>
    /// 保存配置到用户目录，采用原子替换策略。
    /// </summary>
    public void SaveConfig(AppConfig config)
    {
        string tempPath = _userConfigPath + ".tmp";
        try
        {
            // 确保目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(_userConfigPath)!);

            string json = JsonSerializer.Serialize(config, AppConstants.WriteOptions);
            File.WriteAllText(tempPath, json);

            if (File.Exists(_userConfigPath))
            {
                File.Replace(tempPath, _userConfigPath, null);
            }
            else
            {
                File.Move(tempPath, _userConfigPath);
            }
            Print("Global/Config", LogLevel.INFO, $"配置已保存到 {_userConfigPath}", includeTimestamp: true);
        }
        catch (Exception ex)
        {
            Print("Global/Config", LogLevel.ERROR, $"保存配置失败: {ex.Message}", includeTimestamp: true);
            throw; // 重新抛出异常以便调用者知道保存失败
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* 忽略 */ }
            }
        }
    }

    // --- 私有辅助方法 ---

    private void BackupCorruptedConfig(string path)
    {
        try
        {
            string backupPath = path + ".bak";
            File.Copy(path, backupPath, overwrite: true);
        }
        catch { /* 忽略备份失败 */ }
    }

    private AppConfig LoadOrCreateDefaultConfig()
    {
        if (File.Exists(_defaultConfigPath))
        {
            try
            {
                string json = File.ReadAllText(_defaultConfigPath);
                AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json, AppConstants.ReadOptions);
                if (config != null)
                {
                    Print("Global/Config", LogLevel.INFO, "默认配置加载成功。", includeTimestamp: true);
                    return config;
                }
            }
            catch (JsonException ex)
            {
                Print("Global/Config", LogLevel.ERROR, $"默认配置解析失败: {ex.Message}，将创建空默认配置。", includeTimestamp: true);
            }
        }

        var defaultCfg = new AppConfig();
        try
        {
            string json = JsonSerializer.Serialize(defaultCfg, AppConstants.WriteOptions);
            string? dir = Path.GetDirectoryName(_defaultConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_defaultConfigPath, json);
            Print("Global/Config", LogLevel.INFO, "已创建新的默认配置文件。", includeTimestamp: true);
        }
        catch (Exception ex)
        {
            Print("Global/Config", LogLevel.ERROR, $"创建默认配置失败: {ex.Message}，回退到空配置。", includeTimestamp: true);
        }
        return defaultCfg;
    }

    private static string GetUserConfigPath()
    {
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string configDir = Path.Combine(appDataPath, AppConstants.AppName);
        return Path.Combine(configDir, AppConstants.ConfigFileName);
    }

    private static string GetDefaultConfigPath()
    {
        string baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, AppConstants.DefaultConfigFileName);
    }
}