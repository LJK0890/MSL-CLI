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
    /// 保护配置文件写入的锁，避免并发保存互相覆盖同一个临时文件。
    /// </summary>
    private readonly Lock _saveLock = new();

    /// <summary>
    /// 最近一次加载过程中发生的错误描述；为 null 表示加载成功（或文件不存在）。
    /// </summary>
    public string? LastLoadError { get; private set; }

    /// <summary>
    /// 用户配置文件的完整路径（%APPDATA%\{appName}\config.json）。
    /// 用于在启动时明确告知操作员本次运行实际读写的是哪一份配置。
    /// </summary>
    public string UserConfigPath => _userConfigPath;

    /// <summary>
    /// 默认配置模板的完整路径（程序运行目录下的 config.default.json）。
    /// </summary>
    public string DefaultConfigPath => _defaultConfigPath;

    /// <summary>
    /// 最近一次 LoadConfig 的实际数据来源。
    /// </summary>
    public string LastLoadSource { get; private set; } = "（尚未加载）";

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
        // 每次从磁盘读取，保证外部编辑能被立即看到（$app reload / $app cfg get 都依赖这一点）。
        // 注意：不要在这里做实例缓存——多个 AppConfig 副本并存时，退出时的整份保存
        // 会用陈旧副本覆盖掉会话中新增的内容。
        return ReadFromDisk();
    }

    /// <summary>
    /// 从磁盘读取配置（用户配置 → 默认模板 → 内置默认值），并完成规范化。
    /// </summary>
    /// <returns>读取到的配置对象。</returns>
    private AppConfig ReadFromDisk()
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
                else
                    LastLoadSource = _userConfigPath;
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
                {
                    LastLoadSource = _defaultConfigPath + "（默认模板回退）";
                    _output?.Write("Config", LogLevel.Warning, $"已回退到默认配置文件: {_defaultConfigPath}");
                }
            }
            catch (Exception ex)
            {
                RecordLoadError("默认配置文件读取失败", ex);
            }
        }

        // 3. 仍无配置时使用程序内置的默认配置
        if (config == null)
        {
            LastLoadSource = "程序内置默认值";
            config = new AppConfig();
        }

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

        // 锁定 / 隐藏列表：去空白、去重，并丢弃已不存在的服务器名，
        // 避免残留条目把“重新登记的同名服务器”意外锁住或隐藏
        config.LockedServers = NormalizeServerNames(config.LockedServers, config.ServerPaths);
        config.HiddenServers = NormalizeServerNames(config.HiddenServers, config.ServerPaths);

        config.AgentPermissions ??= new AgentPermissions();
        config.AgentPermissions.AllowList ??= new List<string>();
        config.AgentPermissions.AlwaysAskCommands ??= new List<string>();

        // 先统一规范化（去首尾空白、合并内部连续空白）并去重，
        // 使 " $server   ck op " 与 "$server ck op" 被当作同一条目
        config.AgentPermissions.AllowList = NormalizeEntries(config.AgentPermissions.AllowList);
        config.AgentPermissions.AlwaysAskCommands = NormalizeEntries(config.AgentPermissions.AlwaysAskCommands);

        // 历史条目迁移：$exec 已搬到 $app exec，旧配置里残留的条目自动改写
        for (var i = 0; i < config.AgentPermissions.AlwaysAskCommands.Count; i++)
        {
            if (string.Equals(config.AgentPermissions.AlwaysAskCommands[i], "$exec", StringComparison.OrdinalIgnoreCase))
                config.AgentPermissions.AlwaysAskCommands[i] = "$app exec";
        }

        config.AgentPermissions.AlwaysAskCommands =
            NormalizeEntries(config.AgentPermissions.AlwaysAskCommands);

        // 底线条目（执行系统命令、删除服务器目录）任何配置都不能把它们移出强制询问列表
        foreach (var forced in AgentPermissions.ForcedAlwaysAsk)
        {
            if (!config.AgentPermissions.AlwaysAskCommands.Contains(forced, StringComparer.OrdinalIgnoreCase))
                config.AgentPermissions.AlwaysAskCommands.Add(forced);
        }

        config.DefaultAIConfig ??= string.Empty;
    }

    /// <summary>
    /// 规范化“服务器名列表”（锁定 / 隐藏）：去空白、忽略大小写去重，
    /// 并丢弃已不在 <paramref name="serverPaths"/> 中的名字。
    /// </summary>
    /// <param name="items">原始列表，可为 null。</param>
    /// <param name="serverPaths">当前已登记的服务器路径表。</param>
    /// <returns>规范化后的新列表。</returns>
    private static List<string> NormalizeServerNames(List<string>? items, Dictionary<string, string> serverPaths)
        => (items ?? new List<string>())
            .Select(s => s?.Trim() ?? string.Empty)
            // 名称比较忽略大小写：手写的 "Survival" 也要能匹配 ServerPaths 里的 "survival"
            .Where(s => s.Length > 0 &&
                        serverPaths.Keys.Any(k => string.Equals(k, s, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 规范化字符串列表：逐项去首尾空白并合并内部连续空白，剔除空项并按忽略大小写去重。
    /// </summary>
    /// <param name="items">原始列表。</param>
    /// <returns>规范化后的新列表。</returns>
    private static List<string> NormalizeEntries(List<string> items)
        => items.Select(AgentPermissions.Normalize)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

        lock (_saveLock)
        {
            // 保存前规范化，防止把 null 集合写入文件
            Normalize(config);

            var json = JsonSerializer.Serialize(config, WriteOptions);
            // 临时文件名必须唯一：控制台命令与代理授权可能并发保存，
            // 共用固定的 .tmp 会让两个线程互相覆盖/删除对方的临时文件
            var tempPath = $"{_userConfigPath}.{Guid.NewGuid():N}.tmp";

            try
            {
                // 1. 写入临时文件（同一目录，保证后续替换是原子操作）
                File.WriteAllText(tempPath, json);

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
}
