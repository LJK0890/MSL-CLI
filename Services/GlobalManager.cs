using System.Text.Json;

using MSL_CLI.Models;

namespace MSL_CLI.Services;

internal class GlobalManager : IDisposable
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