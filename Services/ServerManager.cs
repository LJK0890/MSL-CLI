using static MSL_CLI.IO.IO;
using MSL_CLI.Models;

namespace MSL_CLI.Services;

/// <summary>
/// 单个服务器的管理器，负责加载该服务器的配置和启动参数。
/// </summary>
internal class ServerManager
{
    private ServerConfig serverConfig;
    private ServerArgument serverArgument;
    private string serverPath;
    private string serverName;

    /// <summary>
    /// 构造函数，加载指定路径的服务器配置。
    /// </summary>
    /// <param name="globalManager">全局管理器实例（用于获取全局配置）</param>
    /// <param name="filePath">服务器根目录路径（应包含 server.properties 和启动脚本）</param>
    public ServerManager(string name,string filePath)
    {
        serverPath = filePath;
        serverName = name;
        Output.Print($"{name}", LogLevel.INFO, $"初始化服务器: {filePath}", includeTimestamp: true);

        // 加载 server.properties
        serverConfig = new ServerConfig(name,Path.Combine(filePath, "server.properties"));
        // 解析启动参数
        serverArgument = new ServerArgument(name,filePath);

        Output.Print($"{name}", LogLevel.INFO, $"服务器初始化完成。", includeTimestamp: true);
    }
}