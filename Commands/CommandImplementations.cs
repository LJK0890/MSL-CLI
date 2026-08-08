using MSL_CLI.Config;
using MSL_CLI.Server;
using MSL_CLI.Services;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using static MSL_CLI.IO.IO;

namespace MSL_CLI.Commands;

// ========== $set ==========
[Command("$set", Description = "设置配置值，用法: $set <路径> <值>")]
public class SetCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, "用法: $set <路径> <值>", true, capture, true);
            return 0;
        }

        var parts = args.Raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            Print("Command", LogLevel.WARNING, "参数不足，需要路径和值", true, capture, true);
            return 0;
        }

        string path = parts[0];
        string value = parts[1];

        try
        {
            var config = args.GlobalManager.GetConfig(); // 需要 GlobalManager 暴露配置对象
            ConfigHelper.SetValueByPath(config, path, value);
            Print("Command", LogLevel.SUCCESS, $"配置已更新: {path} = {value}", true, capture, true);
            return 1;
        }
        catch (Exception ex)
        {
            Print("Command", LogLevel.ERROR, $"设置失败: {ex.Message}", true, capture, true);
            return 0;
        }
    }
}

// ========== $get ==========
public static class GetHelper
{
    public static int Execute(CommandArgs args, bool capture = false)
    {
        try
        {
            var config = args.GlobalManager.GetConfig();
            string output;

            if (string.IsNullOrWhiteSpace(args.Raw))
            {
                // 无参数：输出全部配置
                output = FormatValue(config);
                Print("Command", LogLevel.INFO, $"全部配置 : {output}", true, capture, true);
            }
            else
            {
                // 有参数：获取指定路径
                var path = args.Raw.Trim();
                var value = ConfigHelper.GetValueByPath(config, path);
                output = FormatValue(value);
                Print("Command", LogLevel.INFO, $"{path} : {output}", true, capture, true);
            }
            return 1;
        }
        catch (Exception ex)
        {
            Print("Command", LogLevel.ERROR, $"获取失败: {ex.Message}", true, capture, true);
            return 0;
        }
    }

    private static string FormatValue(object? value, int depth = 0, HashSet<object>? visited = null)
    {
        if (value == null)
            return "(null)";

        // 防止无限递归（循环引用）
        visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (visited.Contains(value))
            return "(循环引用)";
        visited.Add(value);

        // 限制递归深度（避免超长输出）
        const int maxDepth = 5;
        if (depth > maxDepth)
            return "(嵌套过深)";

        Type type = value.GetType();

        // 基本类型（值类型、字符串等）
        if (type.IsPrimitive || type.IsEnum || value is string || value is decimal)
            return value.ToString() ?? "(null)";

        // 字典
        if (value is IDictionary dict)
        {
            var items = new List<string>();
            foreach (DictionaryEntry entry in dict)
            {
                string keyStr = FormatValue(entry.Key, depth + 1, visited);
                string valStr = FormatValue(entry.Value, depth + 1, visited);
                items.Add($"{keyStr} = {valStr}");
            }
            return $"{{{string.Join(", ", items)}}}";
        }

        // 集合/数组（排除字符串）
        if (value is IEnumerable enumerable && !(value is string))
        {
            var items = new List<string>();
            foreach (var item in enumerable)
            {
                items.Add(FormatValue(item, depth + 1, visited));
            }
            return $"[{string.Join(", ", items)}]";
        }

        // 自定义对象：反射获取公共实例属性
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0) // 忽略索引器
                        .ToList();

        if (props.Count == 0)
            return value.ToString() ?? type.Name;

        var propValues = new List<string>();
        foreach (var prop in props)
        {
            string propName = prop.Name;

            // 检查是否为敏感属性
            if (IsSensitiveProperty(propName))
            {
                // 获取原始值
                object? rawValue;
                try
                {
                    rawValue = prop.GetValue(value);
                }
                catch
                {
                    rawValue = null;
                }
                string displayValue = MaskSensitive(rawValue?.ToString());
                propValues.Add($"{propName} = {displayValue}");
            }
            else
            {
                try
                {
                    var propValue = prop.GetValue(value);
                    string formatted = FormatValue(propValue, depth + 1, visited);
                    propValues.Add($"{propName} = {formatted}");
                }
                catch
                {
                    propValues.Add($"{propName} = (获取失败)");
                }
            }
        }
        return $"{{{string.Join(", ", propValues)}}}";
    }

    private static bool IsSensitiveProperty(string propName)
    {
        // 不区分大小写判断是否包含敏感关键词
        string lower = propName.ToLowerInvariant();
        return lower.Equals("apikey") ||
               lower.Equals("password");
    }

    private static string MaskSensitive(string? value)
    {
        return string.IsNullOrEmpty(value) ? "(empty)" : "****";
    }
}

[Command("$get", Description = "获取配置值，用法: $get <路径> 或不带参数获取全部配置")]
public class GetCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false) => GetHelper.Execute(args, capture);
}

// ========== $getall ==========
[Command("$getall", Description = "获取全部配置（等效于 $get 无参数）")]
public class GetAllCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false) => GetHelper.Execute(args, capture);
}

// ========== $reload ==========
[Command("$reload", Description = "重新加载配置文件")]
public class ReloadCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false)
    {
        try
        {
            args.GlobalManager.ReloadConfig();
            Print("Command", LogLevel.SUCCESS, "配置已重新加载", true, capture, true);
            return 1;
        }
        catch (Exception ex)
        {
            Print("Command", LogLevel.ERROR, $"重新加载失败: {ex.Message}", true, capture, true);
            return 0;
        }
    }
}

// ========== $list ==========
[Command("$list", Description = "列出所有服务器")]
public class ListCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false)
    {
        var servers = args.GlobalManager.GetServerManagers();
        if (servers.Count == 0)
        {
            Print("Command", LogLevel.WARNING, "没有配置任何服务器", true, capture, true);
            return 1;
        }

        Print("Command", LogLevel.WARNING, "已配置的服务器:", true, capture, false);
        foreach (var kvp in servers)
        {
            var status = kvp.Value.Status;
            Print("Command", LogLevel.WARNING, $"  {kvp.Key} : {status}", true, capture, false);
        }
        Print("Command", LogLevel.SUCCESS, "", true, capture, true);
        return 1;
    }
}

// ========== $run ==========
[Command("$run", Description = "启动指定服务器，用法: $run <服务器名>")]
public class RunCommand : ICommand
{
    public async Task<int> ExecuteAsync(CommandArgs args,bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, "用法: $run <服务器名>", true, capture, true);
            return 0;
        }

        var name = args.Raw.Trim();
        var servers = args.GlobalManager.GetServerManagers();
        if (!servers.TryGetValue(name, out var server))
        {
            Print("Command", LogLevel.WARNING, $"未找到服务器 '{name}'", true, capture, true);
            return 0;
        }

        // 清空该服务器的缓冲区，准备捕获日志
        ClearServerBuffer(name);
        // 启动服务器（异步）
        await server.StartAsync();

        // 如果当前没有高亮服务器，则将该服务器设为高亮
        if (args.GlobalManager.HighlightedServerName == null)
        {
            args.GlobalManager.SwitchHighlight(name);
        }

        Print("Command", LogLevel.SUCCESS, $"服务器 '{name}' 已启动", true, capture, true);
        return 1;
    }

    // 同步实现 Execute，内部调用异步方法并阻塞（保持接口统一）
    public int Execute(CommandArgs args,bool capture = false)
    {

        return ExecuteAsync(args, capture).GetAwaiter().GetResult();
    }
}

// ========== $stop ==========
[Command("$stop", Description = "停止指定服务器，用法: $stop <服务器名> [-f]")]
public class StopCommand : ICommand
{
    public async Task<int> ExecuteAsync(CommandArgs args,bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, "用法: $stop <服务器名> [-f]", true, capture, true);
            return 0;
        }

        var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0];
        bool force = parts.Length > 1 && parts[1].Equals("-f", StringComparison.OrdinalIgnoreCase);

        var servers = args.GlobalManager.GetServerManagers();
        if (!servers.TryGetValue(name, out var server))
        {
            Print("Command", LogLevel.WARNING, $"未找到服务器 '{name}'", true, capture, true);
            return 0;
        }

        // 清空该服务器的缓冲区，准备捕获日志
        ClearServerBuffer(name);

        await server.StopAsync(force);

        Print("Command", LogLevel.SUCCESS, $"服务器 '{name}' 已停止", true, capture, true);
        return 1;
    }

    public int Execute(CommandArgs args,bool capture = false)
    {
        return ExecuteAsync(args, capture).GetAwaiter().GetResult();
    }
}

// ========== $help ==========
[Command("$help", Description = "显示所有可用命令的帮助信息")]
public class HelpCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false)
    {
        var descriptions = args.GlobalManager.GetCommandDescriptions();
        if (descriptions.Count == 0)
        {
            Print("Command", LogLevel.WARNING, "没有已注册的命令。", true, capture, true);
            return 1;
        }

        Print("Command", LogLevel.WARNING, "可用命令列表：", true, capture, false);
        foreach (var kvp in descriptions.OrderBy(k => k.Key))
        {
            Print("Command", LogLevel.WARNING, $"  {kvp.Key} : {kvp.Value}", true, capture, false);
        }
        Print("Command", LogLevel.SUCCESS, "", true, capture, true);    
        return 1;
    }
}

// ========== $hl / $highlight ==========
// 基础逻辑提取为静态方法，避免重复
public static class HighlightHelper
{
    public static int ExecuteHighlight(CommandArgs args,bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var current = args.GlobalManager.HighlightedServerName;
            if (current == null)
                Print("Command", LogLevel.INFO, "当前没有高亮服务器。", true, capture, true);
            else
                Print("Command", LogLevel.INFO, $"当前高亮服务器: {current}", true, capture, true);
            return 1;
        }

        var name = args.Raw.Trim();
        if (args.GlobalManager.SwitchHighlight(name))
        {
            Print("Command", LogLevel.SUCCESS, $"已切换到服务器 '{name}'", true, capture, true);
            return 1;
        }
        else
        {
            Print("Command", LogLevel.WARNING, $"未找到服务器 '{name}'", true, capture, true);
            return 0;
        }
    }
}

[Command("$hl", Description = "切换高亮服务器，用法: $hl <服务器名> 或 $hl 显示当前高亮")]
public class HighlightCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => HighlightHelper.ExecuteHighlight(args, capture);
}

[Command("$highlight", Description = "切换高亮服务器，用法: $highlight <服务器名> 或 $highlight 显示当前高亮")]
public class HighlightAliasCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => HighlightHelper.ExecuteHighlight(args, capture);
}

// ========== $rn / $runnow ==========
public static class RunNowHelper
{
    public static async Task<int> ExecuteRunNowAsync(CommandArgs args, bool capture = false)
    {
        var server = args.GlobalManager.GetHighlightedServer();
        if (server == null)
        {
            Print("Command", LogLevel.WARNING, "未设置高亮服务器，请先用 $hl/$highlight 设置。", true, capture, true);
            return 0;
        }

        // 清空该服务器的缓冲区，准备捕获日志
        ClearServerBuffer(server.GetServerName());

        await server.StartAsync();

        Print("Command", LogLevel.SUCCESS, $"服务器 '{server.GetServerName()}' 已启动", true, capture, true);
        return 1;
    }
}

[Command("$rn", Description = "运行高亮服务器 (run now)")]
public class RunNowCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => RunNowHelper.ExecuteRunNowAsync(args, capture).GetAwaiter().GetResult();
}

[Command("$runnow", Description = "运行高亮服务器")]
public class RunNowAliasCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => RunNowHelper.ExecuteRunNowAsync(args, capture).GetAwaiter().GetResult();
}

// ========== $sn / $stopnow ==========
public static class StopNowHelper
{
    public static async Task<int> ExecuteStopNowAsync(CommandArgs args, bool force = false, bool capture = false)
    {
        var server = args.GlobalManager.GetHighlightedServer();
        if (server == null)
        {
            Print("Command", LogLevel.WARNING, "未设置高亮服务器，请先用 $hl/$highlight 设置。", true, capture, true);
            return 0;
        }

        // 清空该服务器的缓冲区，准备捕获日志
        ClearServerBuffer(server.GetServerName());

        await server.StopAsync(force);

        Print("Command", LogLevel.SUCCESS, $"服务器 '{server.GetServerName()}' 已停止", true, capture, true);
        return 1;
    }
}

[Command("$sn", Description = "停止高亮服务器 (stop now)")]
public class StopNowCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => StopNowHelper.ExecuteStopNowAsync(args, false, capture).GetAwaiter().GetResult();
}

[Command("$stopnow", Description = "停止高亮服务器")]
public class StopNowAliasCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => StopNowHelper.ExecuteStopNowAsync(args, false, capture).GetAwaiter().GetResult();
}

// ========== $exit ==========
[Command("$exit", Description = "退出程序（会先停止所有服务器）")]
public class ExitCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false)
    {
        Print("Command", LogLevel.INFO, "正在退出程序，停止所有服务器...", true, capture, false);

        // 停止所有服务器（同步阻塞直到全部停止）
        args.GlobalManager.StopAllServers();

        Print("Command", LogLevel.INFO, "所有服务器已关闭，正在退出程序...", true, capture, true);
        args.GlobalManager.RequestExit(); // 设置退出标志
        return 1;
    }
}

// ========== $prtcfg / $printconfig ==========
public static class PrintConfigHelper
{
    public static int Execute(CommandArgs args, bool capture = false)
    {
        args.GlobalManager.PrintConfig(capture);
        return 1;
    }
}

[Command("$prtcfg", Description = "打印当前配置到控制台（调试用）")]
public class PrtcfgCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => PrintConfigHelper.Execute(args, capture);
}

[Command("$printconfig", Description = "打印当前配置到控制台（调试用）")]
public class PrintconfigCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => PrintConfigHelper.Execute(args, capture);
}

// ========== $configget / $cg ==========
public static class ConfigGetHelper
{
    public static int Execute(CommandArgs args, bool useHighlight = false, bool capture = false)
    {
        // 分割参数（允许为空）
        var parts = string.IsNullOrWhiteSpace(args.Raw)
            ? Array.Empty<string>()
            : args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // 确定服务器名和键名
        string? serverName = null;
        string? key = null;

        // ---------- 解析参数 ----------
        if (parts.Length == 0)
        {
            // 无参数：使用高亮服务器，输出全部配置
            if (useHighlight)
            {
                serverName = args.GlobalManager.HighlightedServerName;
                if (serverName == null)
                {
                    Print("Command", LogLevel.WARNING, "未设置高亮服务器，请先使用 hl 设置或指定服务器名", true, capture, true);
                    return 0;
                }
                // 输出全部配置（key 为 null 表示全部）
            }
            else
            {
                // configget 无参数：尝试使用高亮服务器
                serverName = args.GlobalManager.HighlightedServerName;
                if (serverName == null)
                {
                    Print("Command", LogLevel.WARNING, "未设置高亮服务器，请使用 configget <服务器名> 或先 hl 设置", true, capture, true);
                    return 0;
                }
                // 输出全部配置
            }
        }
        else if (parts.Length == 1)
        {
            // 检查第一个参数是否为已知服务器名
            var servers = args.GlobalManager.GetServerManagers();
            if (servers.ContainsKey(parts[0]))
            {
                // 是服务器名：输出该服务器的全部配置
                serverName = parts[0];
                // key 为 null
            }
            else
            {
                // 不是服务器名：视为键名，使用高亮服务器
                if (useHighlight)
                {
                    serverName = args.GlobalManager.HighlightedServerName;
                    if (serverName == null)
                    {
                        Print("Command", LogLevel.WARNING, "未设置高亮服务器，请先使用 hl 设置或指定服务器名", true, capture, true);
                        return 0;
                    }
                    key = parts[0];
                }
                else
                {
                    // configget 单参数但不是服务器名：尝试使用高亮
                    serverName = args.GlobalManager.HighlightedServerName;
                    if (serverName == null)
                    {
                        Print("Command", LogLevel.WARNING, "未设置高亮服务器，且参数不是服务器名，请指定服务器名或先 hl 设置", true, capture, true);
                        return 0;
                    }
                    key = parts[0];
                }
            }
        }
        else if (parts.Length == 2)
        {
            // 两个参数：第一个是服务器名，第二个是键名
            var servers = args.GlobalManager.GetServerManagers();
            if (servers.ContainsKey(parts[0]))
            {
                serverName = parts[0];
                key = parts[1];
            }
            else
            {
                Print("Command", LogLevel.WARNING, "第一个参数不是已知服务器名，请指定正确的服务器名", true, capture, true);
                return 0;
            }
        }
        else
        {
            Print("Command", LogLevel.WARNING, "参数过多，用法: configget [服务器名] [键名] 或 configgetnow [键名] (无参数则输出全部)", true, capture, true);
            return 0;
        }

        // ---------- 获取 ServerProperties ----------
        if (serverName == null)
        {
            Print("Command", LogLevel.WARNING, "无法确定服务器，请检查参数或设置高亮", true, capture, true);
            return 0;
        }

        var props = args.GlobalManager.GetServerProperties(serverName);
        if (props == null)
        {
            Print("Command", LogLevel.WARNING, $"未找到服务器 '{serverName}' 或无法获取配置", true, capture, true);
            return 0;
        }

        // ---------- 执行输出 ----------
        if (key == null)
        {
            // 输出全部配置
            var entries = props.Entries;
            if (entries.Count == 0)
            {
                Print("Command", LogLevel.INFO, $"服务器 '{serverName}' 的配置为空", true, capture, true);
                return 1;
            }

            Print("Command", LogLevel.INFO, $"服务器 '{serverName}' 的全部配置 ({entries.Count} 项):", true, capture, false);
            foreach (var kvp in entries)
            {
                Print("Command", LogLevel.INFO, $"  {kvp.Key} = {kvp.Value}", true, capture, false);
            }
            Print("Command", LogLevel.SUCCESS, "", true, capture, true);
            return 1;
        }
        else
        {
            // 输出单个键值
            var value = props.GetValue(key);
            if (value == null)
                Print("Command", LogLevel.INFO, $"键 '{key}' 不存在或未设置", true, capture, true);
            else
                Print("Command", LogLevel.INFO, $"{serverName}.{key} = {value}", true, capture, true);
            return 1;
        }
    }
}

[Command("$configget", Description = "获取服务器 server.properties 配置，用法: $configget <服务器名> <键> 或 $configget <键> (使用高亮服务器)")]
public class ConfigGetCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => ConfigGetHelper.Execute(args, false, capture);
}

[Command("$cg", Description = "获取服务器 server.properties 配置 ($configget 的简写)")]
public class ConfigGetShortCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => ConfigGetHelper.Execute(args, false, capture);
}

// ========== $configgetnow / $cgn ==========
[Command("$configgetnow", Description = "获取当前高亮服务器的 server.properties 配置，用法: $configgetnow <键>")]
public class ConfigGetNowCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => ConfigGetHelper.Execute(args, true, capture);
}

[Command("$cgn", Description = "获取当前高亮服务器的 server.properties 配置 ($configgetnow 的简写)")]
public class ConfigGetNowShortCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => ConfigGetHelper.Execute(args, true, capture);
}

// ========== $configset / $cs ==========
public static class ConfigSetHelper
{
    public static int Execute(CommandArgs args, bool useHighlight = false, bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, "用法: $configset <服务器名> <键> <值>  或  $configset <键> <值> (使用高亮服务器)", true, capture, true);
            return 0;
        }

        var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? serverName = null;
        string key;
        string value;

        if (parts.Length == 2)
        {
            // 只有键和值，使用高亮服务器
            if (useHighlight)
            {
                serverName = args.GlobalManager.HighlightedServerName;
                if (serverName == null)
                {
                    Print("Command", LogLevel.WARNING, "未设置高亮服务器，请先使用 $hl 设置或指定服务器名", true, capture, true);
                    return 0;
                }
                key = parts[0];
                value = parts[1];
            }
            else
            {
                Print("Command", LogLevel.WARNING, "参数不足，需要键和值，或使用 $configsetnow/$csn 直接操作高亮服务器", true, capture, true);
                return 0;
            }
        }
        else if (parts.Length >= 3)
        {
            // 第一个参数可能是服务器名，也可能是键（但值可能有空格，所以将剩余合并为值）
            var servers = args.GlobalManager.GetServerManagers();
            if (servers.ContainsKey(parts[0]))
            {
                serverName = parts[0];
                key = parts[1];
                // 剩余部分作为值（可能含空格）
                value = string.Join(" ", parts.Skip(2));
            }
            else
            {
                // 如果第一个不是服务器名，则认为是键，值从第二个开始
                if (useHighlight || args.GlobalManager.HighlightedServerName != null)
                {
                    serverName = args.GlobalManager.HighlightedServerName;
                    if (serverName == null)
                    {
                        Print("Command", LogLevel.WARNING, "未设置高亮服务器，且第一个参数不是服务器名", true, capture, true);
                        return 0;
                    }
                    key = parts[0];
                    value = string.Join(" ", parts.Skip(1));
                }
                else
                {
                    Print("Command", LogLevel.WARNING, "无法识别服务器名或高亮服务器未设置", true, capture, true);
                    return 0;
                }
            }
        }
        else
        {
            Print("Command", LogLevel.WARNING, "参数不足，用法: $configset <服务器名> <键> <值>", true, capture, true);
            return 0;
        }

        // 获取 ServerProperties
        var props = serverName != null ? args.GlobalManager.GetServerProperties(serverName) : null;
        if (props == null)
        {
            Print("Command", LogLevel.WARNING, $"未找到服务器 '{serverName}' 或无法获取配置", true, capture, true);
            return 0;
        }

        props.SetValue(key, value);
        Print("Command", LogLevel.SUCCESS, $"{serverName}.{key} = {value} 已更新", true, capture, true);
        return 1;
    }
}

[Command("$configset", Description = "设置服务器 server.properties 配置，用法: $configset <服务器名> <键> <值> 或 $configset <键> <值> (使用高亮服务器)")]
public class ConfigSetCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => ConfigSetHelper.Execute(args, false, capture);
}

[Command("$cs", Description = "设置服务器 server.properties 配置 ($configset 的简写)")]
public class ConfigSetShortCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => ConfigSetHelper.Execute(args, false, capture);
}

// ========== $configsetnow / $csn ==========
[Command("$configsetnow", Description = "设置当前高亮服务器的 server.properties 配置，用法: $configsetnow <键> <值>")]
public class ConfigSetNowCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => ConfigSetHelper.Execute(args, true, capture);
}

[Command("$csn", Description = "设置当前高亮服务器的 server.properties 配置 ($configsetnow 的简写)")]
public class ConfigSetNowShortCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => ConfigSetHelper.Execute(args, true, capture);
}

// ========== $checkop 帮助类 ==========
public static class CheckOpHelper
{
    public static int Execute(CommandArgs args, string? serverName, string? playerName, bool capture = false)
    {
        // 如果未指定服务器名，则使用高亮服务器
        if (string.IsNullOrEmpty(serverName))
        {
            serverName = args.GlobalManager.HighlightedServerName;
            if (serverName == null)
            {
                Print("Command", LogLevel.WARNING, "未设置高亮服务器，请先使用 $highlight/$hl 设置或指定服务器名", true, capture, true);
                return 0;
            }
        }

        // 检查服务器是否存在
        var servers = args.GlobalManager.GetServerManagers();
        if (!servers.ContainsKey(serverName))
        {
            Print("Command", LogLevel.WARNING, $"未找到服务器 '{serverName}'", true, capture, true);
            return 0;
        }

        var ops = args.GlobalManager.GetServerOps(serverName);
        if (ops.Count == 0)
        {
            Print("Command", LogLevel.INFO, $"服务器 '{serverName}' 没有 OP 或 ops.json 不存在/为空", true, capture, true);
            return 1;
        }

        if (string.IsNullOrEmpty(playerName))
        {
            // 输出全部 OP
            Print("Command", LogLevel.INFO, $"服务器 '{serverName}' 的 OP 列表 ({ops.Count} 个):", true, capture, false);
            foreach (var name in ops)
            {
                Print("Command", LogLevel.INFO, $"  {name}", true, capture, false);
            }
            Print("Command", LogLevel.SUCCESS, "", true, capture, true);
        }
        else
        {
            // 检查特定玩家
            if (args.GlobalManager.IsServerOp(serverName, playerName))
                Print("Command", LogLevel.SUCCESS, $"玩家 '{playerName}' 是服务器 '{serverName}' 的 OP", true, capture, true);
            else
                Print("Command", LogLevel.INFO, $"玩家 '{playerName}' 不是服务器 '{serverName}' 的 OP", true, capture, true);
        }

        return 1;
    }
}

// ========== $checkop ==========
[Command("$checkop", Description = "检查服务器 OP 列表或指定玩家是否为 OP，用法: $checkop <服务器名> [玩家名] (不指定玩家则输出全部 OP)")]
public class CheckOpCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, "用法: $checkop <服务器名> [玩家名]", true, capture, true);
            return 0;
        }

        var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string serverName = parts[0];
        string? playerName = parts.Length > 1 ? parts[1] : null;

        return CheckOpHelper.Execute(args, serverName, playerName, capture);
    }
}

// ========== $checkopn / $checkopnow ==========
[Command("$checkopn", Description = "检查高亮服务器的 OP 列表或指定玩家是否为 OP，用法: $checkopn [玩家名] (不指定玩家则输出全部 OP)")]
public class CheckOpNowCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false)
    {
        string? playerName = null;
        if (!string.IsNullOrWhiteSpace(args.Raw))
        {
            var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // 只允许一个参数（玩家名），多个则报错
            if (parts.Length > 1)
            {
                Print("Command", LogLevel.WARNING, "参数过多，用法: $checkopn [玩家名]", true, capture, true);
                return 0;
            }
            playerName = parts[0];
        }
        // 调用帮助类，服务器名为 null（表示使用高亮）
        return CheckOpHelper.Execute(args, null, playerName, capture);
    }
}

[Command("$checkopnow", Description = "检查高亮服务器的 OP 列表或指定玩家是否为 OP ($checkopn 的完整写法)")]
public class CheckOpNowAliasCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false)
    {
        string? playerName = null;
        if (!string.IsNullOrWhiteSpace(args.Raw))
        {
            var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                Print("Command", LogLevel.WARNING, "参数过多，用法: $checkopnow [玩家名]", true, capture, true);
                return 0;
            }
            playerName = parts[0];
        }
        return CheckOpHelper.Execute(args, null, playerName, capture);
    }
}

// ========== $send / $sendn ==========
public static class SendCommandHelper
{
    public static int Execute(CommandArgs args, bool useHighlight = false, bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, "用法: send <服务器名> <命令>  或  sendn <命令> (发送到高亮服务器)", true, capture, true);
            return 0;
        }

        string? serverName = null;
        string command;

        if (useHighlight)
        {
            // sendn：第一个参数是命令，命令可能包含空格，所以整个 Raw 就是命令
            serverName = args.GlobalManager.HighlightedServerName;
            if (serverName == null)
            {
                Print("Command", LogLevel.WARNING, "未设置高亮服务器，请先使用 hl 设置", true, capture, true);
                return 0;
            }
            command = args.Raw.Trim();
        }
        else
        {
            // send：第一个参数是服务器名，剩余部分是命令
            var parts = args.Raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                Print("Command", LogLevel.WARNING, "用法: send <服务器名> <命令>", true, capture, true);
                return 0;
            }
            serverName = parts[0];
            command = parts[1].Trim();
        }

        // 获取服务器管理器
        var servers = args.GlobalManager.GetServerManagers();
        if (!servers.TryGetValue(serverName, out var server))
        {
            Print("Command", LogLevel.WARNING, $"未找到服务器 '{serverName}'", true, capture, true);
            return 0;
        }

        // 检查服务器是否运行中
        if (server.Status != ServerStatus.Running)
        {
            Print("Command", LogLevel.WARNING, $"服务器 '{serverName}' 未运行，无法发送命令", true, capture, true);
            return 0;
        }

        // 发送命令（异步转同步）
        try
        {
            // 清空该服务器的缓冲区，准备捕获日志
            ClearServerBuffer(server.GetServerName());

            server.SendCommandAsync(command).GetAwaiter().GetResult();
            Print("Command", LogLevel.INFO, $"已向服务器 '{serverName}' 发送命令: {command}", true, capture, true);
            return 1;
        }
        catch (Exception ex)
        {
            Print("Command", LogLevel.ERROR, $"发送命令失败: {ex.Message}", true, capture, true);
            return 0;
        }
    }
}

[Command("$send", Description = "向指定服务器发送 Minecraft 命令，用法: $send <服务器名> <命令>")]
public class SendCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => SendCommandHelper.Execute(args, false, capture);
}

[Command("$sendn", Description = "向当前高亮服务器发送 Minecraft 命令，用法: $sendn <命令>")]
public class SendNowCommand : ICommand
{
    public int Execute(CommandArgs args,bool capture = false) => SendCommandHelper.Execute(args, true, capture);
}

// ========== $chat ==========
[Command("$chat", Description = "与 AI 进行纯文本对话（无工具调用），用法: $chat [配置名] <消息> (默认使用 'default' 配置)")]
public class ChatCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false)
    {
        // 参数解析...
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, "用法: $chat [配置名] <消息>", true, capture, true);
            return 0;
        }

        var parts = args.Raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string configName = "default";
        string message;

        var aiModule = args.GlobalManager.AI;
        if (aiModule == null)
        {
            Print("Command", LogLevel.ERROR, "AI 模块未初始化", true, capture, true);
            return 0;
        }

        var configNames = aiModule.GetConfigNames().ToList();
        if (configNames.Contains(parts[0]))
        {
            configName = parts[0];
            message = parts.Length > 1 ? parts[1] : string.Empty;
        }
        else
        {
            message = args.Raw;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            Print("Command", LogLevel.WARNING, "消息不能为空", true, capture, true);
            return 0;
        }

        // 启动后台任务，不等待
        Task.Run(async () =>
        {
            try
            {
                string result = await aiModule.ChatAsync(configName, message);
                // 直接输出结果（忽略 capture，因为命令已返回）
                Print($"AI/{configName}", LogLevel.SUCCESS, result, true);
            }
            catch (Exception ex)
            {
                Print($"AI/{configName}", LogLevel.ERROR, $"AI 对话失败: {ex.Message}", true);
            }
        });

        // 立即返回，提示用户等待
        Print("Command", LogLevel.INFO, "AI 对话已启动，请等待响应...", true, capture, true);
        return 1;
    }
}

// ========== $agent ==========
[Command("$agent", Description = "AI 代理模式，可执行命令（工具调用），用法: $agent [配置名] <指令> (默认使用 'default' 配置)")]
public class AgentCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, "用法: $agent [配置名] <指令>", true, capture, true);
            return 0;
        }

        var parts = args.Raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string configName = "default";
        string instruction;

        var aiModule = args.GlobalManager.AI;
        if (aiModule == null)
        {
            Print("Command", LogLevel.ERROR, "AI 模块未初始化", true, capture, true);
            return 0;
        }

        var configNames = aiModule.GetConfigNames().ToList();
        if (configNames.Contains(parts[0]))
        {
            configName = parts[0];
            instruction = parts.Length > 1 ? parts[1] : string.Empty;
        }
        else
        {
            instruction = args.Raw;
        }

        if (string.IsNullOrWhiteSpace(instruction))
        {
            Print("Command", LogLevel.WARNING, "指令不能为空", true, capture, true);
            return 0;
        }

        // 启动后台任务，不等待
        Task.Run(async () =>
        {
            try
            {
                var (model, message) = await aiModule.AgentAsync(configName, instruction);
                Print($"AI/{model}", LogLevel.SUCCESS, message, true);
            }
            catch (Exception ex)
            {
                Print($"AI", LogLevel.ERROR, $"AI 代理执行失败: {ex.Message}", true);
            }
        });

        // 立即返回，提示用户等待
        Print("Command", LogLevel.INFO, "AI 代理已启动，请等待响应...", true, capture, true);
        return 1;
    }
}

// ========== $query / $queryn ==========
public static class QueryHelper
{
    public static int Execute(CommandArgs args, bool useHighlight = false, bool capture = false)
    {
        string? serverName = null;

        if (useHighlight)
        {
            // $queryn：无参数，使用高亮服务器
            if (!string.IsNullOrWhiteSpace(args.Raw))
            {
                Print("Command", LogLevel.WARNING, "用法: $queryn 无参数，使用高亮服务器", true, capture, true);
                return 0;
            }
            serverName = args.GlobalManager.HighlightedServerName;
            if (string.IsNullOrEmpty(serverName))
            {
                Print("Command", LogLevel.WARNING, "未设置高亮服务器，请先使用 $hl 设置", true, capture, true);
                return 0;
            }
        }
        else
        {
            // $query <服务器名>
            if (string.IsNullOrWhiteSpace(args.Raw))
            {
                Print("Command", LogLevel.WARNING, "用法: $query <服务器名>", true, capture, true);
                return 0;
            }
            var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                Print("Command", LogLevel.WARNING, "参数过多，用法: $query <服务器名>", true, capture, true);
                return 0;
            }
            serverName = parts[0];
        }

        var servers = args.GlobalManager.GetServerManagers();
        if (!servers.TryGetValue(serverName, out var server))
        {
            Print("Command", LogLevel.WARNING, $"未找到服务器 '{serverName}'", true, capture, true);
            return 0;
        }

        try
        {
            var info = server.GetServerQueryInfoAsync().GetAwaiter().GetResult();
            if (info == null)
            {
                Print("Command", LogLevel.WARNING, $"无法获取服务器 '{serverName}' 的 Query 信息（可能未启用或未运行）", true, capture, true);
                return 0;
            }

            Print("Command", LogLevel.INFO, $"服务器 '{serverName}' 的 Query 信息:", true, capture, false);
            foreach (var kvp in info)
            {
                Print("Command", LogLevel.INFO, $"  {kvp.Key} = {kvp.Value}", true, capture, false);
            }
            Print("Command", LogLevel.SUCCESS, "", true, capture, true);
            return 1;
        }
        catch (Exception ex)
        {
            Print("Command", LogLevel.ERROR, $"查询失败: {ex.Message}", true, capture, true);
            return 0;
        }
    }
}

[Command("$query", Description = "查询指定服务器的 Query 信息，用法: $query <服务器名>")]
public class QueryCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false) => QueryHelper.Execute(args, false, capture);
}

[Command("$queryn", Description = "查询当前高亮服务器的 Query 信息，用法: $queryn")]
public class QueryNowCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false) => QueryHelper.Execute(args, true, capture);
}

[Command("$qn", Description = "查询当前高亮服务器的 Query 信息，用法: $qn")]
public class QueryNowAliasCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false) => QueryHelper.Execute(args, true, capture);
}

// ========== $readbuffer / $rbuf ==========
[Command("$readbuffer", Description = "读取指定服务器的缓冲区内容（不清空），用法: $readbuffer <服务器名>")]
public class ReadBufferCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, "用法: $readbuffer <服务器名>", true, capture, true);
            return 0;
        }

        var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1)
        {
            Print("Command", LogLevel.WARNING, "参数错误，用法: $readbuffer <服务器名>", true, capture, true);
            return 0;
        }

        string serverName = parts[0];
        var servers = args.GlobalManager.GetServerManagers();
        if (!servers.ContainsKey(serverName))
        {
            Print("Command", LogLevel.WARNING, $"未找到服务器 '{serverName}'", true, capture, true);
            return 0;
        }

        string content = GetServerBuffer(serverName);
        if (string.IsNullOrEmpty(content))
        {
            Print("Command", LogLevel.INFO, $"服务器 '{serverName}' 的缓冲区为空", true, capture, true);
            return 1;
        }

        Print("Command", LogLevel.INFO, $"服务器 '{serverName}' 的缓冲区内容:", true, capture, false);
        Print("Command", LogLevel.INFO, content, true, capture, true);
        return 1;
    }
}

[Command("$rbuf", Description = "读取指定服务器的缓冲区内容（$readbuffer 的简写）")]
public class RbufCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false) => new ReadBufferCommand().Execute(args, capture);
}

// ========== $updatebuffer / $ubuf ==========
[Command("$updatebuffer", Description = "读取并清空指定服务器的缓冲区，用法: $updatebuffer <服务器名>")]
public class UpdateCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, "用法: $updatebuffer <服务器名>", true, capture, true);
            return 0;
        }

        var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1)
        {
            Print("Command", LogLevel.WARNING, "参数错误，用法: $updatebuffer <服务器名>", true, capture, true);
            return 0;
        }

        string serverName = parts[0];
        var servers = args.GlobalManager.GetServerManagers();
        if (!servers.ContainsKey(serverName))
        {
            Print("Command", LogLevel.WARNING, $"未找到服务器 '{serverName}'", true, capture, true);
            return 0;
        }

        string content = GetAndClearServerBuffer(serverName);
        if (string.IsNullOrEmpty(content))
        {
            Print("Command", LogLevel.INFO, $"服务器 '{serverName}' 的缓冲区为空（已清空）", true, capture, true);
            return 1;
        }

        Print("Command", LogLevel.INFO, $"服务器 '{serverName}' 的缓冲区内容（已清空）:", true, capture, false);
        Print("Command", LogLevel.INFO, content, true, capture, true);
        return 1;
    }
}

[Command("$ubuf", Description = "读取并清空指定服务器的缓冲区（$updatebuffer 的简写）")]
public class UbufCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false) => new UpdateCommand().Execute(args, capture);
}

// ========== $file ==========
[Command("$file", Description = "文件操作（仅限白名单目录），子命令: read <路径>, write <路径> <内容>, list <路径>, delete <路径>。支持占位符：%appdata% (不区分大小写)、%<服务器名>% (区分大小写)")]
public class FileCommand : ICommand
{
    private static List<string> _allowedBaseDirs = new();
    private static readonly Dictionary<string, string> _serverPathMap = new(StringComparer.Ordinal);
    private static string _appDataPath = string.Empty;
    private static bool _initialized = false;
    private static readonly object _initLock = new();

    private static void Initialize(GlobalManager gm)
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;

            _appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appDir = Path.Combine(_appDataPath, AppConstants.AppName);
            _allowedBaseDirs.Add(appDir);

            var servers = gm.GetServerManagers();
            foreach (var kv in servers)
            {
                string name = kv.Key; // 保留原始大小写（服务器名区分大小写）
                string path = Path.GetFullPath(kv.Value.GetServerPath());
                _allowedBaseDirs.Add(path);
                _serverPathMap[name] = path;
            }
            _allowedBaseDirs = _allowedBaseDirs.Distinct().ToList();
            _initialized = true;
        }
    }

    private static string ResolvePath(string inputPath, GlobalManager gm)
    {
        Initialize(gm);

        // 1. 替换 %appdata% (不区分大小写)
        string appdataPattern = "%appdata%";
        int idx = inputPath.IndexOf(appdataPattern, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            inputPath = inputPath.Remove(idx, appdataPattern.Length)
                               .Insert(idx, _appDataPath);
        }

        // 2. 替换 %<服务器名>% (区分大小写)
        foreach (var kv in _serverPathMap)
        {
            string placeholder = "%" + kv.Key + "%";
            // 注意：区分大小写，使用 Ordinal 比较
            if (inputPath.Contains(placeholder, StringComparison.Ordinal))
            {
                inputPath = inputPath.Replace(placeholder, kv.Value);
            }
        }

        return inputPath;
    }

    private static bool IsPathAllowed(string fullPath, GlobalManager gm)
    {
        Initialize(gm);
        foreach (var baseDir in _allowedBaseDirs)
        {
            if (fullPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public int Execute(CommandArgs args, bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, "用法: $file <子命令> <路径> [内容]", true, capture, true);
            return 0;
        }

        var parts = args.Raw.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            Print("Command", LogLevel.WARNING, "子命令和路径必须指定", true, capture, true);
            return 0;
        }

        string subCmd = parts[0].ToLowerInvariant();
        string rawPath = parts[1];
        string? content = parts.Length > 2 ? parts[2] : null;

        // 解析占位符
        string resolvedPath = ResolvePath(rawPath, args.GlobalManager);

        // 转换为绝对路径
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(resolvedPath);
        }
        catch
        {
            Print("Command", LogLevel.WARNING, $"无效路径: {resolvedPath}", true, capture, true);
            return 0;
        }

        // 安全校验
        if (!IsPathAllowed(fullPath, args.GlobalManager))
        {
            Print("Command", LogLevel.WARNING, $"路径 '{fullPath}' 不在允许的目录中", true, capture, true);
            return 0;
        }

        try
        {
            switch (subCmd)
            {
                case "read":
                    if (!File.Exists(fullPath))
                    {
                        Print("Command", LogLevel.WARNING, $"文件不存在: {fullPath}", true, capture, true);
                        return 0;
                    }
                    string fileContent = File.ReadAllText(fullPath, Encoding.UTF8);
                    Print("Command", LogLevel.INFO, $"文件内容 ({fullPath}):", true, capture, false);
                    Print("Command", LogLevel.INFO, fileContent, true, capture, true);
                    return 1;

                case "write":
                    if (content == null)
                    {
                        Print("Command", LogLevel.WARNING, "写入内容不能为空", true, capture, true);
                        return 0;
                    }
                    string? dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(fullPath, content, Encoding.UTF8);
                    Print("Command", LogLevel.SUCCESS, $"已写入文件: {fullPath}", true, capture, true);
                    return 1;

                case "list":
                    if (!Directory.Exists(fullPath))
                    {
                        Print("Command", LogLevel.WARNING, $"目录不存在: {fullPath}", true, capture, true);
                        return 0;
                    }
                    var entries = Directory.GetFileSystemEntries(fullPath);
                    if (entries.Length == 0)
                    {
                        Print("Command", LogLevel.INFO, $"目录为空: {fullPath}", true, capture, true);
                        return 1;
                    }
                    Print("Command", LogLevel.INFO, $"目录内容 ({fullPath}):", true, capture, false);
                    foreach (var entry in entries)
                    {
                        string type = Directory.Exists(entry) ? "[DIR]" : "[FILE]";
                        Print("Command", LogLevel.INFO, $"  {type} {Path.GetFileName(entry)}", true, capture, false);
                    }
                    Print("Command", LogLevel.SUCCESS, "", true, capture, true);
                    return 1;

                case "delete":
                    if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                    {
                        Print("Command", LogLevel.WARNING, $"路径不存在: {fullPath}", true, capture, true);
                        return 0;
                    }
                    if (Directory.Exists(fullPath))
                    {
                        if (Directory.GetFileSystemEntries(fullPath).Length > 0)
                        {
                            Print("Command", LogLevel.WARNING, "目录非空，拒绝删除", true, capture, true);
                            return 0;
                        }
                        Directory.Delete(fullPath);
                    }
                    else
                    {
                        File.Delete(fullPath);
                    }
                    Print("Command", LogLevel.SUCCESS, $"已删除: {fullPath}", true, capture, true);
                    return 1;

                default:
                    Print("Command", LogLevel.WARNING, $"未知子命令: {subCmd}，支持: read, write, list, delete", true, capture, true);
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Print("Command", LogLevel.ERROR, $"文件操作失败: {ex.Message}", true, capture, true);
            return 0;
        }
    }
}

// ========== $checkwhitelist ==========
[Command("$checkwhitelist", Description = "检查服务器的白名单列表，用法: $checkwhitelist <服务器名> [玩家名] (不指定玩家则输出全部)")]
public class CheckWhitelistCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false)
    {
        return CheckListHelper.Execute(args, "whitelist.json", "白名单", capture);
    }
}

// ========== $checkban ==========
[Command("$checkban", Description = "检查服务器的封禁列表（banned-players.json），用法: $checkban <服务器名> [玩家名] (不指定玩家则输出全部)")]
public class CheckBanCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false)
    {
        return CheckListHelper.Execute(args, "banned-players.json", "封禁玩家", capture);
    }
}

// ========== $checkbanip ==========
[Command("$checkbanip", Description = "检查服务器的IP封禁列表（banned-ips.json），用法: $checkbanip <服务器名> [IP] (不指定IP则输出全部)")]
public class CheckBanIpCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false)
    {
        return CheckListHelper.Execute(args, "banned-ips.json", "封禁IP", capture);
    }
}

public static class CheckListHelper
{
    public static int Execute(CommandArgs args, string fileName, string listType, bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, $"用法: $check{listType} <服务器名> [名称]", true, capture, true);
            return 0;
        }

        var parts = args.Raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string serverName = parts[0];
        string? targetName = parts.Length > 1 ? parts[1] : null;

        var servers = args.GlobalManager.GetServerManagers();
        if (!servers.TryGetValue(serverName, out var sm))
        {
            Print("Command", LogLevel.WARNING, $"未找到服务器 '{serverName}'", true, capture, true);
            return 0;
        }

        string filePath = Path.Combine(sm.GetServerPath(), fileName);
        if (!File.Exists(filePath))
        {
            Print("Command", LogLevel.INFO, $"服务器 '{serverName}' 的 {fileName} 不存在，可能未启用对应功能", true, capture, true);
            return 1;
        }

        try
        {
            string json = File.ReadAllText(filePath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                Print("Command", LogLevel.WARNING, $"{fileName} 格式无效（不是数组）", true, capture, true);
                return 0;
            }

            var entries = new List<string>();
            foreach (var element in root.EnumerateArray())
            {
                string? name = null;
                if (element.TryGetProperty("name", out var nameElem))
                    name = nameElem.GetString();
                else if (element.TryGetProperty("ip", out var ipElem)) // 针对 banned-ips
                    name = ipElem.GetString();

                if (!string.IsNullOrEmpty(name))
                    entries.Add(name);
            }

            if (entries.Count == 0)
            {
                Print("Command", LogLevel.INFO, $"服务器 '{serverName}' 的 {listType} 列表为空", true, capture, true);
                return 1;
            }

            if (string.IsNullOrEmpty(targetName))
            {
                Print("Command", LogLevel.INFO, $"服务器 '{serverName}' 的 {listType} 列表 ({entries.Count} 个):", true, capture, false);
                foreach (var entry in entries)
                    Print("Command", LogLevel.INFO, $"  {entry}", true, capture, false);
                Print("Command", LogLevel.SUCCESS, "", true, capture, true);
                return 1;
            }
            else
            {
                bool found = entries.Contains(targetName, StringComparer.OrdinalIgnoreCase);
                if (found)
                    Print("Command", LogLevel.SUCCESS, $"'{targetName}' 在服务器 '{serverName}' 的 {listType} 列表中", true, capture, true);
                else
                    Print("Command", LogLevel.INFO, $"'{targetName}' 不在服务器 '{serverName}' 的 {listType} 列表中", true, capture, true);
                return 1;
            }
        }
        catch (Exception ex)
        {
            Print("Command", LogLevel.ERROR, $"读取 {fileName} 失败: {ex.Message}", true, capture, true);
            return 0;
        }
    }
}

// ========== $backup ==========
[Command("$backup", Description = "备份指定服务器的世界文件到 backups 目录，用法: $backup <服务器名> [备注]")]
public class BackupCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, "用法: $backup <服务器名> [备注]", true, capture, true);
            return 0;
        }

        var parts = args.Raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string serverName = parts[0];
        string? remark = parts.Length > 1 ? parts[1] : null;

        var servers = args.GlobalManager.GetServerManagers();
        if (!servers.TryGetValue(serverName, out var sm))
        {
            Print("Command", LogLevel.WARNING, $"未找到服务器 '{serverName}'", true, capture, true);
            return 0;
        }

        // 检查服务器是否运行中（运行时备份可能损坏，建议用户先停止）
        if (sm.Status == ServerStatus.Running)
        {
            Print("Command", LogLevel.WARNING, "服务器正在运行，备份可能不一致，建议先停止。是否继续？(使用 -f 强制备份)", true, capture, true);
            // 这里简单处理：提醒后仍执行，或要求加 -f 参数。
            // 为简化，我们允许备份，但输出警告。
        }

        string serverPath = sm.GetServerPath();
        string worldFolder = Path.Combine(serverPath, "world"); // 假设世界目录名为 world，可能不同，但我们可尝试检测
        // 或者检测 server.properties 中的 level-name 属性
        string levelName = sm.ServerProperties.GetValue("level-name", "world");
        string worldDir = Path.Combine(serverPath, levelName);
        if (!Directory.Exists(worldDir))
        {
            Print("Command", LogLevel.WARNING, $"世界目录不存在: {worldDir}", true, capture, true);
            return 0;
        }

        string backupsDir = Path.Combine(serverPath, "backups");
        Directory.CreateDirectory(backupsDir);

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string backupName = string.IsNullOrEmpty(remark) ? timestamp : $"{timestamp}_{remark}";
        string backupPath = Path.Combine(backupsDir, backupName);

        try
        {
            Print("Command", LogLevel.INFO, $"正在备份 '{worldDir}' 到 '{backupPath}' ...", true, capture, false);
            // 使用 System.IO.Compression.ZipFile 进行压缩
            System.IO.Compression.ZipFile.CreateFromDirectory(worldDir, backupPath + ".zip");
            Print("Command", LogLevel.SUCCESS, $"备份完成: {backupPath}.zip", true, capture, true);
            return 1;
        }
        catch (Exception ex)
        {
            Print("Command", LogLevel.ERROR, $"备份失败: {ex.Message}", true, capture, true);
            return 0;
        }
    }
}

// ========== $status ==========
[Command("$status", Description = "查看指定服务器或所有服务器的资源占用（CPU/内存），用法: $status [服务器名] (不指定则显示全部)")]
public class StatusCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false)
    {
        var servers = args.GlobalManager.GetServerManagers();
        List<ServerManager> targets;

        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            targets = servers.Values.ToList();
        }
        else
        {
            string name = args.Raw.Trim();
            if (!servers.TryGetValue(name, out var sm))
            {
                Print("Command", LogLevel.WARNING, $"未找到服务器 '{name}'", true, capture, true);
                return 0;
            }
            targets = new List<ServerManager> { sm };
        }

        bool anyRunning = false;
        foreach (var sm in targets)
        {
            var proc = sm.GetProcess();
            if (proc == null || proc.HasExited)
            {
                Print("Command", LogLevel.INFO, $"服务器 '{sm.GetServerName()}' 未运行", true, capture, false);
                continue;
            }
            anyRunning = true;
            try
            {
                proc.Refresh();
                long memoryMB = proc.WorkingSet64 / (1024 * 1024);
                double cpuTime = proc.TotalProcessorTime.TotalSeconds;
                // 获取 CPU 使用率需要计算两次采样，这里简单显示总 CPU 时间
                Print("Command", LogLevel.INFO, $"服务器 '{sm.GetServerName()}' - PID: {proc.Id}, 内存: {memoryMB} MB, CPU时间: {cpuTime:F2}s", true, capture, false);
            }
            catch (Exception ex)
            {
                Print("Command", LogLevel.WARNING, $"获取 '{sm.GetServerName()}' 状态失败: {ex.Message}", true, capture, false);
            }
        }

        if (!anyRunning)
        {
            Print("Command", LogLevel.INFO, "没有正在运行的服务器", true, capture, true);
            return 1;
        }
        Print("Command", LogLevel.SUCCESS, "", true, capture, true);
        return 1;
    }
}

// ========== $sendall ==========
[Command("$sendall", Description = "向所有正在运行的服务器发送命令，用法: $sendall <命令>")]
public class SendAllCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            Print("Command", LogLevel.WARNING, "用法: $sendall <命令>", true, capture, true);
            return 0;
        }

        string command = args.Raw.Trim();
        var running = args.GlobalManager.GetRunningServers();
        if (running.Count == 0)
        {
            Print("Command", LogLevel.WARNING, "没有正在运行的服务器", true, capture, true);
            return 0;
        }

        int success = 0;
        int fail = 0;
        foreach (var sm in running)
        {
            try
            {
                sm.SendCommandAsync(command).GetAwaiter().GetResult();
                Print("Command", LogLevel.INFO, $"已向 '{sm.GetServerName()}' 发送命令: {command}", true, capture, false);
                success++;
            }
            catch (Exception ex)
            {
                Print("Command", LogLevel.ERROR, $"向 '{sm.GetServerName()}' 发送命令失败: {ex.Message}", true, capture, false);
                fail++;
            }
        }
        Print("Command", LogLevel.SUCCESS, $"发送完成: 成功 {success}, 失败 {fail}", true, capture, true);
        return 1;
    }
}

// ========== $stopall ==========
[Command("$stopall", Description = "停止所有正在运行的服务器（优雅关闭）")]
public class StopAllCommand : ICommand
{
    public int Execute(CommandArgs args, bool capture = false)
    {
        // 直接调用 GlobalManager 的 StopAllServers 方法，但需要异步等待
        try
        {
            args.GlobalManager.StopAllServers(); // 同步阻塞，但不会卡死主线程太久
            Print("Command", LogLevel.SUCCESS, "所有服务器已停止", true, capture, true);
            return 1;
        }
        catch (Exception ex)
        {
            Print("Command", LogLevel.ERROR, $"停止所有服务器失败: {ex.Message}", true, capture, true);
            return 0;
        }
    }
}