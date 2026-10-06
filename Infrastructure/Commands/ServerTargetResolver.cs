namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// 目标服务器解析辅助类，供 $server 各动作处理器共用。
/// 约定：省略服务器名时回退到高亮服务器，因此“单个参数是服务器名还是参数名”需要统一判断。
/// </summary>
internal static class ServerTargetResolver
{
    /// <summary>
    /// 判断给定名称是否是已配置的服务器。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="name">待判断的名称。</param>
    /// <returns>是已配置服务器时返回 true。</returns>
    public static bool IsServer(MSL_CLI.Core.Domain.CommandArgs args, string name)
        => args.ServerRegistry.GetServer(name) != null;

    /// <summary>
    /// 解析目标服务器名：显式给出时校验存在性，未给出时回退到高亮服务器。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="name">显式服务器名，可为 null 或空。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>服务器名；失败时返回 null。</returns>
    public static string? Resolve(MSL_CLI.Core.Domain.CommandArgs args, string? name, out string? error)
    {
        error = null;

        if (!string.IsNullOrEmpty(name))
        {
            if (args.ServerRegistry.GetServer(name) == null)
            {
                error = $"未找到服务器 '{name}'";
                return null;
            }
            return name;
        }

        var highlighted = args.ServerRegistry.HighlightedServerName;
        if (string.IsNullOrEmpty(highlighted))
        {
            error = "未设置高亮服务器，请指定服务器名或先用 $server hl <服务器名> 设置高亮";
            return null;
        }

        return highlighted;
    }
}
