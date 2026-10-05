namespace MSL_CLI.Core.Domain;

/// <summary>
/// 应用全局配置模型，保存 AI 功能开关、AI 服务配置与服务器路径映射等顶层配置。
/// </summary>
public class AppConfig
{
    /// <summary>
    /// 是否启用 AI 功能。
    /// </summary>
    public bool EnableAI { get; set; } = true;
    /// <summary>
    /// AI 服务配置字典，键为配置名称，值为对应的 AI 配置。
    /// </summary>
    public Dictionary<string, AIConfig> AIConfigs { get; set; } = new();
    /// <summary>
    /// 服务器路径映射字典，键为服务器名称，值为服务器所在目录路径。
    /// </summary>
    public Dictionary<string, string> ServerPaths { get; set; } = new();
    /// <summary>
    /// 属性信息 LRU 缓存的最大容量，小于等于 0 时回退为默认值 128。
    /// </summary>
    public static int MaxPropertyCacheLength { get; set; } = 128;
    /// <summary>
    /// 默认使用的 AI 配置名称，未显式指定 AI 配置时生效。
    /// </summary>
    public string DefaultAIConfig { get; set; } = string.Empty;
    /// <summary>
    /// 代理命令授权配置，保存“总是允许”白名单与强制逐次询问的命令列表。
    /// </summary>
    public AgentPermissions AgentPermissions { get; set; } = new();
}
