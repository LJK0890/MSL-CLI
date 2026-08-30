namespace MSL_CLI.Core.Domain;

/// <summary>
/// 服务器运行状态枚举。
/// </summary>
public enum ServerStatus
{
    /// <summary>
    /// 服务器已停止。
    /// </summary>
    Stopped,
    /// <summary>
    /// 服务器正在启动中。
    /// </summary>
    Starting,
    /// <summary>
    /// 服务器正在运行中。
    /// </summary>
    Running,
    /// <summary>
    /// 服务器正在停止中。
    /// </summary>
    Stopping
}
