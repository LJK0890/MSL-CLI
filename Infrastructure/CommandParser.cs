using Microsoft.Extensions.DependencyInjection;
using MSL_CLI.Core.Ports;
using MSL_CLI.Core.UseCases;
using System.Reflection;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 命令解析器，通过反射自动发现并注册程序集中的全部 ICommand 实现，
/// 支持按命令名查找命令及获取命令描述列表。
/// </summary>
public class CommandParser : ICommandParser
{
    /// <summary>
    /// 以命令名为键的命令实例字典（命令名不区分大小写，$SERVER 与 $server 等价）。
    /// </summary>
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 扫描当前程序集，将实现 ICommand 接口的非抽象类实例化并注册到命令字典。
    /// </summary>
    /// <param name="serviceProvider">依赖注入服务提供器，用于创建命令实例。</param>
    public void RegisterCommands(IServiceProvider serviceProvider)
    {
        // 通过反射找出当前程序集中所有实现了 ICommand 的非抽象类
        var types = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(ICommand).IsAssignableFrom(t) && !t.IsAbstract && t.IsClass);

        foreach (var type in types)
        {
            // 借助 ActivatorUtilities 从容器解析命令构造函数依赖并创建实例
            try
            {
                var instance = ActivatorUtilities.CreateInstance(serviceProvider, type) as ICommand;
                if (instance != null)
                    _commands[instance.Name] = instance;
            }
            catch (Exception ex)
            {
                // 附带类型名抛出，便于直接定位是哪个命令的构造函数有问题
                // （否则只会看到一个没有上下文的反射异常）
                throw new InvalidOperationException(
                    $"无法创建命令 '{type.FullName}'：请检查其构造函数的依赖是否已在 DI 容器中注册。{ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// 按命令名获取已注册的命令。
    /// </summary>
    /// <param name="name">命令名称。</param>
    /// <returns>匹配的命令实例；若未注册则返回 null。</returns>
    public ICommand? GetCommand(string name) => _commands.GetValueOrDefault(name);

    /// <summary>
    /// 获取所有已注册命令的名称与描述。
    /// </summary>
    /// <returns>命令名到命令描述的映射字典。</returns>
    public Dictionary<string, string> GetCommandDescriptions()
        => _commands.ToDictionary(kv => kv.Key, kv => kv.Value.Description);
}
