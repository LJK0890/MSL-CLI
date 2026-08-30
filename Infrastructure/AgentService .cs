using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;
using OpenAI;
using OpenAI.Chat;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 基于 OpenAI 的智能体服务实现，负责处理聊天与智能体请求。
/// 请求通过无界通道排队，由单个后台消费者串行处理，处理结果经 TaskCompletionSource 回传给调用方。
/// </summary>
public class OpenAiAgentService : IAgentService, IDisposable
{
    /// <summary>AI 配置字典，键为配置名称。</summary>
    private readonly Dictionary<string, AIConfig> _configs;
    /// <summary>服务提供者，用于按需解析命令执行器等依赖。</summary>
    private readonly IServiceProvider _serviceProvider;
    /// <summary>输出写入器，用于记录日志信息。</summary>
    private readonly IOutputWriter _output;
    /// <summary>命令解析器，用于获取命令描述列表以生成智能体提示词。</summary>
    private readonly ICommandParser _commandParser;

    // ---------- 队列相关 ----------
    /// <summary>
    /// 单个排队请求的数据结构，描述一次聊天或智能体调用及其结果回传通道。
    /// </summary>
    private sealed class Request
    {
        /// <summary>请求来源，包含服务器与玩家信息；为 null 表示来自控制台。</summary>
        public (IServer, string)? Source { get; init; }
        /// <summary>所使用的 AI 配置名称。</summary>
        public string ConfigName { get; init; } = string.Empty;
        /// <summary>用户消息内容（聊天）或智能体指令。</summary>
        public string Message { get; init; } = string.Empty;
        /// <summary>是否为智能体请求。</summary>
        public bool IsAgent { get; init; }  // true=Agent, false=Chat
        /// <summary>用于将处理结果（模型名称与响应文本）回传给等待方。</summary>
        public TaskCompletionSource<(string model, string response)> Tcs { get; } = new();
        /// <summary>请求关联的取消令牌。</summary>
        public CancellationToken CancellationToken { get; init; }
    }

    /// <summary>
    /// 无界请求队列，仅允许单个消费者读取，保证请求按入队顺序串行处理。
    /// </summary>
    private readonly Channel<Request> _channel = Channel.CreateUnbounded<Request>(
        new UnboundedChannelOptions { SingleReader = true } // 单消费者
    );
    /// <summary>服务级别的取消令牌源，用于停止后台消费者。</summary>
    private readonly CancellationTokenSource _cts = new();
    /// <summary>后台消费者任务，负责从队列中取出请求并依次执行。</summary>
    private readonly Task _processorTask;

    /// <summary>
    /// 初始化服务实例并启动后台消费者任务。
    /// </summary>
    /// <param name="config">应用配置，提供 AI 配置集合。</param>
    /// <param name="serviceProvider">服务提供者，用于解析依赖（如命令执行器）。</param>
    /// <param name="output">输出写入器，用于记录日志。</param>
    /// <param name="commandParser">命令解析器，用于获取命令描述列表。</param>
    public OpenAiAgentService(
        AppConfig config,
        IServiceProvider serviceProvider,
        IOutputWriter output,
        ICommandParser commandParser)
    {
        _configs = config.AIConfigs;
        _serviceProvider = serviceProvider;
        _output = output;
        _commandParser = commandParser;

        // 启动后台消费者
        _processorTask = Task.Run(ProcessRequestsAsync);
    }

    // ---------- 实现 IAgentService ----------
    /// <summary>
    /// 发起一次普通聊天请求，返回所用模型名称与响应文本。
    /// 若对应配置未启用聊天功能，则不调用模型，直接返回提示文本。
    /// </summary>
    /// <param name="configName">AI 配置名称。</param>
    /// <param name="message">用户发送的聊天消息。</param>
    /// <param name="source">可选的请求来源（服务器与玩家），用于日志记录；默认为 null 表示来自控制台。</param>
    /// <returns>包含所用模型名称与模型响应文本的元组。</returns>
    public async Task<(string model, string response)> ChatAsync(
        string configName,
        string message,
        (IServer, string)? source = null)
    {
        var cfg = GetConfig(configName);
        // 检查该配置是否启用了聊天功能
        if (!cfg.EnableChat)
            return (cfg.Model, "该配置未启用聊天功能");

        // 创建请求并入队
        var req = new Request
        {
            Source = source,
            ConfigName = configName,
            Message = message,
            IsAgent = false,
            CancellationToken = _cts.Token
        };
        await _channel.Writer.WriteAsync(req, _cts.Token);
        return await req.Tcs.Task;
    }

    /// <summary>
    /// 发起一次智能体请求，返回所用模型名称与响应文本。
    /// 若对应配置未启用智能体功能，则不调用模型，直接返回提示文本。
    /// </summary>
    /// <param name="configName">AI 配置名称。</param>
    /// <param name="instruction">智能体指令。</param>
    /// <param name="source">可选的请求来源（服务器与玩家），用于日志记录；默认为 null 表示来自控制台。</param>
    /// <returns>包含所用模型名称与模型响应文本的元组。</returns>
    public async Task<(string model, string response)> AgentAsync(
        string configName,
        string instruction,
        (IServer, string)? source = null)
    {
        var cfg = GetConfig(configName);
        // 检查该配置是否启用了智能体功能
        if (!cfg.EnableAgent)
            return (cfg.Model, "该配置未启用智能体功能");

        // 创建请求并入队
        var req = new Request
        {
            Source = source,
            ConfigName = configName,
            Message = instruction,
            IsAgent = true,
            CancellationToken = _cts.Token
        };
        await _channel.Writer.WriteAsync(req, _cts.Token);
        return await req.Tcs.Task;
    }

    // ---------- 后台消费者 ----------
    /// <summary>
    /// 后台消费者：持续从队列读取请求并依次执行，将执行结果或异常回传给对应请求。
    /// </summary>
    private async Task ProcessRequestsAsync()
    {
        try
        {
            await foreach (var req in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                // 如果取消令牌已触发，则直接设置取消异常
                if (_cts.IsCancellationRequested)
                {
                    req.Tcs.TrySetCanceled(_cts.Token);
                    continue;
                }

                // 记录请求来源和消息
                var source = req.Source;
                string sourceInfo;
                if (source == null)
                {
                    sourceInfo = "来自控制台";
                }
                else
                {
                    (IServer sourceServer, string sourcePlayer) = source.Value;
                    sourceInfo = req.Source != null ? $"来自服务器 '{sourceServer.Name}' 的 '{sourcePlayer}'" : "来自控制台";
                }
                
                _output.Write($"AI/{req.ConfigName}", LogLevel.Debug,
                    $"处理请求 [{(req.IsAgent ? "Agent" : "Chat")}] {sourceInfo}: {req.Message}");

                try
                {
                    // 按请求类型分派到智能体或聊天执行逻辑
                    var result = req.IsAgent
                        ? await ExecuteAgentAsync(req.ConfigName, sourceInfo + req.Message)
                        : await ExecuteChatAsync(req.ConfigName, sourceInfo + req.Message);

                    req.Tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    // 单个请求执行失败时，将异常回传给等待方
                    _output.Write($"AI/{ req.ConfigName}", LogLevel.Error, $"处理失败: {ex.Message}");
                    req.Tcs.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            _output.Write("AI", LogLevel.Fatal, $"消费者异常: {ex.Message}");
        }
    }

    // ---------- 实际执行逻辑（拆分自原方法）----------
    /// <summary>
    /// 执行一次普通聊天调用：创建客户端并请求模型完成对话。
    /// </summary>
    /// <param name="configName">AI 配置名称。</param>
    /// <param name="message">聊天消息（已包含来源信息前缀）。</param>
    /// <returns>包含所用模型名称与模型响应文本的元组。</returns>
    private async Task<(string model, string response)> ExecuteChatAsync(string configName, string message)
    {
        var cfg = GetConfig(configName);
        var client = CreateClient(cfg);
        var chat = client.GetChatClient(cfg.Model);
        var result = await chat.CompleteChatAsync(message);
        return (cfg.Model, result.Value.Content[0].Text);
    }

    /// <summary>
    /// 执行一次智能体调用：模型可调用 execute_command / sleep 工具完成多步任务。
    /// 在最大迭代次数内循环处理工具调用，直至模型给出最终回答或达到迭代上限。
    /// </summary>
    /// <param name="configName">AI 配置名称。</param>
    /// <param name="instruction">智能体指令（已包含来源信息前缀）。</param>
    /// <returns>包含所用模型名称与模型响应文本的元组；达到最大迭代次数时返回提示文本。</returns>
    private async Task<(string model, string response)> ExecuteAgentAsync(string configName, string instruction)
    {
        var cfg = GetConfig(configName);
        var client = CreateClient(cfg);
        var chat = client.GetChatClient(cfg.Model);

        // 定义模型可调用的函数工具：执行命令与等待
        var tools = new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("execute_command", "执行$开头命令", BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new { command = new { type = "string" } },
                required = new[] { "command" }
            })),
            ChatTool.CreateFunctionTool("sleep", "等待秒数", BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new { duration = new { type = "number" } },
                required = new[] { "duration" }
            })),
        };

        // 组装系统提示与用户指令消息
        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(GetAgentPrompt(configName)),
            ChatMessage.CreateUserMessage(instruction)
        };

        // 将工具列表注册到完成请求选项中
        var options = new ChatCompletionOptions();
        foreach (var t in tools) options.Tools.Add(t);

        // 迭代上限：配置未设置时默认 16 次，防止无限循环
        int maxIter = cfg.MaxIterations > 0 ? cfg.MaxIterations : 16;
        for (int i = 0; i < maxIter; i++)
        {
            // 请求模型完成一次对话
            var response = await chat.CompleteChatAsync(messages, options);
            // 模型直接给出最终回答，结束循环
            if (response.Value.FinishReason == ChatFinishReason.Stop)
                return (cfg.Model, response.Value.Content[0].Text);

            // 模型请求调用工具：将助手消息与工具结果追加到对话历史后继续迭代
            if (response.Value.FinishReason == ChatFinishReason.ToolCalls)
            {
                messages.Add(ChatMessage.CreateAssistantMessage(response.Value));
                foreach (var tc in response.Value.ToolCalls)
                {
                    // 仅处理函数类型调用
                    if (tc.Kind == ChatToolCallKind.Function)
                    {
                        string argsJson = tc.FunctionArguments?.ToString() ?? "{}";
                        string toolResult;
                        try
                        {
                            toolResult = await ExecuteToolAsync(cfg.Model, tc.FunctionName, argsJson);
                        }
                        catch (Exception ex)
                        {
                            // 工具执行失败时返回错误文本，避免中断整个迭代
                            toolResult = $"工具执行错误: {ex.Message}";
                            _output.Write($"AI/{cfg.Model}", LogLevel.Error, toolResult);
                        }
                        messages.Add(ChatMessage.CreateToolMessage(tc.Id, toolResult));
                    }
                }
                continue;
            }
            // 其他结束原因（如长度限制），直接跳出循环
            break;
        }
        return (cfg.Model, "达到最大迭代次数");
    }

    // ---------- 辅助方法（与原相同）----------
    /// <summary>
    /// 按名称获取 AI 配置，并校验配置存在且 ApiKey 已设置。
    /// </summary>
    /// <param name="name">AI 配置名称。</param>
    /// <returns>匹配的 AI 配置。</returns>
    private AIConfig GetConfig(string name)
    {
        var cfg = _configs.GetValueOrDefault(name);
        if (cfg == null)
            throw new Exception($"AI 配置 '{name}' 不存在");
        if (string.IsNullOrEmpty(cfg.ApiKey))
            throw new InvalidOperationException($"AI 配置 '{name}' 的 ApiKey 未设置");
        return cfg;
    }

    /// <summary>
    /// 根据 AI 配置创建 OpenAI 客户端实例。
    /// </summary>
    /// <param name="cfg">AI 配置。</param>
    /// <returns>指向配置地址并使用其 ApiKey 的 OpenAI 客户端。</returns>
    private OpenAIClient CreateClient(AIConfig cfg)
        => new OpenAIClient(new ApiKeyCredential(cfg.ApiKey), new OpenAIClientOptions { Endpoint = new Uri(cfg.Url) });

    /// <summary>
    /// 执行模型发起的函数调用（execute_command / sleep）。
    /// </summary>
    /// <param name="model">当前使用的模型名称，用于日志标识。</param>
    /// <param name="func">函数名称。</param>
    /// <param name="argsJson">函数参数的 JSON 字符串。</param>
    /// <returns>工具执行结果文本；遇到未知工具时返回"未知工具"。</returns>
    private async Task<string> ExecuteToolAsync(string model, string func, string argsJson)
    {
        // 解析工具参数 JSON
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        // 执行 $ 开头的服务器命令
        if (func == "execute_command")
        {
            var cmd = root.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() : null;
            if (string.IsNullOrEmpty(cmd)) return "空命令";

            _output.Write($"AI/{model}", LogLevel.Info, $"执行命令: {cmd}");
            // 通过命令执行器执行命令，并收集其输出
            var executor = _serviceProvider.GetRequiredService<ICommandExecutor>();
            var outputCollector = new StringBuilderOutputWriter();
            var result = await executor.ExecuteAsync(cmd, outputCollector);
            var originalOutput = result.Output;
            _output.Write($"AI/{model}", LogLevel.Info, $"命令返回: {originalOutput}");
            return originalOutput;
        }
        // 等待指定秒数
        else if (func == "sleep")
        {
            var dur = root.TryGetProperty("duration", out var durProp) ? durProp.GetInt32() : 0;
            // 无效时长直接返回错误提示
            if (dur <= 0) return "无效等待时间";
            _output.Write($"AI/{model}", LogLevel.Info, $"休眠 {dur} 秒");
            await Task.Delay(dur * 1000);
            return $"已休眠 {dur} 秒";
        }
        // 未匹配任何已知工具
        return "未知工具";
    }

    /// <summary>
    /// 生成智能体系统提示词：将命令描述列表注入配置的提示词模板。
    /// </summary>
    /// <param name="configName">AI 配置名称。</param>
    /// <returns>最终的系统提示词文本。</returns>
    private string GetAgentPrompt(string configName)
    {
        var cfg = _configs.GetValueOrDefault(configName);
        // 以内置默认提示词作为兜底
        var systemPrompt = new AIConfig().AgentPrompt;
        if (cfg == null) return systemPrompt;

        var promptTemplate = cfg.AgentPrompt;
        if (string.IsNullOrWhiteSpace(promptTemplate))
            return systemPrompt;

        // 生成按命令名排序的命令描述列表
        var commandDescriptions = _commandParser.GetCommandDescriptions();
        var commandList = string.Join("\n", commandDescriptions
            .OrderBy(k => k.Key)
            .Select(kvp => $"- {kvp.Key}：{kvp.Value}"));

        // 将命令列表注入模板的 {commandList} 占位符
        return promptTemplate.Replace("{commandList}", commandList);
    }

    /// <summary>
    /// 将输出写入内存缓冲区的 IOutputWriter 实现，用于捕获命令执行输出。
    /// </summary>
    private class StringBuilderOutputWriter : IOutputWriter
    {
        /// <summary>内部字符串缓冲区。</summary>
        private readonly StringBuilder _sb = new();
        /// <summary>
        /// 将消息追加到缓冲区（忽略日志级别与时间戳）。
        /// </summary>
        /// <param name="context">日志上下文，此处不参与处理。</param>
        /// <param name="level">日志级别，此处不参与处理。</param>
        /// <param name="message">要写入的消息内容。</param>
        /// <param name="includeTimestamp">是否包含时间戳，此处不参与处理。</param>
        public void Write(string context, LogLevel level, string message, bool includeTimestamp = true)
            => _sb.AppendLine(message);
        /// <summary>
        /// 返回缓冲区中的全部内容。
        /// </summary>
        /// <returns>已写入的消息拼接结果。</returns>
        public string GetContent() => _sb.ToString();
    }

    // ---------- 资源清理 ----------
    /// <summary>
    /// 释放资源：取消后台消费者并等待其退出，随后释放取消令牌并完成队列写入。
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _processorTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch { /* 忽略超时 */ }
        _cts.Dispose();
        _channel.Writer.TryComplete();
    }
}
