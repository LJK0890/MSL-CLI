using Microsoft.Extensions.DependencyInjection;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;
using MSL_CLI.Core.UseCases;
using MSL_CLI.Infrastructure;

namespace MSL_CLI.CLI;

/// <summary>
/// 程序入口类，负责组装依赖注入容器、启动输入监听并协调整个应用的生命周期。
/// </summary>
public class Program
{
    /// <summary>
    /// 应用程序入口点。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>表示异步入口方法的任务。</returns>
    public static async Task Main(string[] args)
    {
        // ---------- 全局异常捕获 ----------
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            Console.WriteLine($"致命错误: {e.ExceptionObject}");
            Environment.Exit(1);
        };

        // ---------- 1. 构建 DI 容器 ----------
        var services = new ServiceCollection();

        // ----- 输出适配器（控制台 + 文件）-----
        // 先构建输出器，使配置加载时也能把解析错误报告给用户
        var consoleOutput = new ConsoleOutputWriter();
        var fileOutput = new FileOutputWriter();
        var outputWriter = new CompositeOutputWriter(consoleOutput, fileOutput);

        services.AddSingleton<ConsoleOutputWriter>(consoleOutput);
        services.AddSingleton<FileOutputWriter>(fileOutput);
        services.AddSingleton<IOutputWriter>(outputWriter);

        // ----- 配置加载 -----
        var configStore = new FileConfigurationStore("MSL_CLI", outputWriter);
        var appConfig = configStore.LoadConfig();

        services.AddSingleton<IConfigurationStore>(configStore);
        services.AddSingleton(appConfig);

        // ----- 输入适配器（使用队列，并支持提问时独占读取一行）-----
        services.AddSingleton<IInputReader, ConsoleInputReader>();

        // ----- 基础设施 -----
        services.AddTransient<IServerProcess, ServerProcess>();
        // 注意：IAgentService 用惰性工厂传给注册表，避免
        // ICommandExecutor → IServerRegistry → IAgentService → IServerRegistry 的构造循环
        services.AddSingleton<IServerRegistry>(sp => new ServerRegistry(
            appConfig,
            outputWriter,
            sp,
            () => sp.GetRequiredService<IAgentService>()));
        services.AddSingleton<ICommandParser, CommandParser>();
        // 代理命令授权网关：执行代理命令前向操作员请求许可
        services.AddSingleton<IAgentPermissionGateway, AgentPermissionGateway>();
        services.AddSingleton<IAgentService, OpenAiAgentService>();

        // ----- 应用服务 -----
        services.AddSingleton<ICommandExecutor, CommandExecutor>();

        // ---------- 2. 构建服务提供者 ----------
        await using var sp = services.BuildServiceProvider();

        // ---------- 3. 获取服务 ----------
        var output = sp.GetRequiredService<IOutputWriter>();
        var parser = sp.GetRequiredService<ICommandParser>();
        var inputReader = sp.GetRequiredService<IInputReader>();
        var executor = sp.GetRequiredService<ICommandExecutor>();
        var registry = sp.GetRequiredService<IServerRegistry>();

        // ---------- 4. 打印配置来源与 ApiKey 加载状态（只在此处输出一次）----------
        // 明确告知本次运行实际读写的是哪一份配置，便于排查“改了没生效”一类问题
        output.Write("Config", LogLevel.Info, $"配置文件: {configStore.UserConfigPath}");
        output.Write("Config", LogLevel.Info, $"本次配置来源: {configStore.LastLoadSource}");
        output.Write("Config", LogLevel.Info,
            $"允许列表共 {appConfig.AgentPermissions.AllowList.Count} 条");

        foreach (var kv in appConfig.AIConfigs)
        {
            if (kv.Value.UseApiKeyEnv && !string.IsNullOrEmpty(kv.Value.ApiKeyEnv))
            {
                var success = !string.IsNullOrEmpty(kv.Value.ApiKey);
                output.Write("Config", LogLevel.Info,
                    $"从环境变量 '{kv.Value.ApiKeyEnv}' 读取 ApiKey {(success ? "成功" : "失败（请检查环境变量）")}");
            }
        }

        // ---------- 5. 注册命令 ----------
        parser.RegisterCommands(sp);
        output.Write("Command", LogLevel.Info, $"已注册 {parser.GetCommandDescriptions().Count} 个命令");

        // ---------- 6. 启动输入监听 ----------
        var cts = new CancellationTokenSource();

        // 使用队列处理输入（由 IInputReader 内部串行化）
        inputReader.OnInputReceived += async (s, line) =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            try
            {
                // $ 开头视为系统指令，交给命令执行器处理
                if (line.StartsWith("$"))
                {
                    var result = await executor.ExecuteAsync(line, output);
                    // 执行器请求退出时取消主循环等待，触发清理流程
                    if (result != null && result.ExitRequested)
                    {
                        output.Write("GLOBAL", LogLevel.Info, "收到退出指令，正在关闭...");
                        cts.Cancel();
                    }
                }
                else
                {
                    // 普通文本视为发送给当前高亮服务器的命令
                    var hl = registry.GetHighlightedServer();
                    if (hl != null)
                        await hl.SendCommandAsync(line);
                    else
                        output.Write("GLOBAL", LogLevel.Warning, "未高亮服务器，无法发送命令");
                }
            }
            catch (Exception ex)
            {
                output.Write("GLOBAL", LogLevel.Error, $"处理输入时发生异常: {ex.Message}");
            }
        };

        output.Write("GLOBAL", LogLevel.Info, "MSL_CLI 启动，输入命令...");
        inputReader.StartReading();

        // ---------- 7. 等待退出信号 ----------
        try
        {
            await Task.Delay(-1, cts.Token);
        }
        catch (TaskCanceledException)
        {
            // 正常退出
        }

        // ---------- 8. 清理 ----------
        output.Write("GLOBAL", LogLevel.Info, "正在停止所有服务器...");
        inputReader.StopReading();
        await registry.StopAllAsync();

        // 注意：这里不再整份保存 appConfig。
        // 启动时加载的 appConfig 与会话中命令/授权网关读到的磁盘内容可能已经不同，
        // 退出时用这份陈旧副本写回会清掉会话中新增的内容（例如 AgentPermissions.AllowList）。
        // 配置的写入一律由修改方即时落盘（$app cfg set、授权“总是允许”、$app reload 等）。

        output.Write("GLOBAL", LogLevel.Info, "程序退出");

        // FileOutputWriter 会由容器自动释放（using 范围）
    }
}
