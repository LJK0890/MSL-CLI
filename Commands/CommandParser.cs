using System.Reflection;
using MSL_CLI.Services;
using static MSL_CLI.IO.IO;

namespace MSL_CLI.Commands;

/// <summary>
/// 命令解析器，自动注册所有带 [Command] 特性的类。
/// </summary>
internal class CommandParser
{
    private readonly Dictionary<string, ICommand> _handlers
        = new Dictionary<string, ICommand>(StringComparer.OrdinalIgnoreCase);
    private readonly GlobalManager _globalManager;

    public CommandParser(GlobalManager globalManager)
    {
        _globalManager = globalManager ?? throw new ArgumentNullException(nameof(globalManager));
        RegisterCommands();
    }

    /// <summary>
    /// 执行命令字符串。
    /// </summary>
    /// <returns>命令返回值，0 表示失败，1 表示成功，-1 表示未知命令</returns>
    public int Execute(string input,bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(input))
            return 0;

        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string cmdName = parts[0];
        string argsRaw = parts.Length > 1 ? parts[1] : string.Empty;

        if (_handlers.TryGetValue(cmdName, out var command))
        {
            var args = new CommandArgs(argsRaw, _globalManager);
            try
            {
                return command.Execute(args,capture);
            }
            catch (Exception ex)
            {
                Print("Command", LogLevel.ERROR, $"执行命令 '{cmdName}' 时发生异常: {ex.Message}", true);
                return 0;
            }
        }
        else
        {
            Print("Command", LogLevel.WARNING, $"未知命令: {cmdName}", true);
            return -1;
        }
    }

    private void RegisterCommands()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var commandTypes = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ICommand).IsAssignableFrom(t));

        foreach (var type in commandTypes)
        {
            var attr = type.GetCustomAttribute<CommandAttribute>();
            if (attr == null)
                continue;

            if (Activator.CreateInstance(type) is ICommand instance)
            {
                _handlers[attr.Name] = instance;
                Print("Command", LogLevel.INFO, $"已注册命令: {attr.Name} -> {type.Name}", true);
            }
            else
            {
                Print("Command", LogLevel.ERROR, $"无法创建命令实例: {type.Name}", true);
            }
        }
    }

    public Dictionary<string, string> GetCommandDescriptions()
    {
        return _handlers.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.GetType().GetCustomAttribute<CommandAttribute>()?.Description ?? "无描述"
        );
    }

    public async Task<(int ExitCode, string Output)> ExecuteWithCaptureAsync(string input)
    {
        StartCapture();
        int exitCode;
        try
        {
            exitCode = await Task.Run(() => Execute(input,true));

        }
        catch (Exception ex)
        {
            // 捕获异常并记录到缓冲区
            Print("Command", LogLevel.ERROR, $"执行命令时发生异常: {ex.Message}", true, true, true);
            exitCode = 0;
        }
        finally
        {
            // 无论成功或失败，停止捕获并返回输出
        }
        string output = await Task.Run(() => StopCapture());
        return (exitCode, output);
    }
}