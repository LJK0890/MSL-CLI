namespace MSL_CLI.Core.Ports;

/// <summary>
/// 服务器输出缓冲区接口，用于暂存各服务器的输出日志，支持按需读取与清空。
/// </summary>
public interface IServerBuffer
{
    /// <summary>
    /// 向指定服务器的缓冲区追加一段文本。
    /// </summary>
    /// <param name="serverName">服务器名称。</param>
    /// <param name="text">要追加的文本内容。</param>
    void Append(string serverName, string text);
    /// <summary>
    /// 获取指定服务器的缓冲区内容（不清空缓冲区）。
    /// </summary>
    /// <param name="serverName">服务器名称。</param>
    /// <returns>缓冲区的完整文本内容。</returns>
    string GetBuffer(string serverName);
    /// <summary>
    /// 获取指定服务器的缓冲区内容，并清空缓冲区。
    /// </summary>
    /// <param name="serverName">服务器名称。</param>
    /// <returns>清空前的缓冲区文本内容。</returns>
    string GetAndClearBuffer(string serverName);
    /// <summary>
    /// 清空指定服务器的缓冲区。
    /// </summary>
    /// <param name="serverName">服务器名称。</param>
    void ClearBuffer(string serverName);
}
