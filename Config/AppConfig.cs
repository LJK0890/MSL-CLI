namespace MSL_CLI.Config;

/// <summary>
/// 应用程序整体配置，包含 AI 配置和多个服务器路径。
/// </summary>
public class AppConfig
{
    /// <summary>是否启用 AI 功能（全局开关）</summary>
    public bool EnableAI { get; set; } = true;

    /// <summary>所有 AI 配置字典，键为配置名称（如 "default", "azure" 等）</summary>
    public Dictionary<string, AIConfig> AIConfigs { get; set; } = new();

    /// <summary>服务器路径字典，键为服务器名称，值为服务器根目录路径</summary>
    public Dictionary<string, string> ServerPaths { get; set; } = new();

    /// <summary>最大属性缓存长度，超过该长度的属性将不会被缓存。</summary>
    public static int MaxPropertyCacheLength { get; set; } = 100;
}