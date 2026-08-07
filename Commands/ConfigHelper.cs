using MSL_CLI.IO;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MSL_CLI.Commands;

public static class ConfigHelper
{
    /// <summary>
    /// 通过点号路径设置值，支持属性和字典混合。
    /// 字典键必须已经存在，否则抛出异常。
    /// </summary>
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

        object convertedValue = Convert.ChangeType(value, targetProp.PropertyType);
        targetProp.SetValue(current, convertedValue);
    }

    /// <summary>
    /// 通过点号路径获取值，支持属性和字典混合。
    /// </summary>
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