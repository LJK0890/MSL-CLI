using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 服务器启动参数管理器。
/// 内建模型为：<c>javaPath</c>(string)、<c>jvmArgs</c>(List)、<c>jarArgs</c>(string)、<c>appendArgs</c>(List)。
/// JVM 参数写入 <c>user_jvm_args.txt</c>，启动脚本按
/// <c>{javaPath} @user_jvm_args.txt {jarArgs} {appendArgs}</c> 的形式重建，
/// 与 Forge / NeoForge 官方启动脚本格式保持一致。
/// </summary>
public class ServerArgument
{
    /// <summary>服务器名称，用于日志前缀。</summary>
    private readonly string _serverName;

    /// <summary>服务器根目录，用于定位启动脚本与 user_jvm_args.txt。</summary>
    private readonly string _serverPath;

    /// <summary>输出写入器。</summary>
    private readonly IOutputWriter _output;

    /// <summary>Java 可执行文件路径，默认 "java"。可读写，不可删除。</summary>
    private string _javaPath = "java";

    /// <summary>JVM 启动参数（列表）。可读写，可删除单个或全部。</summary>
    private readonly List<string> _jvmArgs = new();

    /// <summary>JAR / 参数文件参数，例如 "-jar server.jar" 或 "@libraries/.../win_args.txt"。可读写，不可删除。</summary>
    private string _jarArgs = "-jar server.jar";

    /// <summary>附加参数（列表），例如 "nogui"。可读写，可删除单个或全部。</summary>
    private readonly List<string> _appendArgs = new();

    /// <summary>JVM 参数文件的固定文件名。</summary>
    private const string JvmArgsFileName = "user_jvm_args.txt";

    /// <summary>解析失败时的默认 JVM 参数。</summary>
    private static readonly string[] DefaultJvmArgs = { "-Xms2G", "-Xmx4G" };

    /// <summary>解析失败时的默认附加参数。</summary>
    private static readonly string[] DefaultAppendArgs = { "nogui" };

    /// <summary>
    /// 初始化管理器：立即解析现有启动脚本，并把解析结果按标准格式持久化回去。
    /// </summary>
    /// <param name="serverName">服务器名称。</param>
    /// <param name="serverPath">服务器根目录路径。</param>
    /// <param name="output">输出写入器。</param>
    public ServerArgument(string serverName, string serverPath, IOutputWriter output)
    {
        _serverName = serverName;
        _serverPath = serverPath;
        _output = output;

        // 脚本存在但识别不出启动行时不改写文件：否则会把用户自定义的启动脚本
        // 覆盖成默认的 "java @user_jvm_args.txt ..."，javaPath 也就此丢失
        if (Parse())
            SaveToScript();
        else
            _output.Write(_serverName, LogLevel.Warning,
                "启动脚本中未识别出启动行，已保持原样未改写；如需重建请用 $server arg set <服务器> javaPath <路径>");
    }

    // ---------- 启动脚本路径 ----------

    /// <summary>当前平台对应的启动脚本完整路径。</summary>
    private string RunScriptPath =>
        Path.Combine(_serverPath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "run.bat" : "run.sh");

    /// <summary>JVM 参数文件的完整路径。</summary>
    private string JvmArgsFilePath => Path.Combine(_serverPath, JvmArgsFileName);

    // ---------- 解析 ----------

    /// <summary>
    /// 解析启动脚本，填充 javaPath / jvmArgs / jarArgs / appendArgs。
    /// 解析失败的部分使用默认值，保证对象始终可用。
    /// </summary>
    /// <returns>
    /// 脚本不存在（按默认值生成）或成功解析出启动行时返回 true；
    /// 脚本存在但识别不出启动行时返回 false（调用方据此避免改写原文件）。
    /// </returns>
    private bool Parse()
    {
        if (!File.Exists(RunScriptPath))
        {
            _output.Write(_serverName, LogLevel.Warning, "启动脚本不存在，使用默认参数");
            _jvmArgs.AddRange(DefaultJvmArgs);
            _appendArgs.AddRange(DefaultAppendArgs);
            return true;
        }

        // 1. 读取 user_jvm_args.txt 作为 JVM 参数的权威来源
        var fileJvmArgs = ReadJvmArgsFile();

        // 2. 找到启动命令行
        var startLine = FindStartLine(File.ReadAllLines(RunScriptPath, Encoding.UTF8));

        if (string.IsNullOrEmpty(startLine))
        {
            _output.Write(_serverName, LogLevel.Warning, "未找到启动行，使用默认参数");
            _jvmArgs.AddRange(fileJvmArgs.Count > 0 ? fileJvmArgs : DefaultJvmArgs);
            _appendArgs.AddRange(DefaultAppendArgs);
            return false;
        }

        // 3. 提取 java 路径及其后的参数
        var (javaPath, rest) = SplitJavaPath(startLine);
        if (string.IsNullOrWhiteSpace(javaPath))
        {
            _output.Write(_serverName, LogLevel.Warning, "无法解析 Java 路径，使用默认参数");
            _jvmArgs.AddRange(fileJvmArgs.Count > 0 ? fileJvmArgs : DefaultJvmArgs);
            _appendArgs.AddRange(DefaultAppendArgs);
            return false;
        }
        _javaPath = javaPath;

        // 4. 拆分其余 token；@user_jvm_args.txt 交由文件承载，不再重复出现在命令行
        var tokens = Tokenize(rest)
            .Where(t => !t.Equals("@" + JvmArgsFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 5. 定位 jar 参数
        var jarIndex = FindJarIndex(tokens);
        if (jarIndex < 0)
        {
            // 识别不出“启动 Java 的那一段”（既无 -jar 也无 @xxx_args.txt）时，
            // 只把解析结果留在内存里作为默认值，**不改写**用户的启动脚本——
            // 否则会把自定义脚本覆盖成 "{java} @user_jvm_args.txt -jar server.jar ..."
            _output.Write(_serverName, LogLevel.Warning, "未找到 -jar 参数，使用默认参数并保持脚本原样");
            _jvmArgs.Clear();
            _jvmArgs.AddRange(fileJvmArgs.Count > 0 ? fileJvmArgs : DefaultJvmArgs);
            _appendArgs.Clear();
            _appendArgs.AddRange(tokens.Count > 0 ? tokens : DefaultAppendArgs);
            return false;
        }

        // 6. jarArgs：-jar x.jar 视为整体，否则是单个 token（如 @.../win_args.txt）
        var jarTokenCount = 1;
        if (tokens[jarIndex].Equals("-jar", StringComparison.OrdinalIgnoreCase) && jarIndex + 1 < tokens.Count)
        {
            _jarArgs = $"-jar {tokens[jarIndex + 1]}";
            jarTokenCount = 2;
        }
        else
        {
            _jarArgs = tokens[jarIndex];
        }

        // 7. appendArgs：jar 参数之后的全部 token
        _appendArgs.Clear();
        _appendArgs.AddRange(tokens.Skip(jarIndex + jarTokenCount));

        // 8. jvmArgs：命令行中位于 jar 之前的 token，与文件内容合并（命令行优先）
        _jvmArgs.Clear();
        _jvmArgs.AddRange(fileJvmArgs);
        foreach (var token in tokens.Take(jarIndex))
        {
            if (token.StartsWith('%')) continue;                 // 跳过 win_args.txt 中的占位符
            if (!_jvmArgs.Contains(token, StringComparer.Ordinal))
                _jvmArgs.Add(token);
        }

        if (_jvmArgs.Count == 0)
            _jvmArgs.AddRange(DefaultJvmArgs);

        return true;
    }

    /// <summary>
    /// 在启动脚本中定位启动命令行。
    /// 先排除批处理/shell 的指令与注释行，再优先取真正启动 Java 的行
    /// （含 -jar 或 @xxx_args.txt），否则退化为首个候选行。
    /// 这样无论 javaPath 是 "java"、带引号的绝对路径，还是含空格且未加引号的路径，都能被识别。
    /// </summary>
    /// <param name="lines">脚本的全部行。</param>
    /// <returns>启动命令行；识别不出时返回 null。</returns>
    private static string? FindStartLine(IEnumerable<string> lines)
    {
        var candidates = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || IsShellDirective(line)) continue;
            candidates.Add(line);
        }

        if (candidates.Count == 0) return null;

        return candidates.FirstOrDefault(LooksLikeJavaCommand) ?? candidates[0];
    }

    /// <summary>
    /// 判断一行是否为批处理 / shell 的指令或注释行（这类行不会启动 Java）。
    /// </summary>
    /// <param name="line">已去除首尾空白的脚本行。</param>
    /// <returns>是指令/注释行时返回 true。</returns>
    private static bool IsShellDirective(string line)
    {
        if (line.StartsWith('@') || line.StartsWith('#') || line.StartsWith(':') || line.StartsWith('%'))
            return true;

        string[] keywords = { "rem ", "set ", "echo ", "pause", "cd ", "if ", "goto ", "exit", "title ", "call " };
        return keywords.Any(k => line.StartsWith(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 判断一行是否像真正的 Java 启动命令（包含 -jar 或 @xxx_args.txt 之类的参数文件）。
    /// </summary>
    /// <param name="line">脚本行。</param>
    /// <returns>像启动命令时返回 true。</returns>
    private static bool LooksLikeJavaCommand(string line)
        => line.Contains("-jar", StringComparison.OrdinalIgnoreCase)
           || Regex.IsMatch(line, @"@\S*\.txt");

    /// <summary>
    /// 从启动行中切出 Java 路径与其余参数，覆盖三种写法：
    /// <c>"C:\Program Files\...\java.exe" @user_jvm_args.txt ...</c>（带引号）、
    /// <c>java -Xmx4G -jar server.jar</c>（裸 java）、
    /// <c>C:\Program Files\...\java.exe @user_jvm_args.txt ...</c>（旧版本写出的未加引号含空格路径）。
    /// </summary>
    /// <param name="startLine">启动命令行。</param>
    /// <returns>Java 路径与剩余参数字符串。</returns>
    private static (string Java, string Args) SplitJavaPath(string startLine)
    {
        var line = startLine.Trim();

        // 情形一：以引号包裹的路径开头
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf('"', 1);
            if (end > 1)
                return (line[1..end].Trim(), line[(end + 1)..].Trim());
        }

        // 情形二：参数从第一个以 - 或 @ 开头的 token 开始，其之前的整体就是 Java 路径
        // （据此可正确还原未加引号、但路径中含空格的写法，而不是被空格截断）
        for (var i = 1; i < line.Length; i++)
        {
            if (!char.IsWhiteSpace(line[i - 1])) continue;   // 只在 token 起始处判断
            if (line[i] == '-' || line[i] == '@')
                return (line[..i].Trim().Trim('"'), line[i..].Trim());
        }

        // 情形三：整行没有可识别的参数，取第一个 token 作为路径
        var spaceIndex = line.IndexOfAny(new[] { ' ', '\t' });
        return spaceIndex < 0
            ? (line.Trim('"'), string.Empty)
            : (line[..spaceIndex].Trim('"'), line[spaceIndex..].Trim());
    }

    /// <summary>
    /// 读取 user_jvm_args.txt 中的 JVM 参数，按空白拆分并跳过注释行。
    /// </summary>
    /// <returns>JVM 参数列表；文件不存在时返回空列表。</returns>
    private List<string> ReadJvmArgsFile()
    {
        var result = new List<string>();
        if (!File.Exists(JvmArgsFilePath)) return result;

        foreach (var line in File.ReadAllLines(JvmArgsFilePath, Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            result.AddRange(Tokenize(trimmed));
        }
        return result;
    }

    /// <summary>
    /// 在 token 列表中定位提供 JAR / 参数文件的那一项。
    /// </summary>
    /// <param name="tokens">命令行 token 列表。</param>
    /// <returns>索引；未找到时返回 -1。</returns>
    private static int FindJarIndex(List<string> tokens)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Equals("-jar", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
                return i;
            // Forge / NeoForge 的 @.../win_args.txt 承担 -jar 的角色
            if (tokens[i].EndsWith("win_args.txt", StringComparison.OrdinalIgnoreCase) ||
                tokens[i].EndsWith("unix_args.txt", StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 将参数字符串按空白拆分为 token，支持双引号包裹的空格。
    /// </summary>
    /// <param name="input">原始参数字符串。</param>
    /// <returns>拆分后的 token 列表。</returns>
    private static List<string> Tokenize(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in input)
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

    // ---------- 读取 ----------

    /// <summary>Java 可执行文件路径。</summary>
    public string JavaPath => _javaPath;

    /// <summary>JAR / 参数文件参数，例如 "-jar server.jar" 或 "@libraries/.../win_args.txt"。</summary>
    public string JarArgs => _jarArgs;

    /// <summary>JVM 启动参数的只读视图。</summary>
    public IReadOnlyList<string> JvmArgs => _jvmArgs;

    /// <summary>附加参数的只读视图。</summary>
    public IReadOnlyList<string> AppendArgs => _appendArgs;

    /// <summary>把 JVM 参数以单行空格分隔的形式返回（与 user_jvm_args.txt 内容一致）。</summary>
    /// <returns>JVM 参数字符串。</returns>
    public string GetJvmArgs() => string.Join(" ", _jvmArgs);

    /// <summary>把附加参数以单行空格分隔的形式返回。</summary>
    /// <returns>附加参数字符串。</returns>
    public string GetAppendArgs() => string.Join(" ", _appendArgs);

    /// <summary>获取除 Java 路径外的全部启动参数（JVM、JAR 与附加参数）。</summary>
    /// <returns>传给进程的参数串。</returns>
    public string GetJavaArgs() => $"{GetJvmArgs()} {_jarArgs} {GetAppendArgs()}".Trim();

    /// <summary>获取完整的启动命令行（含 Java 路径）。</summary>
    /// <returns>完整启动参数字符串。</returns>
    public string GetStartArguments() => $"{_javaPath} {GetJavaArgs()}".Trim();

    // ---------- 写入（自动持久化） ----------

    /// <summary>
    /// 设置 Java 可执行文件路径并持久化。路径为空时抛出异常（javaPath 不可删除）。
    /// </summary>
    /// <param name="value">新的 Java 路径。</param>
    public void SetJavaPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("javaPath 不能为空（不可删除），如需更换请使用 set 指定具体路径");

        _javaPath = value.Trim();
        SaveToScript();
    }

    /// <summary>
    /// 设置 JAR / 参数文件参数并持久化，值按 token 原样保存
    /// （"server.jar"、"@libraries/.../win_args.txt"、"-jar server.jar" 均可）；
    /// 为空时抛出异常（jarArgs 不可删除）。
    /// </summary>
    /// <param name="value">新的 jar 参数。</param>
    public void SetJarArgs(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("jarArgs 不能为空（不可删除），如需更换请使用 set 指定具体值");

        _jarArgs = value.Trim();
        SaveToScript();
    }

    /// <summary>
    /// 整体替换 JVM 参数列表并持久化（传空列表表示清空）。
    /// </summary>
    /// <param name="values">新的 JVM 参数集合。</param>
    public void SetJvmArgs(IEnumerable<string> values)
    {
        _jvmArgs.Clear();
        _jvmArgs.AddRange(values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));
        SaveToScript();
    }

    /// <summary>
    /// 整体替换附加参数列表并持久化（传空列表表示清空）。
    /// </summary>
    /// <param name="values">新的附加参数集合。</param>
    public void SetAppendArgs(IEnumerable<string> values)
    {
        _appendArgs.Clear();
        _appendArgs.AddRange(values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));
        SaveToScript();
    }

    /// <summary>
    /// 按索引替换 JVM 参数中的某一项并持久化。
    /// </summary>
    /// <param name="index">从 0 开始的位置。</param>
    /// <param name="value">新的参数值。</param>
    /// <returns>替换成功返回 true；索引越界返回 false。</returns>
    public bool SetJvmArgAt(int index, string value)
    {
        if (index < 0 || index >= _jvmArgs.Count) return false;
        _jvmArgs[index] = value;
        SaveToScript();
        return true;
    }

    /// <summary>
    /// 按索引替换附加参数中的某一项并持久化。
    /// </summary>
    /// <param name="index">从 0 开始的位置。</param>
    /// <param name="value">新的参数值。</param>
    /// <returns>替换成功返回 true；索引越界返回 false。</returns>
    public bool SetAppendArgAt(int index, string value)
    {
        if (index < 0 || index >= _appendArgs.Count) return false;
        _appendArgs[index] = value;
        SaveToScript();
        return true;
    }

    /// <summary>
    /// 追加一个 JVM 参数并持久化；参数已存在时不重复添加。
    /// </summary>
    /// <param name="value">要追加的参数。</param>
    /// <returns>实际添加成功返回 true。</returns>
    public bool AddJvmArg(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (_jvmArgs.Contains(trimmed, StringComparer.Ordinal)) return false;

        _jvmArgs.Add(trimmed);
        SaveToScript();
        return true;
    }

    /// <summary>
    /// 追加一个附加参数并持久化；参数已存在时不重复添加。
    /// </summary>
    /// <param name="value">要追加的参数。</param>
    /// <returns>实际添加成功返回 true。</returns>
    public bool AddAppendArg(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (_appendArgs.Contains(trimmed, StringComparer.Ordinal)) return false;

        _appendArgs.Add(trimmed);
        SaveToScript();
        return true;
    }

    // ---------- 删除 ----------

    /// <summary>
    /// 按索引删除一个 JVM 参数并持久化。
    /// </summary>
    /// <param name="index">从 0 开始的位置。</param>
    /// <returns>删除成功返回 true；索引越界返回 false。</returns>
    public bool RemoveJvmArgAt(int index)
    {
        if (index < 0 || index >= _jvmArgs.Count) return false;
        _jvmArgs.RemoveAt(index);
        SaveToScript();
        return true;
    }

    /// <summary>
    /// 按索引删除一个附加参数并持久化。
    /// </summary>
    /// <param name="index">从 0 开始的位置。</param>
    /// <returns>删除成功返回 true；索引越界返回 false。</returns>
    public bool RemoveAppendArgAt(int index)
    {
        if (index < 0 || index >= _appendArgs.Count) return false;
        _appendArgs.RemoveAt(index);
        SaveToScript();
        return true;
    }

    /// <summary>
    /// 按值删除一个 JVM 参数并持久化。
    /// </summary>
    /// <param name="value">要删除的参数值。</param>
    /// <returns>删除成功返回 true；未找到返回 false。</returns>
    public bool RemoveJvmArg(string value)
    {
        var index = _jvmArgs.FindIndex(a => string.Equals(a, value, StringComparison.Ordinal));
        if (index < 0) return false;

        _jvmArgs.RemoveAt(index);
        SaveToScript();
        return true;
    }

    /// <summary>
    /// 按值删除一个附加参数并持久化。
    /// </summary>
    /// <param name="value">要删除的参数值。</param>
    /// <returns>删除成功返回 true；未找到返回 false。</returns>
    public bool RemoveAppendArg(string value)
    {
        var index = _appendArgs.FindIndex(a => string.Equals(a, value, StringComparison.Ordinal));
        if (index < 0) return false;

        _appendArgs.RemoveAt(index);
        SaveToScript();
        return true;
    }

    // ---------- 持久化 ----------

    /// <summary>
    /// 将当前参数持久化到启动脚本（run.bat / run.sh）与 user_jvm_args.txt。
    /// 命令行格式固定为 <c>{javaPath} @user_jvm_args.txt {jarArgs} {appendArgs}</c>。
    /// </summary>
    private void SaveToScript()
    {
        try
        {
            var jvmContent = GetJvmArgs();
            var scriptContent = $"{QuoteIfNeeded(_javaPath)} @{JvmArgsFileName} {_jarArgs} {GetAppendArgs()}".Trim();

            // 内容未变化时不写盘，避免无谓地刷新文件时间戳
            var changed = WriteIfChanged(JvmArgsFilePath, jvmContent);
            changed |= WriteIfChanged(RunScriptPath, scriptContent);

            if (changed)
                _output.Write(_serverName, LogLevel.Success, "启动脚本和 user_jvm_args.txt 已更新");
        }
        catch (Exception ex)
        {
            _output.Write(_serverName, LogLevel.Error, $"保存启动脚本失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 仅当文件内容与目标不一致时写入。
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="content">期望内容。</param>
    /// <returns>实际发生了写入时返回 true。</returns>
    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == content)
            return false;

        File.WriteAllText(path, content, Encoding.UTF8);
        return true;
    }

    /// <summary>
    /// 按命令行惯例处理 Java 路径：含空白（或已带引号）时用双引号包裹，
    /// 避免 <c>C:\Program Files\...</c> 这类路径在脚本里被空格截断，
    /// 也保证下一次解析仍能把它当作一个整体读回。
    /// </summary>
    /// <param name="value">Java 路径。</param>
    /// <returns>写入脚本的路径文本。</returns>
    private static string QuoteIfNeeded(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            return trimmed;

        return trimmed.Any(char.IsWhiteSpace) ? $"\"{trimmed}\"" : trimmed;
    }
}
