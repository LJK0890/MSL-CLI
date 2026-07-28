using System.Formats.Tar;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MSL_CLI.Models;

internal class ServerArgument
{
    private string javaPath = "java";
    private string userJvmArgs = "-Xms2500M -Xmx4G";
    private string jarArgs = "-jar server.jar";
    private string appendArgs = "nogui";

    public ServerArgument(string filePath)
    {
        // 1. 读取 run.bat 或 run.sh（根据平台选择）
        string runFile = Path.Combine(filePath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)?"run.bat":"run.sh");
        if (!File.Exists(runFile)) { return; }
        // 2. 读取 run.bat 或 run.sh 的内容，提取默认参数
        string defaultArgsWithREM = File.ReadAllText(runFile, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(defaultArgsWithREM)) {  return; }


        // 3. 提取有效启动参数（忽略nogui）
        string defaultArgsWithJAVA = string.Empty;
        foreach(var line in defaultArgsWithREM.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("\"") || trimmed.StartsWith("java"))
            {
                defaultArgsWithJAVA = trimmed;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(defaultArgsWithJAVA)) { return; }
        // 4. 提取并去除 java 路径（第一个token）
        string pattern = @"^(\s*)(""[^""]*""|\S+)\s*";
        Match match = Regex.Match(defaultArgsWithJAVA, pattern);
        if (!match.Success) { return; }
        string defaultArgsWithJar = defaultArgsWithJAVA.Substring(match.Index + match.Length);
        string javaPathWithQuotes = match.Groups[2].Value;
        javaPath = javaPathWithQuotes.StartsWith("\"") && javaPathWithQuotes.EndsWith("\"")
            ? javaPathWithQuotes.Trim('"')
            : javaPathWithQuotes;
        // 5. 按空格分词，提取参数
        List<string> tokens = Tokenize(defaultArgsWithJar);
        if (tokens.Count == 0) { return; }
        // 6. 内部展开 @ 文件（排除 @win_args.txt / @unix_args.txt）
        tokens = ExpandAtFiles(filePath, tokens);
        // 7. 找到 -jar 的位置
        int jarIndex = -1;
        for (int i = 1; i < tokens.Count; i++)
        {
            if (tokens[i].StartsWith("-jar"))
            {
                jarIndex = i;
                jarArgs = $"-jar {tokens[i + 1]}";
                break;
            }
            else if (tokens[i].StartsWith("@"))
            {
                jarIndex = i;
                jarArgs = tokens[i];
                break;
            }
        }
        if (jarIndex == -1) { return; }
        // 8. 提取 java 和 -jar 之间的所有 token 作为 JVM 参数
        var jvmTokens = new List<string>();
        for (int i = 0; i < jarIndex; i++)
        {
            if (tokens[i].StartsWith("%"))
                continue;
            if (tokens[i].Equals("nogui", StringComparison.OrdinalIgnoreCase))
                break;
            jvmTokens.Add(tokens[i]);
        }
        if (jvmTokens.Count <= 0) { return; }
        userJvmArgs = string.Join(" ", jvmTokens);
    }

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
                    continue;
                }

                // 内部展开：读取文件内容并按空格分词
                string atFilePath = Path.Combine(filePath, fileName);
                if (File.Exists(atFilePath))
                {
                    string atContent = File.ReadAllText(atFilePath, Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(atContent))
                    {
                        // 递归分词（@文件内容本身也可能包含 @ 引用，按需决定是否递归）
                        expanded.AddRange(Tokenize(atContent));
                    }
                }
            }
            else
            {
                expanded.Add(token);
            }
        }

        return expanded;
    }

    private static List<string> Tokenize(string input)
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

            if ((c == ' ' || c == '\t') && !inQuotes)
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
    public string GetStartArguments()
    {
        return $"{javaPath} {userJvmArgs} {jarArgs} {appendArgs}".Trim();
    }
    public void PrintArguments()
    {
        Console.WriteLine("Java Path: " + javaPath);
        Console.WriteLine("User JVM Args: " + userJvmArgs);
        Console.WriteLine("Jar Args: " + jarArgs);
        Console.WriteLine("Append Args: " + appendArgs);
        Console.WriteLine("Full Start Arguments: " + GetStartArguments());
    }
}