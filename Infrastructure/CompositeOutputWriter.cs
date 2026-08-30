using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 组合输出写入器，将同一条日志消息依次分发给多个底层输出写入器（如控制台与文件）。
/// </summary>
public class CompositeOutputWriter : IOutputWriter
{
    /// <summary>
    /// 被组合的底层输出写入器集合。
    /// </summary>
    private readonly IOutputWriter[] _writers;

    /// <summary>
    /// 使用一个或多个输出写入器构造组合写入器。
    /// </summary>
    /// <param name="writers">需要组合的底层输出写入器，按传入顺序依次接收消息。</param>
    public CompositeOutputWriter(params IOutputWriter[] writers)
    {
        _writers = writers;
    }

    /// <summary>
    /// 将日志消息依次写入所有底层输出写入器。
    /// </summary>
    /// <param name="context">日志上下文，通常为服务器名称或来源标识。</param>
    /// <param name="level">日志级别。</param>
    /// <param name="message">日志消息文本。</param>
    /// <param name="includeTimestamp">是否在日志中包含时间戳，默认包含。</param>
    public void Write(string context, LogLevel level, string message, bool includeTimestamp = true)
    {
        foreach (var writer in _writers)
        {
            writer.Write(context, level, message, includeTimestamp);
        }
    }
}
