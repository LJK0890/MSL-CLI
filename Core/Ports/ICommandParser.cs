using MSL_CLI.Core.UseCases;

namespace MSL_CLI.Core.Ports;

/// <summary>
/// 命令解析器接口，负责命令的注册、按名称查询以及命令描述信息的获取。
/// </summary>
public interface ICommandParser
{
    /// <summary>
    /// 从服务容器中解析并注册所有可用的命令。
    /// </summary>
    /// <param name="serviceProvider">用于解析命令实例的服务提供者。</param>
    void RegisterCommands(IServiceProvider serviceProvider);
    /// <summary>
    /// 根据命令名称获取对应的命令实例。
    /// </summary>
    /// <param name="name">命令名称。</param>
    /// <returns>匹配的命令实例；若不存在该命令则返回 null。</returns>
    ICommand? GetCommand(string name);
    /// <summary>
    /// 获取所有已注册命令的名称与描述映射，用于帮助信息展示。
    /// </summary>
    /// <returns>命令名称到描述文本的字典。</returns>
    Dictionary<string, string> GetCommandDescriptions();
}
