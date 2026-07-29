using System.Text.Json;
using static MSL_CLI.IO.IO;
using MSL_CLI.Models;

namespace MSL_CLI.Services;

/// <summary>
/// 全局管理器，负责加载/保存应用程序配置，并管理所有 ServerManager 实例。
/// </summary>
internal class GlobalManager : IDisposable
{
    private AppConfig? appConfig;
    private bool disposed = false;
    private Dictionary<string, ServerManager> serverManagers;

    // 引入配置服务
    private readonly ConfigService _configService;

    public GlobalManager()
    {
        // 初始化服务
        _configService = new ConfigService();

        // 加载配置
        appConfig = _configService.LoadConfig();

        // 初始化业务逻辑
        serverManagers = new Dictionary<string, ServerManager>();
        foreach (var serverPathKV in appConfig.ServerPaths)
        {
            var serverManager = new ServerManager(serverPathKV.Key, serverPathKV.Value);
            serverManagers.Add(serverPathKV.Key, serverManager);
        }

        Output.Print("Global/Config", LogLevel.INFO, $"已加载配置，包含 {appConfig.ServerPaths.Count} 个服务器路径。", includeTimestamp: true);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        if (disposing)
        {
            if (appConfig != null)
            {
                try
                {
                    // 委托给服务保存
                    _configService.SaveConfig(appConfig);
                    Output.Print("Global/Config", LogLevel.INFO, "配置已保存。", includeTimestamp: true);
                }
                catch (Exception ex)
                {
                    Output.Print("Global/Config", LogLevel.ERROR, $"保存配置失败: {ex.Message}", includeTimestamp: true);
                }
            }
        }
        disposed = true;
    }

    ~GlobalManager()
    {
        Dispose(false);
    }

    /// <summary>
    /// 打印当前配置到控制台（用于调试）。
    /// </summary>
    public void PrintConfig()
    {
        if (appConfig == null)
        {
            Output.Print("Global/Config", LogLevel.INFO, "配置未加载", includeTimestamp: true);
            return;
        }
        Output.Print("Global/Config", LogLevel.INFO, "EnableAI: " + appConfig.EnableAI, includeTimestamp: true);
        Output.Print("Global/Config", LogLevel.INFO, "AIConfigs:", includeTimestamp: true);
        foreach (var kvp in appConfig.AIConfigs)
        {
            string keyDisplay = string.IsNullOrEmpty(kvp.Value.ApiKey) ? "(empty)" : "****";
            Output.Print("Global/AI", LogLevel.INFO, $" {kvp.Key}:", includeTimestamp: true);
            Output.Print("Global/AI", LogLevel.INFO, $" Url={kvp.Value.Url}", includeTimestamp: true);
            Output.Print("Global/AI", LogLevel.INFO, $" Model={kvp.Value.Model}", includeTimestamp: true);
            Output.Print("Global/AI", LogLevel.INFO, $" ApiKey={keyDisplay}", includeTimestamp: true);
            Output.Print("Global/AI", LogLevel.INFO, $" UseApiKeyEnv={kvp.Value.UseApiKeyEnv}", includeTimestamp: true);
            Output.Print("Global/AI", LogLevel.INFO, $" ApiKeyEnv={kvp.Value.ApiKeyEnv}", includeTimestamp: true);
        }
        Output.Print("Global/Config", LogLevel.INFO, "ServerPaths:", includeTimestamp: true);
        foreach (var kvp in appConfig.ServerPaths)
        {
            Output.Print("Global/Config", LogLevel.INFO, $" {kvp.Key}: {kvp.Value}", includeTimestamp: true);
        }
    }

    public void MainLoop()
    {
        while (true)
        {

        }
    }
}