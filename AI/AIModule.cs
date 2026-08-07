using MSL_CLI.Services;
using static MSL_CLI.IO.IO;

namespace MSL_CLI.AI;

/// <summary>
/// AI 模块，管理多个 AI 客户端，提供对话和 Agent 功能。
/// </summary>
internal class AIModule
{
    private readonly GlobalManager _globalManager;
    private readonly Dictionary<string, AIClient> _clients = new();

    public AIModule(GlobalManager globalManager)
    {
        _globalManager = globalManager;
        var config = globalManager.GetConfig();
        foreach (var kvp in config.AIConfigs)
        {
            try
            {
                _clients[kvp.Key] = new AIClient(kvp.Value, globalManager);
                Print("AI/Module", LogLevel.INFO, $"已加载 AI 配置: {kvp.Key}", true);
            }
            catch (Exception ex)
            {
                Print("AI/Module", LogLevel.ERROR, $"加载 AI 配置 '{kvp.Key}' 失败: {ex.Message}", true);
            }
        }
    }

    /// <summary>
    /// 使用指定 AI 配置进行对话。
    /// </summary>
    public async Task<string> ChatAsync(string configName, string message)
    {
        if (!_clients.TryGetValue(configName, out var client))
            throw new ArgumentException($"AI 配置 '{configName}' 不存在");

        return await client.ChatAsync(message);
    }

    /// <summary>
    /// 使用指定 AI 配置执行 Agent 指令（支持工具调用）。
    /// </summary>
    public async Task<(string model,string message)> AgentAsync(string configName, string instruction)
    {
        if (!_clients.TryGetValue(configName, out var client))
            throw new ArgumentException($"AI 配置 '{configName}' 不存在");

        return (client.AIConfig.Model, await client.AgentAsync(instruction));
    }

    /// <summary>
    /// 获取所有已加载的 AI 配置名称。
    /// </summary>
    public IEnumerable<string> GetConfigNames() => _clients.Keys;
}