using System.Diagnostics;
using System.Text;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// <c>$app exec</c> 动作的处理器：执行系统命令或脚本，并捕获标准输出与错误输出。
/// 它被放在 <c>$app</c> 下，因为执行任意系统命令属于高危、大权限操作，
/// 与 <c>$app cfg</c>（可改写全部配置）同属一类；代理调用时永远需要操作员逐次确认。
/// </summary>
internal static class AppExecHandler
{
    /// <summary>
    /// 执行一条系统命令或脚本。
    /// </summary>
    /// <param name="rest">exec 之后的参数文本（命令/脚本路径与参数）。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果，退出码为 0 时视为成功。</returns>
    public static async Task<CommandResult> RunAsync(string rest, IOutputWriter? output)
    {
        if (string.IsNullOrWhiteSpace(rest))
            return Fail(output, "用法: $app exec <命令/脚本路径> [参数...]");

        // 解析命令和参数（支持引号括起来的参数）
        var parts = ParseCommandLine(rest);
        if (parts.Count == 0)
            return Fail(output, "命令不能为空");

        var command = parts[0];

        output?.Write("Command", LogLevel.Info, ""); // 输出换行

        try
        {
            // 配置进程启动信息：隐藏窗口、重定向输出并使用 UTF-8 编码
            var processStartInfo = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // 逐个参数追加到 ArgumentList：拼接成字符串会丢掉引号信息，
            // 使 $app exec tool "a b" c 被执行成 tool a b c（4 个参数而不是 3 个）
            foreach (var arg in parts.Skip(1))
                processStartInfo.ArgumentList.Add(arg);

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

            var resultMsg = $"进程退出码: {process.ExitCode}";

            // 汇总退出码、标准输出与错误输出为完整结果。
            // 只在这里输出一次：上面若再逐段输出，$app exec 的每行输出都会出现两遍。
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
            return Fail(output, $"执行失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 简单解析命令行，支持双引号包裹的参数（忽略转义）。
    /// </summary>
    /// <param name="commandLine">原始命令行字符串。</param>
    /// <returns>解析出的参数列表（不含引号）。</returns>
    private static List<string> ParseCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        // 逐字符扫描：引号切换状态，引号外的空白作为参数分隔
        foreach (var c in commandLine)
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

    /// <summary>
    /// 输出错误信息并构造失败结果。
    /// </summary>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <param name="message">错误信息。</param>
    /// <returns>退出码为 0 的失败结果。</returns>
    private static CommandResult Fail(IOutputWriter? output, string message)
    {
        output?.Write("Command", LogLevel.Error, message);
        return new CommandResult(0, message);
    }
}
