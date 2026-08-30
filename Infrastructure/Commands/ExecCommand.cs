using System.Diagnostics;
using System.Text;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// 执行系统命令或脚本的命令（$exec）。
/// </summary>
public class ExecCommand : ICommand
{
    /// <summary>
    /// 命令名称：$exec。
    /// </summary>
    public string Name => "$exec";
    /// <summary>
    /// 命令描述：执行系统命令或脚本。
    /// </summary>
    public string Description => "执行系统命令或脚本";

    /// <summary>
    /// 执行 $exec 命令，启动系统进程执行命令，并捕获标准输出与错误输出。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果，退出码为 0 时视为成功。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var msg = "用法: $exec <命令/脚本路径> [参数...]";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // 解析命令和参数（支持引号括起来的参数）
        var parts = ParseCommandLine(args.Raw);
        if (parts.Count == 0)
        {
            var msg = "命令不能为空";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        string command = parts[0];
        string arguments = parts.Count > 1 ? string.Join(" ", parts.Skip(1)) : string.Empty;

        output?.Write("Command", LogLevel.Info, ""); // 输出换行

        try
        {
            // 配置进程启动信息：隐藏窗口、重定向输出并使用 UTF-8 编码
            var processStartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = processStartInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            // 异步收集标准输出与错误输出
            process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            var outputText = outputBuilder.ToString();
            var errorText = errorBuilder.ToString();

            if (!string.IsNullOrEmpty(outputText))
                output?.Write("Command", LogLevel.Info, outputText);
            if (!string.IsNullOrEmpty(errorText))
                output?.Write("Command", LogLevel.Error, errorText);

            var resultMsg = $"进程退出码: {process.ExitCode}";
            output?.Write("Command", LogLevel.Info, resultMsg);

            // 汇总退出码、标准输出与错误输出为完整结果
            var fullOutput = resultMsg;
            if (!string.IsNullOrEmpty(outputText))
                fullOutput += "\n" + outputText;
            if (!string.IsNullOrEmpty(errorText))
                fullOutput += "\n[错误]\n" + errorText;

            output?.Write("Command", process.ExitCode == 0 ? LogLevel.Success : LogLevel.Error, fullOutput);
            return new CommandResult(process.ExitCode == 0 ? 1 : 0, fullOutput);
        }
        catch (Exception ex)
        {
            var msg = $"执行失败: {ex.Message}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }
    }

    /// <summary>
    /// 简单解析命令行，支持双引号包裹的参数（忽略转义）
    /// </summary>
    /// <param name="commandLine">原始命令行字符串。</param>
    /// <returns>解析出的参数列表（不含引号）。</returns>
    private List<string> ParseCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        // 逐字符扫描：引号切换状态，引号外的空白作为参数分隔
        foreach (char c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        // 收尾：将最后一个参数加入结果
        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }
}
