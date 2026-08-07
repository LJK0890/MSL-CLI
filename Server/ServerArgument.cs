using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using static MSL_CLI.IO.IO;

namespace MSL_CLI.Server;

/// <summary>
/// 负责解析 Minecraft 服务器启动脚本（run.bat/run.sh）中的参数，
/// 提取 Java 路径、JVM 参数、jar 参数等。
/// </summary>
internal class ServerArgument
{
    private string javaPath = "java";
    private string userJvmArgs = "-Xms2500M -Xmx4G";
    private string jarArgs = "-jar server.jar";
    private string appendArgs = "nogui";
    private string name;

    /// <summary>
    /// 构造函数，解析指定服务器目录下的启动脚本。
    /// </summary>
    /// <param name="filePath">服务器根目录路径</param>
    public ServerArgument(string name, string filePath)
    {
        this.name = name;
        // 1. 根据操作系统选择对应的启动脚本文件名
        string runFile = Path.Combine(filePath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "run.bat" : "run.sh");
        Print($"{name}/Argument", LogLevel.INFO, $"尝试读取启动脚本: {runFile}", includeTimestamp: true);
        if (!File.Exists(runFile))
        {
            Print($"{name}/Argument", LogLevel.WARNING, $"启动脚本不存在，使用默认参数。", includeTimestamp: true);
            return;
        }

        // 2. 读取脚本内容
        string defaultArgsWithREM = File.ReadAllText(runFile, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(defaultArgsWithREM))
        {
            Print($"{name}/Argument", LogLevel.WARNING, $"启动脚本内容为空，使用默认参数。", includeTimestamp: true);
            return;
        }

        // 3. 查找以 '"' 或 "java" 开头的行（实际启动命令）
        string defaultArgsWithJAVA = string.Empty;
        foreach (var line in defaultArgsWithREM.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("\"") || trimmed.StartsWith("java"))
            {
                defaultArgsWithJAVA = trimmed;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(defaultArgsWithJAVA))
        {
            Print($"{name}/Argument", LogLevel.WARNING, $"未能找到 java 启动行，使用默认参数。", includeTimestamp: true);
            return;
        }
        Print($"{name}/Argument", LogLevel.INFO, $"找到启动命令行: {defaultArgsWithJAVA}", includeTimestamp: true);

        // 4. 提取 java 可执行文件路径（第一个 token）
        string pattern = @"^(\s*)(""[^""]*""|\S+)\s*";
        Match match = Regex.Match(defaultArgsWithJAVA, pattern);
        if (!match.Success)
        {
            Print($"{name}/Argument", LogLevel.WARNING, $"无法解析 java 路径，使用默认参数。", includeTimestamp: true);
            return;
        }
        string defaultArgsWithJar = defaultArgsWithJAVA.Substring(match.Length);
        string javaPathWithQuotes = match.Groups[2].Value;
        javaPath = javaPathWithQuotes.StartsWith("\"") && javaPathWithQuotes.EndsWith("\"")
            ? javaPathWithQuotes.Trim('"')
            : javaPathWithQuotes;
        Print($"{name}/Argument", LogLevel.INFO, $"Java 路径: {javaPath}", includeTimestamp: true);

        // 5. 按空格分词（保留引号内的空格）
        List<string> tokens = Tokenize(defaultArgsWithJar);
        if (tokens.Count == 0)
        {
            Print($"{name}/Argument", LogLevel.WARNING, $"参数分词结果为空，使用默认参数。", includeTimestamp: true);
            return;
        }

        // 6. 展开 @ 文件（例如 @libraries.txt），但排除 @win_args.txt / @unix_args.txt
        tokens = ExpandAtFiles(filePath, tokens);
        Print($"{name}/Argument", LogLevel.INFO, $"展开 @ 文件后共有 {tokens.Count} 个 token。", includeTimestamp: true);

        // 7. 定位 -jar 参数及其后紧跟的 jar 文件名
        int jarIndex = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].StartsWith("-jar="))
            {
                jarIndex = i;
                jarArgs = tokens[i];
                break;
            }
            else if (tokens[i].StartsWith("@"))
            {
                jarIndex = i;
                jarArgs = tokens[i];
                break;
            }
            else if (tokens[i].Equals("-jar", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= tokens.Count)
                {
                    Print($"{name}/Argument", LogLevel.WARNING, $"-jar 后缺少文件名，使用默认参数。", includeTimestamp: true);
                    return;
                }
                jarArgs = tokens[i] + " " + tokens[i + 1];
            }
        }
        if (jarIndex == -1)
        {
            Print($"{name}/Argument", LogLevel.WARNING, $"未找到 -jar 参数，使用默认参数。", includeTimestamp: true);
            return;
        }
        Print($"{name}/Argument", LogLevel.INFO, $"找到 -jar 参数: {jarArgs}", includeTimestamp: true);

        // 8. 提取 -jar 之前的所有 token 作为 JVM 参数（排除 % 开头的变量和 nogui）
        var jvmTokens = new List<string>();
        for (int i = 0; i < jarIndex; i++)
        {
            if (tokens[i].StartsWith("%"))
                continue;
            if (tokens[i].Equals("nogui", StringComparison.OrdinalIgnoreCase))
                break;
            jvmTokens.Add(tokens[i]);
        }
        if (jvmTokens.Count > 0)
        {
            userJvmArgs = string.Join(" ", jvmTokens);
            Print($"{name}/Argument", LogLevel.INFO, $"JVM 参数: {userJvmArgs}", includeTimestamp: true);
        }
        else
        {
            Print($"{name}/Argument", LogLevel.INFO, $"未提取到 JVM 参数，使用默认值。", includeTimestamp: true);
        }
    }

    /// <summary>
    /// 递归展开 @ 文件引用的内容（仅当文件存在且不是排除列表中的文件）。
    /// </summary>
    private List<string> ExpandAtFiles(string filePath, List<string> tokens)
    {
        var expanded = new List<string>();
        foreach (string token in tokens)
        {
            if (token.StartsWith("@"))
            {
                string fileName = token.Substring(1);

                // 排除 @win_args.txt 和 @unix_args.txt，保留原样
                if (fileName.EndsWith("win_args.txt", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith("unix_args.txt", StringComparison.OrdinalIgnoreCase))
                {
                    expanded.Add(token);
                    Print($"{name}/Argument", LogLevel.INFO, $"保留排除文件: {token}", includeTimestamp: true);
                    continue;
                }
                string atFilePath = Path.Combine(filePath, fileName);
                if (File.Exists(atFilePath))
                {
                    string atContent = File.ReadAllText(atFilePath, Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(atContent))
                    {
                        List<string> lines = new List<string>();
                        foreach(string line in atContent.Split(new[] { '\n' })){
                            if (!string.IsNullOrEmpty(line)  && !line.StartsWith("#"))
                            {
                                lines.Add(line);
                            }
                        }
                        var subTokens = Tokenize(string.Join(" ",lines));
                        expanded.AddRange(subTokens);
                        Print($"{name}/Argument", LogLevel.INFO, $"展开 @{fileName}，得到 {subTokens.Count} 个子 token。", includeTimestamp: true);
                    }
                    else
                    {
                        Print($"{name}/Argument", LogLevel.INFO, $"@{fileName} 文件内容为空，忽略。", includeTimestamp: true);
                    }
                }
                else
                {
                    Print($"{name}/Argument", LogLevel.INFO, $"@{fileName} 文件不存在，保留原 token。", includeTimestamp: true);
                    expanded.Add(token);
                }
            }
            else
            {
                expanded.Add(token);
            }
        }
        return expanded;
    }

    /// <summary>
    /// 将输入字符串按空格分词，支持双引号包围的字符串作为一个整体。
    /// </summary>
    private List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if ((c == ' ' || c == '\t' || c == '\n') && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>
    /// 获取完整的启动参数字符串。
    /// </summary>
    public string GetStartArguments()
    {
        return $"{javaPath} {userJvmArgs} {jarArgs} {appendArgs}".Trim();
    }

    /// <summary>
    /// 打印所有解析出的参数（用于调试）。
    /// </summary>
    public void PrintArguments()
    {
        Print($"{name}/Argument", LogLevel.INFO, $"Java Path: {javaPath}", includeTimestamp: true);
        Print($"{name}/Argument", LogLevel.INFO, $"User JVM Args: {userJvmArgs}", includeTimestamp: true);
        Print($"{name}/Argument", LogLevel.INFO, $"Jar Args: {jarArgs}", includeTimestamp: true);
        Print($"{name}/Argument", LogLevel.INFO, $"Append Args: {appendArgs}", includeTimestamp: true);
        Print($"{name}/Argument", LogLevel.INFO, $"Full Start Arguments: {GetStartArguments()}", includeTimestamp: true);
    }
}