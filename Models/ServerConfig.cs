using System.Text.RegularExpressions;

namespace MSL_CLI.Models;

internal class ServerConfig
{
    private readonly Dictionary<string, string> _properties = new();
    private readonly string _filePath;

    private static readonly Regex _mcRegex = new(
        @"^\s*([^#=]+)\s*=\s*(.*)",
        RegexOptions.Compiled
    );

    public ServerConfig(string filePath)
    {
        _filePath = filePath;
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"server.properties 不存在: {filePath}");
        }

        Load();
    }

    private void Load()
    {
        _properties.Clear();
        foreach (var line in File.ReadLines(_filePath))
        {
            var trimmed = line.Trim();

            // 跳过空行和注释行（Minecraft 只用 # 注释）
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

    public string? GetValue(string key)
    {
        _properties.TryGetValue(key, out var value);
        return value;
    }

    public string GetValue(string key, string defaultValue)
    {
        return _properties.TryGetValue(key, out var value) ? value : defaultValue;
    }

    public bool ContainsKey(string key) => _properties.ContainsKey(key);

    public void SetValue(string key, string value)
    {
        _properties[key] = value;
        Save();
    }

    private void Save()
    {
        // 读取原文件保留注释和空行格式
        var lines = File.ReadAllLines(_filePath).ToList();
        var updatedKeys = new HashSet<string>();

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

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

        // 追加新增的配置项
        foreach (var kvp in _properties)
        {
            if (!updatedKeys.Contains(kvp.Key))
            {
                lines.Add($"{kvp.Key}={kvp.Value}");
            }
        }

        File.WriteAllLines(_filePath, lines);
    }
}