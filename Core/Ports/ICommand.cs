using MSL_CLI.Core.Domain;

namespace MSL_CLI.Core.Ports;

/// <summary>
/// 命令接口，定义单个可执行命令的元数据与执行行为。
/// </summary>
public interface ICommand
{
    /// <summary>
    /// 命令名称，用于在命令解析器中匹配命令。
    /// </summary>
    string Name { get; }
    /// <summary>
    /// 命令的功能描述文本，用于帮助信息展示。
    /// </summary>
    string Description { get; }
    /// <summary>
    /// 异步执行命令。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及所需的各服务引用。</param>
    /// <param name="output">可选的输出写入器；为 null 时使用默认输出。</param>
    /// <returns>命令执行结果，包含退出码与输出内容。</returns>
    Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null);
}

/// <summary>
/// 可选的参数校验钩子：命令可自行判断参数（含子命令/动作）是否合法。
/// 实现该接口后，代理在申请授权或执行之前就能拦掉拼错的子命令，
/// 不必等命令真正跑起来才报错，也不会把无效调用写进授权白名单。
/// </summary>
public interface IArgValidatingCommand
{
    /// <summary>
    /// 校验参数（<see cref="CommandArgs.Raw"/> 对应的文本，不含命令名本身）是否合法。
    /// </summary>
    /// <param name="rawArgs">命令名之后的参数文本。</param>
    /// <param name="args">命令参数上下文，提供注册表等依赖以便校验（可为 null）。</param>
    /// <param name="error">不合法时的说明文本，会原样回传给调用方。</param>
    /// <returns>参数合法时返回 true。</returns>
    bool TryValidateArgs(string rawArgs, CommandArgs? args, out string error);

    /// <summary>
    /// 取出用于授权匹配的“命令 + 子动作”范围，例如 <c>$server cfg</c>、<c>$app cfg</c>。
    /// 没有子动作概念或参数不足以判定时返回空字符串，表示回退为仅按命令名匹配。
    /// </summary>
    /// <param name="rawArgs">命令名之后的参数文本。</param>
    /// <param name="args">命令参数上下文，提供注册表等依赖以便判定（可为 null）。</param>
    /// <returns>授权范围；无法判定时返回空字符串。</returns>
    string GetPermissionScope(string rawArgs, CommandArgs? args);
}
