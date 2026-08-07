using System;
using System.Collections.Generic;
using System.Reflection;
using MSL_CLI.Config;

namespace MSL_CLI.Commands;

/// <summary>
/// 属性信息 LRU 缓存，限制容量由 AppConfig.MaxPropertyCacheLength 控制。
/// </summary>
internal static class PropertyCache
{
    private static readonly Dictionary<string, (PropertyInfo Prop, LinkedListNode<string> Node)> _cache
        = new Dictionary<string, (PropertyInfo, LinkedListNode<string>)>();
    private static readonly LinkedList<string> _accessOrder = new LinkedList<string>();
    private static readonly object _lock = new object();

    private static int MaxCapacity => AppConfig.MaxPropertyCacheLength > 0
        ? AppConfig.MaxPropertyCacheLength
        : 100;

    /// <summary>
    /// 获取或添加属性元数据，若缓存已满则淘汰最久未使用项。
    /// </summary>
    public static PropertyInfo GetOrAdd(string fullPath, Type targetType, string propertyName)
    {
        lock (_lock)
        {
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

    public static void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _accessOrder.Clear();
        }
    }
}