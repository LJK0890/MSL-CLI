namespace MSL_CLI.Core.Ports;

/// <summary>
/// 输入读取器接口，定义持续读取用户命令行输入并对外分发事件的能力，
/// 并支持其他组件在需要提问时临时独占读取一行输入。
/// </summary>
public interface IInputReader
{
    /// <summary>
    /// 当读取到新的用户输入时触发，事件参数为用户输入的文本内容。
    /// </summary>
    event EventHandler<string> OnInputReceived;
    /// <summary>
    /// 开始持续读取用户输入。
    /// </summary>
    void StartReading();
    /// <summary>
    /// 停止读取用户输入。
    /// </summary>
    void StopReading();
    /// <summary>
    /// 请求独占读取控制台的一行输入，用于向用户提问并等待作答。
    /// 在请求挂起期间，读取到的行会作为本方法的返回值，而不会触发 <see cref="OnInputReceived"/>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌，取消时立即返回空字符串。</param>
    /// <param name="timeout">可选的等待超时；超时后返回空字符串并解除独占。</param>
    /// <returns>用户输入的整行文本；超时、取消或已有其他请求挂起时为空字符串。</returns>
    Task<string> ReadLineAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null);
}
