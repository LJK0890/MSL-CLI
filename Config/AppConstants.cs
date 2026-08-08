using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSL_CLI.Config;

/// <summary>
/// 应用程序常量定义，包括文件名、JSON 序列化选项等。
/// </summary>
public static class AppConstants
{
    /// <summary>应用名称，用于配置文件夹</summary>
    public static readonly string AppName = "MSL_CLI";
    /// <summary>用户配置文件名</summary>
    public static readonly string ConfigFileName = "config.json";
    /// <summary>默认配置文件名（随程序分发）</summary>
    public static readonly string DefaultConfigFileName = "config.default.json";

    /// <summary>序列化选项：缩进、忽略 null、宽松转义</summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>反序列化选项：不区分大小写、允许尾随逗号、忽略注释</summary>
    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}