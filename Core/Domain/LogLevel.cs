namespace MSL_CLI.Core.Domain;

/// <summary>
/// 日志级别枚举。
/// </summary>
public enum LogLevel
{
    /// <summary>
    /// 调试信息，用于开发排查。
    /// </summary>
    Debug,
    /// <summary>
    /// 普通信息。
    /// </summary>
    Info,
    /// <summary>
    /// 成功提示信息。
    /// </summary>
    Success,
    /// <summary>
    /// 警告信息。
    /// </summary>
    Warning,
    /// <summary>
    /// 错误信息。
    /// </summary>
    Error,
    /// <summary>
    /// 严重错误。
    /// </summary>
    Critical,
    /// <summary>
    /// 致命错误。
    /// </summary>
    Fatal
}
