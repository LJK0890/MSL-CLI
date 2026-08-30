using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Core.UseCases;

/// <summary>
/// 命令执行器，负责解析并执行用户输入的命令。
/// </summary>
public class CommandExecutor : ICommandExecutor
{
    private readonly ICommandParser _parser;
    private readonly IServerRegistry _registry;
    private readonly IAgentService _agent;
    private readonly IConfigurationStore _config;

    /// <summary>
    /// 初始化命令执行器。
    /// </summary>
    /// <param name="parser">命令解析器，用于根据命令名获取命令。</param>
    /// <param name="registry">服务器注册表，供命令执行过程中访问服务器。</param>
    /// <param name="agent">AI 代理服务，供命令执行过程中调用代理能力。</param>
    /// <param name="config">配置存储，供命令执行过程中读取配置。</param>
    public CommandExecutor(ICommandParser parser, IServerRegistry registry, IAgentService agent, IConfigurationStore config)
    {
        _parser = parser;
        _registry = registry;
        _agent = agent;
        _config = config;
    }

    /// <summary>
    /// 异步执行一条命令。
    /// </summary>
    /// <param name="input">用户输入的命令字符串。</param>
    /// <param name="output">输出写入器，用于输出执行过程中的日志；为 null 时忽略日志输出。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(string input, IOutputWriter? output = null)
    {
        // 空白输入直接返回成功且无输出的空结果
        if (string.IsNullOrWhiteSpace(input))
            return new CommandResult(0, string.Empty);

        // 将输入按首个空格拆分为命令名与剩余参数
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmdName = parts[0];
        var argsRaw = parts.Length > 1 ? parts[1] : string.Empty;

        // 根据命令名查找命令，未找到时记录错误并返回失败结果
        var command = _parser.GetCommand(cmdName);
        if (command == null)
        {
            output?.Write("Command", LogLevel.Error, $"未知命令: {cmdName}");
            return new CommandResult(-1, $"未知命令: {cmdName}");
        }
            

        // 构造命令参数对象，并注入解析器以供命令内部使用
        var args = new CommandArgs(argsRaw, _registry, _agent, _config)
        {
            Parser = _parser
        };

        try
        {
            // 执行命令并返回其执行结果
            var result = await command.ExecuteAsync(args, output);
            return result;
        }
        catch (Exception ex)
        {
            // 命令执行异常时记录错误日志，并以退出码 0 返回异常信息
            output?.Write("Command", LogLevel.Error, $"执行命令异常: {ex.Message}");
            return new CommandResult(0, ex.Message);
        }
    }
}
