using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $server 的服务器登记类动作处理器：
/// <c>add</c>（登记新服务器）、<c>rm</c>（只移除配置引用）、<c>del</c>（移除引用并删除服务器目录）。
/// 三者都会在修改配置后立即重载注册表，无需重启。
/// </summary>
internal static class ServerLifecycleHandler
{
    /// <summary>不能用作服务器名的保留字（"all" 是多服务器动作的目标）。</summary>
    private static readonly string[] ReservedNames = { "all" };

    /// <summary>
    /// 处理登记类动作。
    /// </summary>
    /// <param name="action">已规范化的动作名（add / rm / del）。</param>
    /// <param name="rest">动作之后的参数文本。</param>
    /// <param name="args">命令参数，包含配置存储与服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Handle(string action, string rest, CommandArgs args, IOutputWriter? output) => action switch
    {
        "add" => Add(rest, args, output),
        "rm" => RemoveReference(rest, args, output),
        "del" => Delete(rest, args, output),
        _ => Fail(output, Usage())
    };

    // ---------- add ----------

    /// <summary>
    /// 登记一台新服务器：把“名称 -&gt; 路径”写入 <c>ServerPaths</c> 并重载注册表。
    /// 名称必须合法且未被占用，路径必须存在、且未被其他服务器占用。
    /// </summary>
    /// <param name="rest">add 之后的参数文本。</param>
    /// <param name="args">命令参数。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult Add(string rest, CommandArgs args, IOutputWriter? output)
    {
        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return Fail(output, "用法: $server add <名称> <路径>\n" +
                                 "  例如: $server add survival D:\\Minecraft\\survival");

        var name = parts[0];
        if (!IsValidName(name, out var nameError)) return Fail(output, nameError);

        var config = args.ConfigStore.LoadConfig();
        // 按不区分大小写查找已有条目：应用其余部分（锁定/隐藏、$server hl 等）都忽略大小写，
        // 这里若区分大小写就会多出一个仅大小写不同的“重复服务器”
        var existing = config.ServerPaths.Keys
            .FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return Fail(output, $"服务器 '{name}' 已存在（名称: {existing}，路径: {config.ServerPaths[existing]}）。" +
                                 $"如需修改路径用 $app cfg set ServerPaths.{existing} <新路径>，如需删除用 $server rm {existing}");

        if (!TryNormalizePath(parts[1], out var path, out var pathError)) return Fail(output, pathError!);

        if (!Directory.Exists(path))
            return Fail(output, $"目录不存在: {path}\n请先创建目录，或确认路径是否正确（支持环境变量，如 %USERPROFILE%\\srv）。");

        // 同一目录登记为两台服务器会导致两个进程操作同一份存档，直接拒绝
        foreach (var kv in config.ServerPaths)
        {
            if (PathsEqual(kv.Value, path))
                return Fail(output, $"路径已被服务器 '{kv.Key}' 使用: {path}\n同一目录不能登记为两台服务器。");
        }

        config.ServerPaths[name] = path;
        args.ConfigStore.SaveConfig(config);
        args.ServerRegistry.Reload(config);

        var msg = $"已添加服务器 '{name}' -> {path}（当前共 {config.ServerPaths.Count} 台）\n" +
                  $"下一步: $server hl {name} 可切换高亮，$server run {name} 可启动。";
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    // ---------- rm ----------

    /// <summary>
    /// 只移除配置引用，保留服务器目录（可随时用 <c>$server add</c> 加回来）。
    /// 运行中的服务器必须先停止，避免注册表里出现“有进程无配置”的孤儿实例。
    /// </summary>
    /// <param name="rest">rm 之后的参数文本。</param>
    /// <param name="args">命令参数。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult RemoveReference(string rest, CommandArgs args, IOutputWriter? output)
    {
        var name = FirstToken(rest);
        if (name == null) return Fail(output, "用法: $server rm <名称>");

        var config = args.ConfigStore.LoadConfig();
        var canonical = config.ServerPaths.Keys
            .FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (canonical == null)
            return Fail(output, $"未找到服务器 '{name}'。已配置: {Available(config)}");

        var path = config.ServerPaths[canonical];

        if (RunningBlock(args, canonical) is { } running) return Fail(output, running);

        config.ServerPaths.Remove(canonical);
        args.ConfigStore.SaveConfig(config);
        args.ServerRegistry.Reload(config);

        var msg = $"已移除服务器 '{canonical}' 的配置引用（目录保留: {path}）\n" +
                  $"如需连目录一起删除: $server del {canonical} confirm";
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    // ---------- del ----------

    /// <summary>
    /// 删除服务器：移除配置引用**并删除整个服务器目录**。
    /// 属于不可恢复操作，必须显式带上 <c>confirm</c> 才会真正执行；
    /// 运行中的服务器、驱动器根目录、符号链接、以及作为其他服务器上级目录的路径一律拒绝。
    /// </summary>
    /// <param name="rest">del 之后的参数文本。</param>
    /// <param name="args">命令参数。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult Delete(string rest, CommandArgs args, IOutputWriter? output)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Fail(output, "用法: $server del <名称> confirm");

        var requested = parts[0];
        var confirmed = parts.Skip(1).Any(p => p.Equals("confirm", StringComparison.OrdinalIgnoreCase));

        var config = args.ConfigStore.LoadConfig();
        var name = config.ServerPaths.Keys
            .FirstOrDefault(k => k.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (name == null)
            return Fail(output, $"未找到服务器 '{requested}'。已配置: {Available(config)}");

        var configuredPath = config.ServerPaths[name];

        if (RunningBlock(args, name) is { } running) return Fail(output, running);

        if (!TryResolveDeletablePath(configuredPath, name, config, out var fullPath, out var pathError))
            return Fail(output, pathError!);

        // 未确认时只预告目标，不修改任何内容
        if (!confirmed)
        {
            var preview = $"即将删除服务器 '{name}'，请确认：\n" +
                          $"  配置引用: ServerPaths.{name}\n" +
                          $"  目录（连同全部内容）: {fullPath}\n" +
                          "该操作不可恢复。确认请重新执行: $server del " + name + " confirm\n" +
                          $"建议先备份: $server bp {name}";
            output?.Write("Command", LogLevel.Warning, preview);
            return new CommandResult(1, preview);
        }

        // 先移除引用并重载注册表：这会释放该服务器的实例与进程句柄，随后才能删除目录
        config.ServerPaths.Remove(name);
        args.ConfigStore.SaveConfig(config);
        args.ServerRegistry.Reload(config);

        try
        {
            Directory.Delete(fullPath!, recursive: true);
        }
        catch (Exception ex)
        {
            var msg = $"服务器 '{name}' 的配置引用已移除，但目录删除失败: {ex.Message}\n" +
                      $"目录仍然保留: {fullPath}\n" +
                      $"如需重新登记: $server add {name} \"{fullPath}\"";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var ok = $"已删除服务器 '{name}'（配置引用 + 目录）: {fullPath}";
        output?.Write("Command", LogLevel.Success, ok);
        return new CommandResult(1, ok);
    }

    // ---------- 辅助 ----------

    /// <summary>
    /// 运行中的服务器不允许移除/删除，返回拒绝原因；可操作时返回 null。
    /// </summary>
    /// <param name="args">命令参数。</param>
    /// <param name="name">服务器名。</param>
    /// <returns>拒绝原因，或 null。</returns>
    private static string? RunningBlock(CommandArgs args, string name)
    {
        var server = args.ServerRegistry.GetServer(name);
        if (server == null || server.Status == ServerStatus.Stopped) return null;

        return $"服务器 '{name}' 正在运行（{server.Status}），请先执行 $server stop {name}";
    }

    /// <summary>
    /// 校验服务器名：非空、不含空白与路径字符、不是保留字。
    /// </summary>
    /// <param name="name">服务器名。</param>
    /// <param name="error">不合法时的说明文本。</param>
    /// <returns>合法时返回 true。</returns>
    private static bool IsValidName(string name, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "服务器名不能为空";
            return false;
        }

        if (name.IndexOfAny(new[] { ' ', '\t', '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
        {
            error = $"服务器名 '{name}' 含有非法字符（空白或 \\ / : * ? \" < > |）";
            return false;
        }

        if (ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            error = $"'{name}' 是保留字（多服务器动作的目标），不能作为服务器名";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 把用户输入（可含引号与环境变量）规范化为绝对路径。
    /// </summary>
    /// <param name="input">原始路径文本。</param>
    /// <param name="path">规范化后的绝对路径。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>成功返回 true。</returns>
    private static bool TryNormalizePath(string input, out string path, out string? error)
    {
        path = string.Empty;
        error = null;

        var raw = input.Trim().Trim('"');
        if (raw.Length == 0)
        {
            error = "路径不能为空";
            return false;
        }

        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw));

            // 去掉结尾分隔符（驱动器根除外），避免把 "D:\srv\" 这种写法存进配置
            var root = Path.GetPathRoot(path);
            if (root != null && path.Length > root.Length)
                path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return true;
        }
        catch (Exception ex)
        {
            error = $"路径无效: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 校验待删除目录并给出规范化后的绝对路径。
    /// 拒绝驱动器根目录、不存在的目录、符号链接/联接点，
    /// 以及作为其他已配置服务器目录的上级目录（否则会连带删除其他服务器）。
    /// </summary>
    /// <param name="configuredPath">配置中的路径。</param>
    /// <param name="name">要删除的服务器名。</param>
    /// <param name="config">当前配置，用于比对其余服务器路径。</param>
    /// <param name="fullPath">规范化后的绝对路径。</param>
    /// <param name="error">拒绝原因。</param>
    /// <returns>可以安全删除时返回 true。</returns>
    private static bool TryResolveDeletablePath(
        string configuredPath, string name, AppConfig config, out string? fullPath, out string? error)
    {
        fullPath = null;
        error = null;

        if (!TryNormalizePath(configuredPath, out var path, out var pathError))
        {
            error = pathError;
            return false;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (trimmed.Length == 0 || string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
        {
            error = $"拒绝删除驱动器根目录: {path}";
            return false;
        }

        if (!Directory.Exists(trimmed))
        {
            error = $"目录不存在: {trimmed}\n如只需移除配置引用，请用 $server rm {name}";
            return false;
        }

        try
        {
            if ((File.GetAttributes(trimmed) & FileAttributes.ReparsePoint) != 0)
            {
                error = $"目录是符号链接/联接点，出于安全考虑拒绝删除: {trimmed}";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"读取目录属性失败: {ex.Message}";
            return false;
        }

        foreach (var kv in config.ServerPaths)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryNormalizePath(kv.Value, out var other, out _)) continue;

            var otherTrimmed = other.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (otherTrimmed.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                error = $"目录 '{trimmed}' 是服务器 '{kv.Key}' 的上级目录（{otherTrimmed}），" +
                        "拒绝删除以免连带删除其他服务器。请先移除这些服务器，或改用 $server rm " + name;
                return false;
            }
        }

        fullPath = trimmed;
        return true;
    }

    /// <summary>
    /// 判断两个路径是否指向同一个目录（忽略大小写与结尾分隔符）。
    /// </summary>
    /// <param name="left">路径一。</param>
    /// <param name="right">路径二。</param>
    /// <returns>相同返回 true。</returns>
    private static bool PathsEqual(string left, string right)
        => TryNormalizePath(left, out var a, out _)
           && TryNormalizePath(right, out var b, out _)
           && string.Equals(
               a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
               b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
               StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 取出参数文本的首个 token。
    /// </summary>
    /// <param name="rest">参数文本。</param>
    /// <returns>首个 token；为空时返回 null。</returns>
    private static string? FirstToken(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : parts[0];
    }

    /// <summary>
    /// 列出当前已配置的服务器名，用于错误提示。
    /// </summary>
    /// <param name="config">当前配置。</param>
    /// <returns>服务器名列表文本。</returns>
    private static string Available(AppConfig config)
        => config.ServerPaths.Count == 0
            ? "（当前没有任何服务器，可用 $server add <名称> <路径> 添加）"
            : string.Join(", ", config.ServerPaths.Keys);

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

    /// <summary>
    /// 用法说明。
    /// </summary>
    /// <returns>用法文本。</returns>
    private static string Usage() =>
        "用法: $server add|rm|del ...\n" +
        "  add <名称> <路径>       登记新服务器（路径须已存在，支持环境变量）\n" +
        "  rm <名称>               只移除配置引用，保留服务器目录\n" +
        "  del <名称> confirm      移除引用并删除整个服务器目录（不可恢复）";
}
