using MSL_CLI.Core.Ports;

namespace MSL_CLI.Core.Domain;

/// <summary>
/// 服务器 server.properties 配置文件的封装，支持键值对的读取、修改与原子保存。
/// </summary>
public class ServerProperties
{
    // 已加载的属性键值对缓存
    private readonly Dictionary<string, string> _properties = new();
    // 已被显式删除、需要从文件中移除的键
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    // 配置文件路径
    private readonly string _filePath;
    // 输出写入器
    private readonly IOutputWriter _output;

    /// <summary>
    /// 初始化 ServerProperties 实例，并立即加载配置文件。
    /// </summary>
    /// <param name="filePath">server.properties 配置文件路径。</param>
    /// <param name="output">输出写入器。</param>
    public ServerProperties(string filePath, IOutputWriter output)
    {
        _filePath = filePath;
        _output = output;
        Load();
    }

    /// <summary>
    /// 从配置文件加载全部键值对到内存缓存。
    /// </summary>
    private void Load()
    {
        // 配置文件不存在时直接返回
        if (!File.Exists(_filePath)) return;
        foreach (var line in File.ReadLines(_filePath))
        {
            var trimmed = line.Trim();
            // 跳过空行与 # 开头的注释行
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            var eq = trimmed.IndexOf('=');
            // 仅处理包含等号的行，拆分为键值对
            if (eq > 0)
            {
                var key = trimmed.Substring(0, eq).Trim();
                var value = trimmed.Substring(eq + 1).Trim();
                _properties[key] = value;
            }
        }
    }

    /// <summary>
    /// 获取指定键的属性值。
    /// </summary>
    /// <param name="key">属性键。</param>
    /// <returns>属性值；键不存在时返回 null。</returns>
    public string? GetValue(string key) => _properties.TryGetValue(key, out var v) ? v : null;
    /// <summary>
    /// 获取指定键的属性值，键不存在时返回提供的默认值。
    /// </summary>
    /// <param name="key">属性键。</param>
    /// <param name="defaultValue">默认值。</param>
    /// <returns>属性值或默认值。</returns>
    public string GetValue(string key, string defaultValue) => GetValue(key) ?? defaultValue;
    /// <summary>
    /// 设置指定键的属性值，并立即保存到配置文件。
    /// </summary>
    /// <param name="key">属性键。</param>
    /// <param name="value">属性值。</param>
    public void SetValue(string key, string value) { _properties[key] = value; _removed.Remove(key); Save(); }

    /// <summary>
    /// 删除指定键并立即保存到配置文件。
    /// </summary>
    /// <param name="key">要删除的属性键。</param>
    /// <returns>键原先存在并被删除时返回 true；键不存在时返回 false。</returns>
    public bool TryRemoveValue(string key)
    {
        var existed = _properties.Remove(key);
        if (!existed) return false;

        // 记录待删除键：文件中对应的行会在 Save 时被剔除
        _removed.Add(key);
        Save();
        return true;
    }

    /// <summary>
    /// 将内存中的键值对写回配置文件。
    /// </summary>
    private void Save()
    {
        // 原子写入
        string tempPath = _filePath + ".tmp";
        var lines = File.Exists(_filePath) ? File.ReadAllLines(_filePath).ToList() : new List<string>();
        var updated = new HashSet<string>();
        var kept = new List<string>(lines.Count);

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            var eq = trimmed.IndexOf('=');
            if (eq > 0 && !trimmed.StartsWith('#'))
            {
                var key = trimmed.Substring(0, eq).Trim();

                // 已删除的键直接丢弃该行
                if (_removed.Contains(key)) continue;

                // 文件中已存在的键直接覆盖其行内容
                if (_properties.ContainsKey(key))
                {
                    kept.Add($"{key}={_properties[key]}");
                    updated.Add(key);
                    continue;
                }
            }

            // 注释、空行以及不再维护的键原样保留
            kept.Add(lines[i]);
        }

        // 追加文件中不存在的新键值行
        foreach (var kv in _properties.Where(kv => !updated.Contains(kv.Key)))
            kept.Add($"{kv.Key}={kv.Value}");

        File.WriteAllLines(tempPath, kept);
        // 文件已存在时用 Replace 原子替换，避免写入中断损坏原文件
        if (File.Exists(_filePath))
            File.Replace(tempPath, _filePath, null);
        else
            File.Move(tempPath, _filePath);

        // 删除已落盘，清空待删除集合
        _removed.Clear();
    }

    /// <summary>
    /// 是否启用了查询（enable-query）功能。
    /// </summary>
    public bool EnableQuery => GetValue("enable-query", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// 查询端口号；未配置 query.port 时回退到 server-port。
    /// </summary>
    public int QueryPort => int.TryParse(GetValue("query.port"), out var p) ? p : int.Parse(GetValue("server-port", "25565"));
    /// <summary>
    /// 当前已加载的全部属性键值对。
    /// </summary>
    public IReadOnlyDictionary<string, string> Entries => _properties;
}
