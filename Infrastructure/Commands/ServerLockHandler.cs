using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// 服务器锁定状态的判定与过滤辅助。
/// 锁定只禁止 <c>run</c>/<c>stop</c>/<c>send</c>/<c>rm</c>/<c>del</c> 五类操作，
/// 配置、查看、备份、文件等操作不受影响；解锁用 <c>$server ulk</c>。
/// 状态统一从配置文件读取（<c>$server lk/ulk</c> 即时写盘），因此无需缓存或重载。
/// </summary>
internal static class ServerLock
{
    /// <summary>
    /// 判断服务器是否已锁定。
    /// </summary>
    /// <param name="args">命令参数，包含配置存储。</param>
    /// <param name="name">服务器名。</param>
    /// <returns>已锁定返回 true。</returns>
    public static bool IsLocked(CommandArgs args, string name) => IsLocked(args.ConfigStore.LoadConfig(), name);

    /// <summary>
    /// 判断服务器是否已锁定。
    /// </summary>
    /// <param name="config">当前配置。</param>
    /// <param name="name">服务器名。</param>
    /// <returns>已锁定返回 true。</returns>
    public static bool IsLocked(AppConfig config, string name)
        => config.LockedServers.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 统一的拒绝说明。
    /// </summary>
    /// <param name="name">服务器名。</param>
    /// <returns>提示文本。</returns>
    public static string LockedMessage(string name)
        => $"服务器 '{name}' 已锁定：run/stop/send/rm/del 一律拒绝，$server ulk {name} 可解锁";

    /// <summary>
    /// 从目标列表中剔除锁定服务器。
    /// </summary>
    /// <param name="servers">候选服务器。</param>
    /// <param name="config">当前配置。</param>
    /// <param name="skipped">被跳过的锁定服务器名。</param>
    /// <returns>可继续执行的目标列表。</returns>
    public static List<IServer> SkipLocked(IEnumerable<IServer> servers, AppConfig config, out List<string> skipped)
    {
        skipped = new List<string>();
        var result = new List<IServer>();

        foreach (var server in servers)
        {
            if (IsLocked(config, server.Name)) skipped.Add(server.Name);
            else result.Add(server);
        }

        return result;
    }

    /// <summary>
    /// 生成“已跳过锁定服务器”的提示后缀。
    /// </summary>
    /// <param name="skipped">被跳过的服务器名。</param>
    /// <returns>无跳过时为空串。</returns>
    public static string SkippedNote(IReadOnlyCollection<string> skipped)
        => skipped.Count == 0 ? string.Empty : $"\n（已跳过锁定服务器: {string.Join(", ", skipped)}）";
}

/// <summary>
/// 服务器名列表类开关的通用实现：<c>lk/ulk</c>（锁定）与 <c>hd/uhd</c>（控制台隐藏输出）共用。
/// </summary>
internal static class ServerFlagHandler
{
    /// <summary>
    /// 打开 / 关闭某个服务器在指定列表中的条目。
    /// </summary>
    /// <param name="enable">true 表示加入列表，false 表示移出。</param>
    /// <param name="rest">动作之后的参数文本（服务器名）。</param>
    /// <param name="args">命令参数，包含配置存储。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <param name="select">从配置中取出目标列表。</param>
    /// <param name="enableUsage">开启用法的提示。</param>
    /// <param name="disableUsage">关闭用法的提示。</param>
    /// <param name="enabledMessage">加入成功后的说明模板（{0} 为服务器名）。</param>
    /// <param name="disabledMessage">移出成功后的说明模板（{0} 为服务器名）。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Toggle(
        bool enable,
        string rest,
        CommandArgs args,
        IOutputWriter? output,
        Func<AppConfig, List<string>> select,
        string enableUsage,
        string disableUsage,
        string enabledMessage,
        string disabledMessage)
    {
        var name = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name))
            return Fail(output, enable ? enableUsage : disableUsage);

        var config = args.ConfigStore.LoadConfig();

        // 用配置中的实际键名，避免大小写不一致导致写入的条目匹配不上
        var canonical = config.ServerPaths.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (canonical == null)
            return Fail(output, $"未找到服务器 '{name}'。已配置: " +
                                 (config.ServerPaths.Count == 0 ? "（无）" : string.Join(", ", config.ServerPaths.Keys)));

        var list = select(config);
        var present = list.Any(n => string.Equals(n, canonical, StringComparison.OrdinalIgnoreCase));

        if (enable)
        {
            if (present) return Ok(output, $"服务器 '{canonical}' 已经处于该状态，无需重复设置");
            list.Add(canonical);
        }
        else
        {
            if (!present) return Ok(output, $"服务器 '{canonical}' 本来就不是该状态，无需取消");
            list.RemoveAll(n => string.Equals(n, canonical, StringComparison.OrdinalIgnoreCase));
        }

        args.ConfigStore.SaveConfig(config);

        var msg = string.Format(enable ? enabledMessage : disabledMessage, canonical);
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>输出提示信息并构造成功结果。</summary>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <param name="message">信息文本。</param>
    /// <returns>成功结果。</returns>
    private static CommandResult Ok(IOutputWriter? output, string message)
    {
        output?.Write("Command", LogLevel.Info, message);
        return new CommandResult(1, message);
    }

    /// <summary>输出错误信息并构造失败结果。</summary>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <param name="message">错误信息。</param>
    /// <returns>退出码为 0 的失败结果。</returns>
    private static CommandResult Fail(IOutputWriter? output, string message)
    {
        output?.Write("Command", LogLevel.Error, message);
        return new CommandResult(0, message);
    }
}

/// <summary>
/// <c>$server lk|ulk</c> 动作的处理器：锁定 / 解锁服务器。
/// 锁定状态保存在 <see cref="AppConfig.LockedServers"/>，写盘后立即生效。
/// </summary>
internal static class ServerLockHandler
{
    /// <summary>
    /// 处理锁定 / 解锁动作。
    /// </summary>
    /// <param name="action">动作名（lk 或 ulk）。</param>
    /// <param name="rest">动作之后的参数文本。</param>
    /// <param name="args">命令参数，包含配置存储。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Handle(string action, string rest, CommandArgs args, IOutputWriter? output)
        => ServerFlagHandler.Toggle(
            enable: action == "lk",
            rest, args, output,
            select: config => config.LockedServers,
            enableUsage: "用法: $server lk <服务器名>",
            disableUsage: "用法: $server ulk <服务器名>",
            enabledMessage: "已锁定服务器 '{0}'：run/stop/send/rm/del 一律拒绝，$server ulk {0} 可解锁",
            disabledMessage: "已解锁服务器 '{0}'：run/stop/send/rm/del 已恢复");
}

/// <summary>
/// <c>$server hd|uhd</c> 动作的处理器：在控制台隐藏 / 显示某台服务器的输出。
/// 隐藏只影响控制台与日志输出，输出缓冲区与 AI 读取不受影响。
/// </summary>
internal static class ServerVisibilityHandler
{
    /// <summary>
    /// 处理隐藏 / 显示动作。
    /// </summary>
    /// <param name="action">动作名（hd 或 uhd）。</param>
    /// <param name="rest">动作之后的参数文本。</param>
    /// <param name="args">命令参数，包含配置存储。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Handle(string action, string rest, CommandArgs args, IOutputWriter? output)
        => ServerFlagHandler.Toggle(
            enable: action == "hd",
            rest, args, output,
            select: config => config.HiddenServers,
            enableUsage: "用法: $server hd <服务器名>",
            disableUsage: "用法: $server uhd <服务器名>",
            enabledMessage: "已在控制台隐藏服务器 '{0}' 的输出（缓冲区与 AI 读取不受影响，$server uhd {0} 可恢复）",
            disabledMessage: "已恢复服务器 '{0}' 的控制台输出");
}

