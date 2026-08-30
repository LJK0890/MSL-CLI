using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 服务器启动参数管理器：解析服务器 run.bat / run.sh 启动脚本中的
/// Java 路径、JVM 参数、JAR 参数与附加参数，并支持修改后自动持久化回脚本。
/// </summary>
public class ServerArgument
{
    /// <summary>
    /// 服务器名称。
    /// </summary>
    private readonly string _serverName;

    /// <summary>
    /// 服务器根目录路径，用于定位启动脚本及参数文件。
    /// </summary>
    private readonly string _serverPath;

    /// <summary>
    /// 输出写入器，用于输出解析与保存过程中的提示或错误信息。
    /// </summary>
    private readonly IOutputWriter _output;

    /// <summary>
    /// Java 可执行文件路径，默认 "java"。
    /// </summary>
    private string _javaPath = "java";

    /// <summary>
    /// JVM 启动参数，默认 "-Xms2G -Xmx4G"。
    /// </summary>
    private string _jvmArgs = "-Xms2G -Xmx4G";

    /// <summary>
    /// JAR 启动参数，默认 "-jar server.jar"。
    /// </summary>
    private string _jarArgs = "-jar server.jar";

    /// <summary>
    /// 附加参数，默认 "nogui"。
    /// </summary>
    private string _appendArgs = "nogui";

    /// <summary>
    /// 初始化服务器启动参数管理器，立即解析现有启动脚本并持久化参数。
    /// </summary>
    /// <param name="serverName">服务器名称。</param>
    /// <param name="serverPath">服务器根目录路径。</param>
    /// <param name="output">输出写入器，用于记录解析及保存过程中的信息。</param>
    public ServerArgument(string serverName, string serverPath, IOutputWriter output)
    {
        _serverName = serverName;
        _serverPath = serverPath;
        _output = output;
        Parse();
        // 解析后立即持久化，确保 user_jvm_args.txt 存在且脚本指向它
        SaveToScript();
    }

    /// <summary>
    /// 解析启动脚本，提取 Java 路径、JVM 参数、JAR 参数与附加参数。
    /// </summary>
    private void Parse()
    {
        string runFile = Path.Combine(_serverPath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "run.bat" : "run.sh");
        if (!File.Exists(runFile))
        {
            _output.Write(_serverName, LogLevel.Warning, "启动脚本不存在，使用默认参数");
            return;
        }

        var lines = File.ReadAllLines(runFile, Encoding.UTF8);
        string? startLine = null;
        // 逐行查找以引号或 "java" 开头的启动命令行
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("\"") || trimmed.StartsWith("java"))
            {
                startLine = trimmed;
                break;
            }
        }
        if (string.IsNullOrEmpty(startLine))
        {
            _output.Write(_serverName, LogLevel.Warning, "未找到启动行，使用默认参数");
            return;
        }

        // 提取java路径
        var match = Regex.Match(startLine, @"^(\s*)(""[^""]*""|\S+)\s*");
        if (!match.Success) return;
        var javaPart = match.Groups[2].Value.Trim('"');
        _javaPath = javaPart;

        // 剩余参数
        string rest = startLine.Substring(match.Length);
        var tokens = Tokenize(rest);
        tokens = ExpandAtFiles(_serverPath, tokens);
        // 定位 -jar
        int jarIndex = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Equals("-jar", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < tokens.Count)
                {
                    _jarArgs = "-jar " + tokens[i + 1];
                    jarIndex = i;
                    break;
                }
            }
            else if (tokens[i].StartsWith("-jar=") || tokens[i].StartsWith("@"))
            {
                _jarArgs = tokens[i];
                jarIndex = i;
                break;
            }
        }
        if (jarIndex == -1)
        {
            _output.Write(_serverName, LogLevel.Warning, "未找到 -jar 参数，使用默认");
            return;
        }

        // 提取JVM参数（-jar之前）
        var jvm = new List<string>();
        for (int i = 0; i < jarIndex; i++)
        {
            if (tokens[i].StartsWith("%")) continue;
            if (tokens[i].Equals("nogui", StringComparison.OrdinalIgnoreCase))
            {
                _appendArgs = "nogui";
                break;
            }
            jvm.Add(tokens[i]);
        }
        if (jvm.Count > 0)
            _jvmArgs = string.Join(" ", jvm);
    }

    /// <summary>
    /// 将参数字符串按空白字符拆分为 token 列表，支持双引号包裹的空格。
    /// </summary>
    /// <param name="input">原始参数字符串。</param>
    /// <returns>拆分后的 token 列表。</returns>
    private List<string> Tokenize(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in input)
        {
            // 双引号用于切换引号状态，本身不进入 token
            if (c == '"') { inQuotes = !inQuotes; continue; }
            // 引号外的空白作为 token 分隔符
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// 展开 @ 引用的参数文件（如 @libraries/...），将其内容按行并入参数列表；
    /// 但 win_args.txt / unix_args.txt 这类脚本参数文件本身不展开。
    /// </summary>
    /// <param name="basePath">参数文件所在的基础目录（服务器根目录）。</param>
    /// <param name="tokens">待展开的 token 列表。</param>
    /// <returns>展开后的 token 列表；文件不存在时保留原 token。</returns>
    private List<string> ExpandAtFiles(string basePath, List<string> tokens)
    {
        var expanded = new List<string>();
        foreach (var token in tokens)
        {
            if (token.StartsWith("@") && !token.EndsWith("win_args.txt", StringComparison.OrdinalIgnoreCase)
                                      && !token.EndsWith("unix_args.txt", StringComparison.OrdinalIgnoreCase))
            {
                string atFile = Path.Combine(basePath, token.Substring(1));
                if (File.Exists(atFile))
                {
                    var content = File.ReadAllText(atFile, Encoding.UTF8);
                    // 按行读取并跳过以 # 开头的注释行
                    var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Where(l => !l.Trim().StartsWith("#"));
                    foreach (var line in lines)
                        expanded.AddRange(Tokenize(line));
                }
                else
                    expanded.Add(token);
            }
            else
                expanded.Add(token);
        }
        return expanded;
    }

    // ---------- Getter ----------
    /// <summary>
    /// 获取完整的启动命令行参数（含 Java 路径、JVM 参数、JAR 参数与附加参数）。
    /// </summary>
    /// <returns>拼接并去除首尾空白的完整启动参数字符串。</returns>
    public string GetStartArguments() => $"{_javaPath} {_jvmArgs} {_jarArgs} {_appendArgs}".Trim();

    /// <summary>
    /// 获取 Java 可执行文件路径。
    /// </summary>
    /// <returns>Java 路径。</returns>
    public string GetJavaPath() => _javaPath;

    /// <summary>
    /// 获取 JVM 启动参数。
    /// </summary>
    /// <returns>JVM 参数字符串。</returns>
    public string GetJvmArgs() => _jvmArgs;

    /// <summary>
    /// 获取 JAR 启动参数。
    /// </summary>
    /// <returns>JAR 参数字符串。</returns>
    public string GetJarArgs() => _jarArgs;

    /// <summary>
    /// 获取附加参数（如 nogui）。
    /// </summary>
    /// <returns>附加参数字符串。</returns>
    public string GetAppendArgs() => _appendArgs;

    /// <summary>
    /// 获取除 Java 路径外的全部启动参数（JVM、JAR 与附加参数）。
    /// </summary>
    /// <returns>拼接并去除首尾空白的参数字符串。</returns>
    public string GetJavaArgs() => $"{_jvmArgs} {_jarArgs} {_appendArgs}".Trim();

    // ---------- Setter（自动持久化） ----------
    /// <summary>
    /// 设置 Java 可执行文件路径并立即持久化到启动脚本。
    /// </summary>
    /// <param name="value">新的 Java 路径。</param>
    public void SetJavaPath(string value)
    {
        _javaPath = value;
        SaveToScript();
    }

    /// <summary>
    /// 设置 JVM 启动参数并立即持久化到 user_jvm_args.txt。
    /// </summary>
    /// <param name="value">新的 JVM 参数。</param>
    public void SetJvmArgs(string value)
    {
        _jvmArgs = value;
        SaveToScript();
    }

    /// <summary>
    /// 设置 JAR 启动参数并立即持久化到启动脚本。
    /// </summary>
    /// <param name="value">新的 JAR 参数。</param>
    public void SetJarArgs(string value)
    {
        _jarArgs = value;
        SaveToScript();
    }

    /// <summary>
    /// 设置附加参数并立即持久化到启动脚本。
    /// </summary>
    /// <param name="value">新的附加参数。</param>
    public void SetAppendArgs(string value)
    {
        _appendArgs = value;
        SaveToScript();
    }

    // ---------- 持久化 ----------
    /// <summary>
    /// 将当前参数持久化到服务器的启动脚本（run.bat / run.sh）与 user_jvm_args.txt。
    /// </summary>
    private void SaveToScript()
    {
        try
        {
            // 1. 写入 run.bat / run.sh
            string runFile = Path.Combine(_serverPath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "run.bat" : "run.sh");
            // 构建新脚本内容：<javapath> @user_jvm_args.txt <jarargs> <append>
            string scriptContent = $"{_javaPath} @user_jvm_args.txt {_jarArgs} {_appendArgs}";
            File.WriteAllText(runFile, scriptContent, Encoding.UTF8);

            // 2. 写入 user_jvm_args.txt（仅 JVM 参数）
            string jvmArgsFile = Path.Combine(_serverPath, "user_jvm_args.txt");
            File.WriteAllText(jvmArgsFile, _jvmArgs, Encoding.UTF8);

            _output.Write(_serverName, LogLevel.Success, $"启动脚本和 user_jvm_args.txt 已更新");
        }
        catch (Exception ex)
        {
            _output.Write(_serverName, LogLevel.Error, $"保存启动脚本失败: {ex.Message}");
        }
    }
}
