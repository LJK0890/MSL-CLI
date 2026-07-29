using static MSL_CLI.IO.IO;
using System.Text.RegularExpressions;

namespace MSL_CLI.Models;

/// <summary>
/// 处理 server.properties 文件的读取、写入和键值操作。
/// </summary>
internal class ServerConfig
{
    private readonly Dictionary<string, string> _properties = new();
    private readonly string _filePath;
    private readonly string name;

    private static readonly Regex _mcRegex = new(
        @"^\s*([^#=]+)\s*=\s*(.*)",
        RegexOptions.Compiled
    );

    /// <summary>
    /// 构造函数，加载指定路径的 server.properties 文件。
    /// </summary>
    /// <param name="filePath">server.properties 文件的完整路径</param>
    /// <exception cref="FileNotFoundException">文件不存在时抛出</exception>
    public ServerConfig(string name, string filePath)
    {
        this.name = name;
        _filePath = filePath;
        if (!File.Exists(filePath))
        {
            Output.Print($"{name}/Config", LogLevel.ERROR, $"server.properties 不存在: {filePath}", includeTimestamp: true);
        }

        Load();
        Output.Print($"{name}/Config", LogLevel.INFO, $"成功加载 server.properties，共 {_properties.Count} 个属性。", includeTimestamp: true);
    }

    /// <summary>
    /// 从文件重新加载所有属性。
    /// </summary>
    private void Load()
    {
        _properties.Clear();
        foreach (var line in File.ReadLines(_filePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            var match = _mcRegex.Match(line);
            if (match.Success)
            {
                var key = match.Groups[1].Value.Trim();
                var value = match.Groups[2].Value.Trim();
                _properties[key] = value;
            }
        }
    }

    /// <summary>
    /// 获取指定键的值，若不存在则返回 null。
    /// </summary>
    public string? GetValue(string key)
    {
        _properties.TryGetValue(key, out var value);
        return value;
    }

    /// <summary>
    /// 获取指定键的值，若不存在则返回默认值。
    /// </summary>
    public string GetValue(string key, string defaultValue)
    {
        return _properties.TryGetValue(key, out var value) ? value : defaultValue;
    }

    /// <summary>
    /// 判断键是否存在。
    /// </summary>
    public bool ContainsKey(string key) => _properties.ContainsKey(key);

    /// <summary>
    /// 设置键值并保存到文件。
    /// </summary>
    public void SetValue(string key, string value)
    {
        _properties[key] = value;
        Save();
        Output.Print($"{name}/Config", LogLevel.INFO, $"设置键 '{key}' = '{value}' 并保存。", includeTimestamp: true);
    }

    /// <summary>
    /// 将当前内存中的属性写回文件，采用原子替换（先写临时文件再替换）。
    /// </summary>
    private void Save()
    {
        var lines = File.ReadAllLines(_filePath).ToList();
        var updatedKeys = new HashSet<string>();

        // 更新已存在的键
        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            var match = _mcRegex.Match(lines[i]);
            if (match.Success)
            {
                var key = match.Groups[1].Value.Trim();
                if (_properties.ContainsKey(key))
                {
                    lines[i] = $"{key}={_properties[key]}";
                    updatedKeys.Add(key);
                }
            }
        }
        // 追加新增的键
        foreach (var kvp in _properties)
        {
            if (!updatedKeys.Contains(kvp.Key))
            {
                lines.Add($"{kvp.Key}={kvp.Value}");
            }
        }

        string tempFilePath = _filePath + ".tmp";
        try
        {
            File.WriteAllLines(tempFilePath, lines);
            if (File.Exists(_filePath))
            {
                File.Replace(tempFilePath, _filePath, null);
            }
            else
            {
                File.Move(tempFilePath, _filePath);
            }
            Output.Print($"{name}/Config", LogLevel.INFO, $"成功保存 server.properties 到 {_filePath}", includeTimestamp: true);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); } catch { /* 忽略删除失败 */ }
            }
        }
    }
}