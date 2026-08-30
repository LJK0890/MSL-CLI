using MSL_CLI.Core.Domain;

namespace MSL_CLI.Core.Ports;

/// <summary>
/// 输出写入器接口，定义向用户界面输出带日志级别的消息的能力。
/// </summary>
public interface IOutputWriter
{
    /// <summary>
    /// 写入一条带日志级别的输出消息。
    /// </summary>
    /// <param name="context">消息所属的上下文（如服务器名称、命令名称等）。</param>
    /// <param name="level">消息的日志级别（如 Info、Warning、Error 等）。</param>
    /// <param name="message">要输出的消息文本内容。</param>
    /// <param name="includeTimestamp">是否在消息前附带时间戳，默认 true。</param>
    void Write(string context, LogLevel level, string message, bool includeTimestamp = true);
}
