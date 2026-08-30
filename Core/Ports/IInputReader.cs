namespace MSL_CLI.Core.Ports;

/// <summary>
/// 输入读取器接口，定义持续读取用户命令行输入并对外分发事件的能力。
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
}
