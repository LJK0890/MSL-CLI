using System.Text.Json;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 基于 JSON 文件的应用配置存储实现，优先读取用户配置（%APPDATA%），
/// 读取失败或不存在时回退到程序目录下的默认配置文件。
/// 加载结果会被规范化（补齐 null 集合与 null 配置项），避免损坏的字段导致运行时异常；
/// 保存采用“先备份、再写临时文件、最后原子替换”的流程，降低配置丢失风险。
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
    /// 最近一次加载过程中发生的错误描述；为 null 表示加载成功（或文件不存在）。
    /// </summary>
    public string? LastLoadError { get; private set; }

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
    /// 反序列化配置时使用的 JSON 选项。
    /// </summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// 序列化配置时使用的 JSON 选项。
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 加载应用配置：优先读取用户配置文件，失败或缺失时回退到默认配置文件，
    /// 两者均不可用时返回全新默认配置。
    /// </summary>
    /// <returns>加载到的应用配置；其结果始终已被规范化。</returns>
    public AppConfig LoadConfig()
    {
        LastLoadError = null;
        AppConfig? config = null;

        // 1. 尝试读取用户配置文件
        if (File.Exists(_userConfigPath))
        {
            try
            {
                var json = File.ReadAllText(_userConfigPath);
                config = JsonSerializer.Deserialize<AppConfig>(json, ReadOptions);

                // 反序列化返回 null 说明文件内容为字面量 null，按损坏处理
                if (config == null)
                    RecordLoadError("用户配置文件内容为 null", new JsonException("配置根对象为 null"));
            }
            catch (JsonException ex)
            {
                // JSON 语法或类型错误：记录错误并尝试回退，不静默丢弃
                RecordLoadError("用户配置文件解析失败", ex);
            }
            catch (Exception ex)
            {
                RecordLoadError("用户配置文件读取失败", ex);
            }
        }

        // 2. 用户配置不可用时回退到默认配置文件
        if (config == null && File.Exists(_defaultConfigPath))
        {
            try
            {
                var json = File.ReadAllText(_defaultConfigPath);
                config = JsonSerializer.Deserialize<AppConfig>(json, ReadOptions);
                if (config != null)
                    _output?.Write("Config", LogLevel.Warning, $"已回退到默认配置文件: {_defaultConfigPath}");
            }
            catch (Exception ex)
            {
                RecordLoadError("默认配置文件读取失败", ex);
            }
        }

        // 3. 仍无配置时使用程序内置的默认配置
        config ??= new AppConfig();

        // 4. 规范化：补齐 null 集合与 null 配置项，避免后续访问抛 NullReferenceException
        Normalize(config);

        return config;
    }

    /// <summary>
    /// 记录加载错误并通过输出写入器提示用户，便于发现配置损坏。
    /// </summary>
    /// <param name="context">错误场景描述。</param>
    /// <param name="ex">捕获到的异常。</param>
    private void RecordLoadError(string context, Exception ex)
    {
        LastLoadError = $"{context}: {ex.Message}";
        try
        {
            _output?.Write("Config", LogLevel.Error,
                $"{context}: {ex.Message}（已回退到默认配置，原文件未被修改；如需恢复请检查 {_userConfigPath}）");
        }
        catch
        {
            // 日志输出失败不应中断配置加载
        }
    }

    /// <summary>
    /// 规范化配置对象：将 null 的集合与嵌套对象替换为安全默认值，
    /// 并剔除字典中的 null 配置项，保证后续代码无需重复判空。
    /// </summary>
    /// <param name="config">要规范化的配置对象。</param>
    private static void Normalize(AppConfig config)
    {
        config.AIConfigs ??= new Dictionary<string, AIConfig>();

        // 剔除值为 null 的 AI 配置项，避免迭代时解引用 null
        foreach (var key in config.AIConfigs.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList())
        {
            config.AIConfigs.Remove(key);
        }

        config.ServerPaths ??= new Dictionary<string, string>();

        // 剔除键或值为 null 的服务器路径项，避免后续路径拼接抛异常
        foreach (var key in config.ServerPaths
                     .Where(kv => kv.Key == null || kv.Value == null)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            config.ServerPaths.Remove(key!);
        }

        config.AgentPermissions ??= new AgentPermissions();
        config.AgentPermissions.AllowList ??= new List<string>();
        config.AgentPermissions.AlwaysAskCommands ??= new List<string>();

        // 先剔除空白项，避免无效条目影响后续判断
        config.AgentPermissions.AllowList.RemoveAll(string.IsNullOrWhiteSpace);
        config.AgentPermissions.AlwaysAskCommands.RemoveAll(string.IsNullOrWhiteSpace);

        // $exec 是始终必须人工确认的底线，任何配置都不能把它移出强制询问列表
        if (!config.AgentPermissions.AlwaysAskCommands.Any(
                c => string.Equals(c.Trim(), "$exec", StringComparison.OrdinalIgnoreCase)))
        {
            config.AgentPermissions.AlwaysAskCommands.Add("$exec");
        }

        config.DefaultAIConfig ??= string.Empty;
    }

    /// <summary>
    /// 从最近一次保存前生成的备份恢复用户配置。
    /// 用于配置文件被外部改坏后回滚到上一个可用状态。
    /// </summary>
    /// <returns>存在备份并成功恢复时返回 true；不存在备份时返回 false。</returns>
    public bool RestoreFromBackup()
    {
        var backupPath = _userConfigPath + ".bak";
        if (!File.Exists(backupPath))
            return false;

        // 先把当前（可能已损坏的）文件另存为 .corrupt，便于事后排查
        if (File.Exists(_userConfigPath))
            File.Copy(_userConfigPath, _userConfigPath + ".corrupt", overwrite: true);

        File.Copy(backupPath, _userConfigPath, overwrite: true);
        _output?.Write("Config", LogLevel.Success, $"已从备份恢复配置: {backupPath}");
        return true;
    }

    /// <summary>
    /// 将配置保存到用户配置文件，采用先备份、再写临时文件、最后原子替换的流程，
    /// 避免写入中断损坏原文件，同时在覆盖前保留一份可回滚的备份。
    /// </summary>
    /// <param name="config">要保存的应用配置。</param>
    public void SaveConfig(AppConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));

        // 保存前规范化，防止把 null 集合写入文件
        Normalize(config);

        var json = JsonSerializer.Serialize(config, WriteOptions);
        var tempPath = _userConfigPath + ".tmp";

        // 1. 写入临时文件（同一目录，保证后续替换是原子操作）
        File.WriteAllText(tempPath, json);

        try
        {
            if (File.Exists(_userConfigPath))
            {
                // 2. 覆盖前先备份现有配置，便于用户回滚
                File.Copy(_userConfigPath, _userConfigPath + ".bak", overwrite: true);

                // 3. 原子替换：目标存在时必须提供备份文件名参数，实际备份用 File.Copy 完成
                File.Replace(tempPath, _userConfigPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                // 首次保存直接落盘
                File.Move(tempPath, _userConfigPath);
            }
        }
        catch
        {
            // 写入失败时清理临时文件，避免残留干扰下次保存
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }
}
