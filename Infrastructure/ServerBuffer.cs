using System.Collections.Concurrent;
using System.Text;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 基于内存的服务器输出缓冲区实现，为每台服务器维护独立的日志文本，
/// 供 AI 代理及 $bufr / $bufu 等命令读取服务器实时输出。
/// </summary>
public class ServerBuffer : IServerBuffer
{
    /// <summary>
    /// 以服务器名为键的输出缓冲区字典，ConcurrentDictionary 保证多线程下的并发安全。
    /// </summary>
    private readonly ConcurrentDictionary<string, StringBuilder> _buffers = new();

    /// <summary>
    /// 向指定服务器的缓冲区追加一行文本。
    /// </summary>
    /// <param name="serverName">服务器名称，用作缓冲区键。</param>
    /// <param name="text">要追加的文本内容。</param>
    public void Append(string serverName, string text)
    {
        // 若该服务器尚无缓冲区则先创建，再通过对象锁保证同一缓冲区的写入串行化
        var sb = _buffers.GetOrAdd(serverName, _ => new StringBuilder());
        lock (sb) sb.AppendLine(text);
    }

    /// <summary>
    /// 获取指定服务器的缓冲区当前内容（不清空）。
    /// </summary>
    /// <param name="serverName">服务器名称。</param>
    /// <returns>缓冲区文本；若服务器不存在则返回空字符串。</returns>
    public string GetBuffer(string serverName)
    {
        if (_buffers.TryGetValue(serverName, out var sb))
        {
            lock (sb) return sb.ToString();
        }
        return string.Empty;
    }

    /// <summary>
    /// 获取指定服务器的缓冲区内容，并同时清空缓冲区。
    /// </summary>
    /// <param name="serverName">服务器名称。</param>
    /// <returns>清空前的缓冲区文本；若服务器不存在则返回空字符串。</returns>
    public string GetAndClearBuffer(string serverName)
    {
        if (_buffers.TryGetValue(serverName, out var sb))
        {
            lock (sb)
            {
                string content = sb.ToString();
                sb.Clear();
                return content;
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// 清空指定服务器的缓冲区。
    /// </summary>
    /// <param name="serverName">服务器名称。</param>
    public void ClearBuffer(string serverName)
    {
        if (_buffers.TryGetValue(serverName, out var sb))
        {
            lock (sb) sb.Clear();
        }
    }
}
