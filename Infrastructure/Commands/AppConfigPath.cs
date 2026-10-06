using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MSL_CLI.Core.Domain;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// 应用配置的点号路径读写辅助类，供 $app cfg 与 $ai cfg 共用。
/// 支持属性与字典混合嵌套，例如 AIConfigs.default.Url。
/// </summary>
internal static class AppConfigPath
{
    /// <summary>格式化复杂配置值时使用的 JSON 选项。</summary>
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// 把配置值格式化为可读文本：字符串、布尔与数字原样输出，集合与复杂对象输出缩进 JSON，
    /// 避免打印出 <c>System.Collections.Generic.Dictionary`2[…]</c> 或类型全名。
    /// </summary>
    /// <param name="value">配置路径对应的值。</param>
    /// <returns>可直接打印的文本。</returns>
    public static string FormatValue(object? value)
    {
        switch (value)
        {
            case null:
                return "(null)";
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case int or long or short or byte or uint or ulong or ushort or sbyte or float or double or decimal:
                return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            default:
                return JsonSerializer.Serialize(value, IndentedJson);
        }
    }

    /// <summary>
    /// 通过点号路径设置值，支持属性与字典混合。路径中间缺失的字典键会自动创建。
    /// 单段路径表示直接设置配置根对象的属性，例如 DefaultAIConfig、EnableAI。
    /// </summary>
    /// <param name="target">要修改的配置根对象。</param>
    /// <param name="path">点号分隔的属性路径。</param>
    /// <param name="value">要设置的字符串值，将转换为目标属性的类型。</param>
    public static void SetValueByPath(object target, string path, string value)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空", nameof(path));

        var segments = path.Split('.');
        object current = target;
        var currentType = current.GetType();

        // 导航到倒数第二层
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];

            if (current is IDictionary dict)
            {
                // 字典分支：键不存在时按值类型自动创建对象
                if (!dict.Contains(segment))
                {
                    var valueType = current.GetType().GetGenericArguments()[1];
                    var newObj = Activator.CreateInstance(valueType);
                    dict.Add(segment, newObj);
                    current = newObj!;
                }
                else
                {
                    current = dict[segment]!;
                }
            }
            else
            {
                // 属性分支：中间对象为 null 时尝试自动创建实例
                var prop = PropertyCache.GetOrAdd($"{currentType.FullName}.{segment}", currentType, segment);
                if (prop == null)
                    throw new Exception($"类型 '{currentType.FullName}' 中找不到属性 '{segment}'");

                var next = prop.GetValue(current);
                if (next == null)
                {
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
                current = next!;
            }
            currentType = current.GetType();
        }

        // 最后一层：既可以是属性，也可以是字典键（如 ServerPaths.tga2、AIConfigs.x.Url 中的 Url 为属性）
        var lastSegment = segments.Last();

        if (current is IDictionary targetDict)
        {
            // 字典条目：按字典值类型转换后写入，实现“新增/覆盖”条目
            var args = targetDict.GetType().GetGenericArguments();
            var valueType = args.Length > 1 ? args[1] : typeof(object);

            object? converted;
            try
            {
                converted = Convert.ChangeType(value, valueType, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                throw new Exception($"值 '{value}' 无法转换为 '{valueType.Name}': {ex.Message}");
            }

            targetDict[lastSegment] = converted;
            return;
        }

        var targetProp = PropertyCache.GetOrAdd($"{currentType.FullName}.{lastSegment}", currentType, lastSegment);
        if (targetProp == null)
            throw new Exception($"类型 '{currentType.FullName}' 中找不到属性 '{lastSegment}'");

        var convertedValue = Convert.ChangeType(value, targetProp.PropertyType, CultureInfo.InvariantCulture);
        targetProp.SetValue(current, convertedValue);
    }

    /// <summary>
    /// 通过点号路径获取值，支持属性与字典混合。
    /// </summary>
    /// <param name="target">要读取的配置根对象。</param>
    /// <param name="path">点号分隔的属性路径。</param>
    /// <returns>路径对应的值；路径上任意一环缺失时抛出异常。</returns>
    public static object? GetValueByPath(object target, string path)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空", nameof(path));

        var segments = path.Split('.');
        object current = target;
        var currentType = current.GetType();

        foreach (var segment in segments)
        {
            if (current is IDictionary dict)
            {
                if (!dict.Contains(segment))
                    throw new Exception($"字典 '{currentType.Name}' 中不包含键 '{segment}'");
                current = dict[segment]!;
                if (current == null)
                    throw new Exception($"字典键 '{segment}' 的值为 null");
            }
            else
            {
                var prop = PropertyCache.GetOrAdd($"{currentType.FullName}.{segment}", currentType, segment);
                if (prop == null)
                    throw new Exception($"类型 '{currentType.FullName}' 中找不到属性 '{segment}'");
                current = prop.GetValue(current)!;
                if (current == null)
                    throw new Exception($"属性 '{segment}' 的值为 null");
            }
            currentType = current.GetType();
        }

        return current;
    }

    /// <summary>
    /// 通过点号路径删除配置项。仅支持删除字典条目（例如 AIConfigs.myserver、
    /// ServerPaths.tga）；属性本身无法删除，只能通过 <see cref="SetValueByPath"/> 改值。
    /// </summary>
    /// <param name="target">要修改的配置根对象。</param>
    /// <param name="path">点号分隔的路径，最后一段是字典键。</param>
    /// <returns>键原先存在并被删除时返回 true；键不存在时返回 false。</returns>
    public static bool RemoveByPath(object target, string path)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空", nameof(path));

        var segments = path.Split('.');
        object current = target;
        var currentType = current.GetType();

        // 导航到倒数第二层
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (current is IDictionary dict)
            {
                if (!dict.Contains(segment))
                    throw new Exception($"字典 '{currentType.Name}' 中不包含键 '{segment}'");
                current = dict[segment]!;
            }
            else
            {
                var prop = PropertyCache.GetOrAdd($"{currentType.FullName}.{segment}", currentType, segment);
                if (prop == null)
                    throw new Exception($"类型 '{currentType.FullName}' 中找不到属性 '{segment}'");
                current = prop.GetValue(current)!;
            }

            if (current == null)
                throw new Exception($"路径 '{string.Join(".", segments.Take(i + 1))}' 的值为 null");

            currentType = current.GetType();
        }

        var last = segments.Last();

        // 只有字典条目可以删除
        if (current is IDictionary targetDict)
        {
            if (!targetDict.Contains(last))
                throw new Exception($"字典 '{currentType.Name}' 中不包含键 '{last}'，无法删除");

            targetDict.Remove(last);
            return true;
        }

        // 属性不可删除，给出明确提示而不是静默失败
        var targetProp = PropertyCache.GetOrAdd($"{currentType.FullName}.{last}", currentType, last);
        if (targetProp != null)
            throw new Exception($"'{last}' 是属性而非字典条目，无法删除；如需清空请使用 set 赋新值");

        throw new Exception($"路径 '{path}' 不存在，无法删除");
    }
}

/// <summary>
/// 属性信息 LRU 缓存，用于避免每次都反射查找属性。
/// </summary>
internal static class PropertyCache
{
    /// <summary>缓存容量上限；属性元数据数量很少，128 足够容纳全部配置路径。</summary>
    private const int MaxCapacity = 128;

    /// <summary>缓存项：属性信息与其在访问顺序链表中的节点。</summary>
    private static readonly Dictionary<string, (PropertyInfo Prop, LinkedListNode<string> Node)> _cache = new();
    /// <summary>访问顺序链表，用于淘汰最久未使用的键。</summary>
    private static readonly LinkedList<string> _accessOrder = new();
    /// <summary>缓存访问锁。</summary>
    private static readonly object _lock = new();

    /// <summary>
    /// 获取或添加属性元数据，缓存已满时淘汰最久未使用项。
    /// </summary>
    /// <param name="fullPath">属性的完整点号路径，作为缓存键。</param>
    /// <param name="targetType">要查找属性的类型。</param>
    /// <param name="propertyName">属性名称（不区分大小写）。</param>
    /// <returns>属性信息；找不到时返回 null。</returns>
    public static PropertyInfo? GetOrAdd(string fullPath, Type targetType, string propertyName)
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
