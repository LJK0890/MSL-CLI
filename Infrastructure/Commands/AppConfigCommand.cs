using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;
using System.Collections;
using System.Reflection;
using System.Text;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// 应用配置读取/修改的辅助类，支持通过点号路径访问嵌套属性与字典。
/// </summary>
public static class AppConfigHelper
{
    /// <summary>
    /// 通过点号路径设置值，支持属性和字典混合。
    /// 字典键必须已经存在，否则抛出异常。
    /// </summary>
    /// <param name="target">要修改的配置根对象。</param>
    /// <param name="path">点号分隔的属性路径，至少两层（容器.属性）。</param>
    /// <param name="value">要设置的字符串值，将转换为目标属性的类型。</param>
    public static void SetValueByPath(object target, string path, string value)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空", nameof(path));

        var segments = path.Split('.');
        if (segments.Length < 2)
            throw new ArgumentException("路径至少需要两层：容器.属性", nameof(path));

        object current = target;
        Type currentType = current.GetType();

        // 导航到倒数第二层
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string segment = segments[i];

            // ---------- 字典分支 ----------
            if (current is IDictionary dict)
            {
                if (!dict.Contains(segment))
                {
                    // 尝试创建新对象（假设非泛型或泛型）
                    var dictType = current.GetType();
                    var valueType = dictType.GetGenericArguments()[1]; // 获取值类型
                    var newObj = Activator.CreateInstance(valueType);
                    dict.Add(segment, newObj);
                    current = newObj;
                }
                else
                {
                    current = dict[segment];
                }
            }
            // ---------- 属性分支 ----------
            else
            {
                var prop = PropertyCache.GetOrAdd(
                    $"{currentType.FullName}.{segment}",
                    currentType,
                    segment
                );
                if (prop == null)
                    throw new Exception($"类型 '{currentType.FullName}' 中找不到属性 '{segment}'");

                var next = prop.GetValue(current);
                if (next == null)
                {
                    // 中间对象为 null 时尝试自动创建实例
                    try
                    {
                        next = Activator.CreateInstance(prop.PropertyType);
                        prop.SetValue(current, next);
                    }
                    catch
                    {
                        throw new Exception($"无法自动创建中间对象 '{segment}'，请确保其已初始化");
                    }
                }
                current = next;
            }
            currentType = current.GetType();
        }

        // ---------- 最后一层必须是属性（不能是字典键） ----------
        string lastSegment = segments.Last();
        if (current is IDictionary)
            throw new Exception("路径不能以字典键结尾，必须指定具体属性，例如 'AIConfigs.default.Url'");

        var targetProp = PropertyCache.GetOrAdd(
            $"{currentType.FullName}.{lastSegment}",
            currentType,
            lastSegment
        );
        if (targetProp == null)
            throw new Exception($"类型 '{currentType.FullName}' 中找不到属性 '{lastSegment}'");

        // 将字符串值转换为目标属性的类型并写入
        object convertedValue = Convert.ChangeType(value, targetProp.PropertyType);
        targetProp.SetValue(current, convertedValue);
    }

    /// <summary>
    /// 通过点号路径获取值，支持属性和字典混合。
    /// </summary>
    /// <param name="target">要读取的配置根对象。</param>
    /// <param name="path">点号分隔的属性路径。</param>
    /// <returns>路径对应的值；路径上任意一环为 null 时抛出异常。</returns>
    public static object? GetValueByPath(object target, string path)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空", nameof(path));

        var segments = path.Split('.');
        object current = target;
        Type currentType = current.GetType();

        foreach (string segment in segments)
        {
            // ---------- 字典分支 ----------
            if (current is IDictionary dict)
            {
                if (!dict.Contains(segment))
                    throw new Exception($"字典 '{currentType.Name}' 中不包含键 '{segment}'");
                current = dict[segment];
                if (current == null)
                    throw new Exception($"字典键 '{segment}' 的值为 null");
            }
            // ---------- 属性分支 ----------
            else
            {
                var prop = PropertyCache.GetOrAdd(
                    $"{currentType.FullName}.{segment}",
                    currentType,
                    segment
                );
                if (prop == null)
                    throw new Exception($"类型 '{currentType.FullName}' 中找不到属性 '{segment}'");
                current = prop.GetValue(current);
                if (current == null)
                    throw new Exception($"属性 '{segment}' 的值为 null");
            }
            currentType = current.GetType();
        }
        return current;
    }
}

/// <summary>
/// 属性信息 LRU 缓存，限制容量由 AppConfig.MaxPropertyCacheLength 控制。
/// </summary>
public static class PropertyCache
{
    /// <summary>
    /// 属性缓存：键为完整路径，值为属性信息与访问顺序链表节点。
    /// </summary>
    private static readonly Dictionary<string, (PropertyInfo Prop, LinkedListNode<string> Node)> _cache
        = new Dictionary<string, (PropertyInfo, LinkedListNode<string>)>();
    /// <summary>
    /// 访问顺序链表，用于 LRU 淘汰最久未使用的键。
    /// </summary>
    private static readonly LinkedList<string> _accessOrder = new LinkedList<string>();
    /// <summary>
    /// 缓存访问锁。
    /// </summary>
    private static readonly object _lock = new object();

    /// <summary>
    /// 缓存容量上限，由 AppConfig.MaxPropertyCacheLength 控制，未配置时默认 128。
    /// </summary>
    private static int MaxCapacity => AppConfig.MaxPropertyCacheLength > 0
        ? AppConfig.MaxPropertyCacheLength
        : 128;

    /// <summary>
    /// 获取或添加属性元数据，若缓存已满则淘汰最久未使用项。
    /// </summary>
    /// <param name="fullPath">属性的完整点号路径，作为缓存键。</param>
    /// <param name="targetType">要查找属性的类型。</param>
    /// <param name="propertyName">属性名称（不区分大小写）。</param>
    /// <returns>属性信息；找不到时返回 null。</returns>
    public static PropertyInfo GetOrAdd(string fullPath, Type targetType, string propertyName)
    {
        lock (_lock)
        {
            // 命中缓存：更新访问顺序（移至链表末尾）
            if (_cache.TryGetValue(fullPath, out var entry))
            {
                _accessOrder.Remove(entry.Node);
                var newNode = _accessOrder.AddLast(fullPath);
                _cache[fullPath] = (entry.Prop, newNode);
                return entry.Prop;
            }

            var prop = targetType.GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null)
                return null;

            // 缓存已满：淘汰最久未使用的项
            if (_cache.Count >= MaxCapacity)
            {
                var oldestKey = _accessOrder.First;
                if (oldestKey != null)
                {
                    _cache.Remove(oldestKey.Value);
                    _accessOrder.RemoveFirst();
                }
            }

            var node = _accessOrder.AddLast(fullPath);
            _cache[fullPath] = (prop, node);
            return prop;
        }
    }

    /// <summary>
    /// 清空属性缓存。
    /// </summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _accessOrder.Clear();
        }
    }
}

/// <summary>
/// 获取配置值的命令（$appconfigget）。
/// </summary>
public class AppConfigGetCommand : ICommand
{
    /// <summary>
    /// 命令名称：$appconfigget。
    /// </summary>
    public string Name => "$appconfigget";
    /// <summary>
    /// 命令描述：获取配置值。
    /// </summary>
    public string Description => "获取配置值，用法: $appconfigget <路径> 或不带参数获取全部配置";

    /// <summary>
    /// 执行 $appconfigget 命令：无参数时输出全部配置 JSON，否则按路径读取单个配置值。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        try
        {
            var config = args.ConfigStore.LoadConfig();
            var sb = new StringBuilder();

            // 无参数时输出全部配置的 JSON
            if (string.IsNullOrWhiteSpace(args.Raw))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                output?.Write("Command", LogLevel.Success, json);
                return new CommandResult(1, json);
            }
            else
            {
                // 按路径读取单个配置值
                var path = args.Raw.Trim();
                var value = AppConfigHelper.GetValueByPath(config, path);
                if (value == null)
                {
                    var msg = $"路径 '{path}' 不存在或值为 null";
                    output?.Write("Command", LogLevel.Error, msg);
                    return new CommandResult(0, msg);
                }
                else
                {
                    var valueStr = value.ToString() ?? "(null)";
                    var msg = $"{path} : {valueStr}";
                    output?.Write("Command", LogLevel.Success, msg);
                    return new CommandResult(1, msg);
                }
            }
        }
        catch (Exception ex)
        {
            var msg = $"获取失败: {ex.Message}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }
    }
}

/// <summary>
/// $appconfigget 的简写命令（$acg）。
/// </summary>
public class AppConfigGetAliasCommand : ICommand
{
    /// <summary>
    /// 命令名称：$acg。
    /// </summary>
    public string Name => "$acg";
    /// <summary>
    /// 命令描述：$appconfigget 的简写。
    /// </summary>
    public string Description => "$appconfigget 的简写";
    /// <summary>
    /// 内部封装的 AppConfigGetCommand 实例。
    /// </summary>
    private readonly AppConfigGetCommand _inner = new();
    /// <summary>
    /// 执行命令，转发给内部封装的 AppConfigGetCommand。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
        => await _inner.ExecuteAsync(args, output);
}

/// <summary>
/// 获取全部配置的命令（$appconfiggetall），等效于 $appconfigget 无参数。
/// </summary>
public class AppConfigGetAllCommand : ICommand
{
    /// <summary>
    /// 命令名称：$appconfiggetall。
    /// </summary>
    public string Name => "$appconfiggetall";
    /// <summary>
    /// 命令描述：获取全部配置。
    /// </summary>
    public string Description => "获取全部配置（等效于 $appconfigget 无参数）";

    /// <summary>
    /// 内部封装的 AppConfigGetCommand 实例。
    /// </summary>
    private readonly AppConfigGetCommand _inner = new();

    /// <summary>
    /// 执行命令，以空参数调用内部封装的 AppConfigGetCommand 获取全部配置。
    /// </summary>
    /// <param name="args">命令参数，包含运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        // 调用 $appconfigget 不带参数
        var argsCopy = new CommandArgs(string.Empty, args.ServerRegistry, args.AgentService, args.ConfigStore)
        {
            Parser = args.Parser
        };
        return await _inner.ExecuteAsync(argsCopy, output);
    }
}

/// <summary>
/// $appconfiggetall 的简写命令（$acga）。
/// </summary>
public class AppConfigGetAllAliasCommand : ICommand
{
    /// <summary>
    /// 命令名称：$acga。
    /// </summary>
    public string Name => "$acga";
    /// <summary>
    /// 命令描述：$appconfiggetall 的简写。
    /// </summary>
    public string Description => "$appconfiggetall 的简写";
    /// <summary>
    /// 内部封装的 AppConfigGetAllCommand 实例。
    /// </summary>
    private readonly AppConfigGetAllCommand _inner = new();
    /// <summary>
    /// 执行命令，转发给内部封装的 AppConfigGetAllCommand。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
        => await _inner.ExecuteAsync(args, output);
}

/// <summary>
/// 设置配置值的命令（$appconfigset）。
/// </summary>
public class AppConfigSetCommand : ICommand
{
    /// <summary>
    /// 命令名称：$appconfigset。
    /// </summary>
    public string Name => "$appconfigset";
    /// <summary>
    /// 命令描述：设置配置值。
    /// </summary>
    public string Description => "设置配置值，用法: $appconfigset <路径> <值>";

    /// <summary>
    /// 执行 $appconfigset 命令，按路径修改配置并保存。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var msg = "用法: $appconfigset <路径> <值>";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg); 
        }

        var parts = args.Raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            var msg = "参数不足，需要路径和值";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg); 
        }

        string path = parts[0];
        string value = parts[1];

        try
        {
            var config = args.ConfigStore.LoadConfig();
            AppConfigHelper.SetValueByPath(config, path, value);
            // 保存配置
            args.ConfigStore.SaveConfig(config);
            var msg = $"配置已更新: {path} = {value}";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }
        catch (Exception ex)
        {
            var msg = $"设置失败: {ex.Message}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }
    }
}

/// <summary>
/// $appconfigset 的简写命令（$acs）。
/// </summary>
public class AppConfigSetAliasCommand : ICommand
{
    /// <summary>
    /// 命令名称：$acs。
    /// </summary>
    public string Name => "$acs";
    /// <summary>
    /// 命令描述：$appconfigset 的简写。
    /// </summary>
    public string Description => "$appconfigset 的简写";
    /// <summary>
    /// 内部封装的 AppConfigSetCommand 实例。
    /// </summary>
    private readonly AppConfigSetCommand _inner = new();
    /// <summary>
    /// 执行命令，转发给内部封装的 AppConfigSetCommand。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
        => await _inner.ExecuteAsync(args, output);
}

/// <summary>
/// 打印当前配置到控制台的命令（$printconfig），用于调试。
/// </summary>
public class PrintConfigCommand : ICommand
{
    /// <summary>
    /// 命令名称：$printconfig。
    /// </summary>
    public string Name => "$printconfig";
    /// <summary>
    /// 命令描述：打印当前配置到控制台（调试用）。
    /// </summary>
    public string Description => "打印当前配置到控制台（调试用）";

    /// <summary>
    /// 执行 $printconfig 命令，重新加载配置并打印核心字段。
    /// </summary>
    /// <param name="args">命令参数，包含运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        var config = args.ConfigStore.LoadConfig(); // 重新加载以确保最新，或直接使用已加载的
        // 也可以从注入的配置对象获取，但最好重新加载以保证最新。
        // 这里简单获取，假设有 GetConfig 方法
        var sb = new StringBuilder();
        sb.AppendLine("当前配置:");
        sb.AppendLine($"EnableAI: {config.EnableAI}");
        sb.AppendLine("AIConfigs:");
        foreach (var kv in config.AIConfigs)
        {
            sb.AppendLine($"  {kv.Key}:");
            sb.AppendLine($"    Url: {kv.Value.Url}");
            sb.AppendLine($"    Model: {kv.Value.Model}");
            sb.AppendLine($"    ApiKey: {(string.IsNullOrEmpty(kv.Value.ApiKey) ? "(empty)" : "****")}");
            sb.AppendLine($"    UseApiKeyEnv: {kv.Value.UseApiKeyEnv}");
            sb.AppendLine($"    ApiKeyEnv: {kv.Value.ApiKeyEnv ?? "(null)"}");
            sb.AppendLine($"    EnableChat: {kv.Value.EnableChat}");
            sb.AppendLine($"    EnableAgent: {kv.Value.EnableAgent}");
            sb.AppendLine($"    AgentPrompt: {kv.Value.AgentPrompt ?? "(null)"}");
        }
        sb.AppendLine("ServerPaths:");
        foreach (var kv in config.ServerPaths)
            sb.AppendLine($"  {kv.Key}: {kv.Value}");

        output?.Write("Command", LogLevel.Success, sb.ToString());
        return new CommandResult(1, sb.ToString());
    }
}
