using System.Text.Json;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 基于 JSON 文件的应用配置存储实现，优先读取用户配置（%APPDATA%），
/// 读取失败或不存在时回退到程序目录下的默认配置文件。
/// </summary>
public class FileConfigurationStore : IConfigurationStore
{
    /// <summary>
    /// 用户配置文件完整路径（%APPDATA%\{appName}\config.json）。
    /// </summary>
    private readonly string _userConfigPath;

    /// <summary>
    /// 默认配置文件完整路径（程序运行目录下的 config.default.json）。
    /// </summary>
    private readonly string _defaultConfigPath;

    /// <summary>
    /// 可选的输出写入器，用于日志输出（可注入）。
    /// </summary>
    private readonly IOutputWriter? _output; // 可选注入

    /// <summary>
    /// 初始化配置存储，创建用户配置目录并计算用户/默认配置文件路径。
    /// </summary>
    /// <param name="appName">应用名称，用于在 %APPDATA% 下定位配置目录。</param>
    /// <param name="output">可选输出写入器，用于记录日志信息。</param>
    public FileConfigurationStore(string appName, IOutputWriter? output = null)
    {
        _output = output;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDir = Path.Combine(appData, appName);
        _userConfigPath = Path.Combine(appDir, "config.json");
        _defaultConfigPath = Path.Combine(AppContext.BaseDirectory, "config.default.json");
        Directory.CreateDirectory(appDir);
    }

    /// <summary>
    /// 加载应用配置：优先读取用户配置文件，失败或缺失时回退到默认配置文件，
    /// 两者均不可用时返回全新默认配置。
    /// </summary>
    /// <returns>加载到的应用配置。</returns>
    public AppConfig LoadConfig()
    {
        AppConfig? config = null;

        // 1. 尝试读取用户配置文件
        if (File.Exists(_userConfigPath))
        {
            try
            {
                var json = File.ReadAllText(_userConfigPath);
                config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });
            }
            catch { /* 忽略，回退 */ }
        }

        // 2. 用户配置不可用时回退到默认配置文件
        if (config == null && File.Exists(_defaultConfigPath))
        {
            try
            {
                var json = File.ReadAllText(_defaultConfigPath);
                config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });
            }
            catch { }
        }

        // 3. 仍无配置时使用程序内置的默认配置
        config ??= new AppConfig();

        // 检查各 AI 配置中环境变量密钥的可用性（当前仅检查，不修改配置）
        foreach (var kv in config.AIConfigs)
        {
            if (kv.Value.UseApiKeyEnv && !string.IsNullOrEmpty(kv.Value.ApiKeyEnv))
            {
                var success = !string.IsNullOrEmpty(kv.Value.ApiKey);
            }
        }

        return config;
    }

    /// <summary>
    /// 将配置保存到用户配置文件，采用先写临时文件再替换的方式避免写入中断损坏原文件。
    /// </summary>
    /// <param name="config">要保存的应用配置。</param>
    public void SaveConfig(AppConfig config)
    {
        string tempPath = _userConfigPath + ".tmp";
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        File.WriteAllText(tempPath, json);
        // 文件已存在时用 File.Replace 原子替换，否则直接移动临时文件
        if (File.Exists(_userConfigPath))
            File.Replace(tempPath, _userConfigPath, null);
        else
            File.Move(tempPath, _userConfigPath);
    }
}
