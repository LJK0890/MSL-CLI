using MSL_CLI.Commands;
using MSL_CLI.Config;
using MSL_CLI.Services;
using static MSL_CLI.IO.IO;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Text.Json;

namespace MSL_CLI.AI;

public class AIClient
{
    private readonly ChatClient _chatClient;
    private readonly List<ChatTool> _tools;
    private readonly CommandParser _commandParser;
    private readonly AIConfig _aiConfig;

    public AIConfig AIConfig => _aiConfig;

    public AIClient(AIConfig config, GlobalManager globalManager)
    {
        _aiConfig = config;

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(config.Url)
        };
        var client = new OpenAIClient(new ApiKeyCredential(config.ApiKey), options);
        _chatClient = client.GetChatClient(config.Model);

        // 创建命令解析器（用于执行命令）
        _commandParser = new CommandParser(globalManager);

        // 提供工具：执行命令
        _tools =
        [
            ChatTool.CreateFunctionTool(
                "execute_command",
                "执行任意命令（以 $ 开头），返回执行结果输出。",
                BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        command = new { type = "string", description = "完整的命令字符串，如 '$run myserver'" }
                    },
                    required = new[] { "command" }
                })
            ),
            ChatTool.CreateFunctionTool(
                "sleep",
                "休眠指定时间（秒）。",
                BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        duration = new { type = "number", description = "休眠时间（秒）" }
                    },
                    required = new[] { "duration" }
                })
            )
        ];
    }

    public async Task<string> ChatAsync(string message)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateUserMessage(message)
        };
        var response = await _chatClient.CompleteChatAsync(messages);
        return response.Value.Content[0].Text;
    }

    public async Task<string> AgentAsync(string instruction)
    {
        // 获取动态命令列表
        var commandDescriptions = _commandParser.GetCommandDescriptions();
        var commandList = string.Join("\n", commandDescriptions
            .OrderBy(k => k.Key)
            .Select(kvp => $"- {kvp.Key}：{kvp.Value}"));

        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(_aiConfig.Prompt.Replace("{commandList}", commandList)),
            ChatMessage.CreateUserMessage(instruction)
        };

        int maxIterations = _aiConfig.MaxIterations;
        int iterationLimit = maxIterations == -1 ? int.MaxValue : maxIterations;
        for (int i = 0; i < iterationLimit; i++)
        {
            var options = new ChatCompletionOptions();
            foreach (var tool in _tools)
                options.Tools.Add(tool);

            var response = await _chatClient.CompleteChatAsync(messages, options);
            Print($"AI/{_aiConfig.Model}",LogLevel.SUCCESS, $"AI 迭代 {i + 1}/{maxIterations}，FinishReason: {response.Value.FinishReason}", true);
            var finish = response.Value.FinishReason;
            if (finish == ChatFinishReason.Stop)
            {
                return response.Value.Content[0].Text;
            }
            else if (finish == ChatFinishReason.ToolCalls)
            {
                var toolCalls = response.Value.ToolCalls;
                var toolMessages = new List<ChatMessage>();
                foreach (var toolCall in toolCalls)
                {
                    if (toolCall.Kind == ChatToolCallKind.Function)
                    {
                        string argsJson = toolCall.FunctionArguments?.ToString() ?? "{}";
                        var result = await ExecuteToolAsync(toolCall.FunctionName, argsJson);
                        toolMessages.Add(ChatMessage.CreateToolMessage(toolCall.Id, result));
                    }
                }
                messages.Add(ChatMessage.CreateAssistantMessage(response.Value));
                messages.AddRange(toolMessages);
                continue;
            }
            else
            {
                return response.Value.Content[0].Text;
            }
        }
        return $"Agent 执行超过最大迭代次数 ({maxIterations})，已终止。";
    }

    private async Task<string> ExecuteToolAsync(string functionName, string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        switch (functionName)
        {
            case "execute_command":
                {
                    string command = root.GetProperty("command").GetString();
                    if (string.IsNullOrEmpty(command))
                        return "错误：命令为空。";
                    // 使用捕获机制执行命令
                    Print($"AI/{_aiConfig.Model}", LogLevel.WARNING, $"正在执行命令: {command}", true);
                    var (exitCode, output) = await _commandParser.ExecuteWithCaptureAsync(command);
                    Print($"AI/{_aiConfig.Model}", LogLevel.SUCCESS, $"命令执行完成", true);
                    if (exitCode == -1)
                        return $"未知命令: {command}\n输出:\n{output}";
                    if (exitCode == 0)
                        return $"命令执行失败 (退出码 0)\n输出:\n{output}";
                    return output; // 成功，直接返回捕获的输出
                }
            case "sleep":
                {
                    int duration = root.GetProperty("duration").GetInt32();
                    if (duration < 0)
                        return "错误：休眠时间不能为负数。";
                    Print($"AI/{_aiConfig.Model}", LogLevel.WARNING, $"正在休眠 {duration} 秒...", true);
                    await Task.Delay(duration * 1000);
                    return $"已休眠 {duration} 秒。";
                }
            default:
                return $"未知工具: {functionName}";
        }
    }
}