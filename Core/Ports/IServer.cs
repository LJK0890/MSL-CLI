using MSL_CLI.Core.Domain;
using MSL_CLI.Infrastructure;

namespace MSL_CLI.Core.Ports;

/// <summary>
/// 服务器接口，定义单个 Minecraft 服务器的状态信息、属性配置与常用操作能力。
/// </summary>
public interface IServer
{
    /// <summary>
    /// 服务器名称（唯一标识）。
    /// </summary>
    string Name { get; }
    /// <summary>
    /// 服务器所在目录路径。
    /// </summary>
    string Path { get; }
    /// <summary>
    /// 服务器当前运行状态。
    /// </summary>
    ServerStatus Status { get; }
    /// <summary>
    /// 服务器的属性配置（server.properties 文件）。
    /// </summary>
    ServerProperties Properties { get; }
    /// <summary>
    /// 服务器的启动参数配置。
    /// </summary>
    ServerArgument Argument { get; }
    /// <summary>
    /// 异步启动服务器。
    /// </summary>
    Task StartAsync();
    /// <summary>
    /// 异步停止服务器。
    /// </summary>
    /// <param name="force">是否强制停止（跳过优雅关闭流程）。</param>
    Task StopAsync(bool force = false);
    /// <summary>
    /// 异步向服务器发送一条控制台命令。
    /// </summary>
    /// <param name="command">要发送的命令文本。</param>
    Task SendCommandAsync(string command);
    /// <summary>
    /// 异步查询服务器的状态信息（如版本、在线人数等）。
    /// </summary>
    /// <returns>查询得到的键值对信息；查询失败时返回 null。</returns>
    Task<Dictionary<string, string>?> GetQueryInfoAsync();
    /// <summary>
    /// 获取服务器 ops.json 中的管理员玩家列表。
    /// </summary>
    /// <returns>管理员玩家名称列表。</returns>
    List<string> GetOps();
    /// <summary>
    /// 判断指定玩家是否为服务器管理员（op）。
    /// </summary>
    /// <param name="player">玩家名称。</param>
    /// <returns>该玩家是否为管理员。</returns>
    bool IsOp(string player);
    /// <summary>
    /// 获取输出缓冲区内容，但不清空缓冲区。
    /// </summary>
    /// <returns>缓冲区的完整文本内容。</returns>
    string GetBufferContent();              // 获取缓冲区内容（不清空）
    /// <summary>
    /// 获取输出缓冲区内容，并清空缓冲区。
    /// </summary>
    /// <returns>清空前的缓冲区文本内容。</returns>
    string GetAndClearBufferContent();      // 获取并清空
    /// <summary>
    /// 返回底层进程对象（用于状态命令展示）。
    /// </summary>
    /// <returns>底层进程对象；服务器未运行时可能为 null。</returns>
    object? GetProcess();                   // 返回 Process 对象（用于状态命令）
}
