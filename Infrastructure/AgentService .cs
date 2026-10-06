using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;
using MSL_CLI.Infrastructure.OpenAi;

namespace MSL_CLI.Infrastructure;

/// <summary>
/// 基于 OpenAI 的智能体服务实现，负责处理聊天与智能体请求。
/// 请求通过无界通道排队，由单个后台消费者串行处理，处理结果经 TaskCompletionSource 回传给调用方。
/// 智能体执行的每条命令都必须先通过授权网关获得操作员许可，无法确认时一律拒绝。
/// </summary>
public class OpenAiAgentService : IAgentService, IDisposable
{
    /// <summary>AI 配置字典，键为配置名称。可通过 <see cref="ReloadConfig"/> 整体替换。</summary>
    private volatile Dictionary<string, AIConfig> _configs;
    /// <summary>全局 AI 开关（AppConfig.EnableAI），随 <see cref="ReloadConfig"/> 热更新。</summary>
    private volatile bool _enableAI = true;
    /// <summary>服务提供者，用于按需解析命令执行器等依赖。</summary>
    private readonly IServiceProvider _serviceProvider;
    /// <summary>输出写入器，用于记录日志信息。</summary>
    private readonly IOutputWriter _output;
    /// <summary>命令解析器，用于获取命令描述列表以生成智能体提示词。</summary>
    private readonly ICommandParser _commandParser;
    /// <summary>命令授权网关，负责在执行代理命令前向本地操作员请求许可。</summary>
    private readonly IAgentPermissionGateway _permissions;
    /// <summary>服务器注册表，供命令的参数校验钩子使用。</summary>
    private readonly IServerRegistry _serverRegistry;
    /// <summary>配置存储，供命令的参数校验钩子使用。</summary>
    private readonly IConfigurationStore _configStore;

    /// <summary>逐条消息的固定格式开销，估算 token 时按每条累加。</summary>
    private const int MessageOverheadTokens = 4;
    /// <summary>三个工具定义的 JSON Schema 所占上下文开销（估算 token 数）。</summary>
    private const int ToolSchemaReserveTokens = 512;

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
        /// <summary>请求是否来自本地控制台；只有控制台请求才有资格向操作员请求授权。</summary>
        public bool FromConsole { get; init; }
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
    /// <param name="permissions">命令授权网关，用于执行代理命令前请求操作员许可。</param>
    /// <param name="serverRegistry">服务器注册表，供命令的参数校验钩子使用。</param>
    /// <param name="configStore">配置存储，供命令的参数校验钩子使用。</param>
    public OpenAiAgentService(
        AppConfig config,
        IServiceProvider serviceProvider,
        IOutputWriter output,
        ICommandParser commandParser,
        IAgentPermissionGateway permissions,
        IServerRegistry serverRegistry,
        IConfigurationStore configStore)
    {
        _configs = config.AIConfigs;
        _enableAI = config.EnableAI;
        _serviceProvider = serviceProvider;
        _output = output;
        _commandParser = commandParser;
        _permissions = permissions;
        _serverRegistry = serverRegistry;
        _configStore = configStore;

        // 启动后台消费者
        _processorTask = Task.Run(ProcessRequestsAsync);
    }

    // ---------- 实现 IAgentService ----------
    /// <summary>
    /// 当前已登记的 AI 配置名集合（每次读取时从最新的配置字典快照生成）。
    /// </summary>
    public IReadOnlyCollection<string> ConfigNames => _configs.Keys.ToList();

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

        // 全局开关：EnableAI 为 false 时任何 AI 调用都不发起
        if (!_enableAI)
            return (cfg.Model, "AI 功能已在配置中关闭（EnableAI = false），可用 $ai cfg set EnableAI true 打开");

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
            FromConsole = source == null,
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

        // 全局开关：EnableAI 为 false 时任何 AI 调用都不发起
        if (!_enableAI)
            return (cfg.Model, "AI 功能已在配置中关闭（EnableAI = false），可用 $ai cfg set EnableAI true 打开");

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
            FromConsole = source == null,
            CancellationToken = _cts.Token
        };
        await _channel.Writer.WriteAsync(req, _cts.Token);
        return await req.Tcs.Task;
    }

    // ---------- 配置热更新 ----------
    /// <summary>
    /// 用新的应用配置替换内存中的 AI 实例集合。
    /// 后台消费者每轮按引用读取配置字典，这里整体替换引用，
    /// 使 $ai add / $ai rm、$app cfg 修改、$app reload 等操作无需重启即可生效。
    /// </summary>
    /// <param name="newConfig">新的应用配置。</param>
    public void ReloadConfig(AppConfig newConfig)
    {
        _configs = newConfig?.AIConfigs ?? new Dictionary<string, AIConfig>();
        _enableAI = newConfig?.EnableAI ?? true;
        _output.Write("AI", LogLevel.Info,
            $"AI 配置已重新加载（{_configs.Count} 个实例，EnableAI={_enableAI}）");
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
                // 来源标注会被拼在指令开头，提示词依赖它区分“控制台（完全权限）”与
                // “游戏内玩家（需管理员校验）”，因此格式必须与提示词约定完全一致，
                // 并用空格与指令正文分隔，避免模型把配置名/首个单词误认成玩家名。
                var source = req.Source;
                string sourceLabel;
                if (source == null)
                {
                    sourceLabel = "[来自控制台]";
                }
                else
                {
                    (IServer sourceServer, string sourcePlayer) = source.Value;
                    sourceLabel = $"[来自服务器 '{sourceServer.Name}' 的玩家 '{sourcePlayer}']";
                }

                _output.Write($"AI/{req.ConfigName}", LogLevel.Debug,
                    $"处理请求 [{(req.IsAgent ? "Agent" : "Chat")}] {sourceLabel} {req.Message}");

                try
                {
                    // 按请求类型分派到智能体或聊天执行逻辑
                    var instruction = $"{sourceLabel} {req.Message}";
                    var result = req.IsAgent
                        ? await ExecuteAgentAsync(req, instruction)
                        : await ExecuteChatAsync(req, instruction);

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
    /// 执行一次普通聊天调用：创建客户端，以“系统提示词 + 用户消息”请求模型完成对话。
    /// 聊天模式不挂载工具，模型只回答问题、不会执行命令。
    /// </summary>
    /// <param name="req">发起本次调用的请求。</param>
    /// <param name="message">聊天消息（已包含来源信息前缀）。</param>
    /// <returns>包含所用模型名称与模型响应文本的元组。</returns>
    private async Task<(string model, string response)> ExecuteChatAsync(Request req, string message)
    {
        var cfg = GetConfig(req.ConfigName);
        var client = CreateClient(cfg);
        var chat = client.GetChatClient(cfg.Model);

        // 组装系统提示与用户消息
        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(GetChatPrompt(req.ConfigName)),
            ChatMessage.CreateUserMessage(message)
        };

        var result = await chat.CompleteChatAsync(messages, cancellationToken: req.CancellationToken);
        // 内容可能为空（模型拒答、只返回工具调用等），直接索引会抛 ArgumentOutOfRangeException
        return (cfg.Model, result.Value.Content.Count > 0 ? result.Value.Content[0].Text : string.Empty);
    }

    /// <summary>
    /// 执行一次智能体调用：模型可调用 request_permission / execute_command / sleep 工具完成多步任务。
    /// 在最大迭代次数内循环处理工具调用，直至模型给出最终回答或达到迭代上限。
    /// </summary>
    /// <param name="req">发起本次调用的请求，提供来源与取消令牌。</param>
    /// <param name="instruction">智能体指令（已包含来源信息前缀）。</param>
    /// <returns>包含所用模型名称与模型响应文本的元组；达到最大迭代次数时返回提示文本。</returns>
    private async Task<(string model, string response)> ExecuteAgentAsync(Request req, string instruction)
    {
        var cfg = GetConfig(req.ConfigName);
        var client = CreateClient(cfg);
        var chat = client.GetChatClient(cfg.Model);

        // 定义模型可调用的函数工具：申请授权、执行命令与等待
        var tools = new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("request_permission",
                "在执行命令前向操作员申请授权。已获白名单放行的命令无需调用。必须先获得允许才能调用 execute_command。",
                BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        command = new { type = "string", description = "需要申请授权的完整$命令，例如 $stop server1" },
                        reason = new { type = "string", description = "执行该命令的理由" }
                    },
                    required = new[] { "command" }
                })),
            ChatTool.CreateFunctionTool("execute_command",
                "执行$开头命令，需已通过授权（在允许列表中或已获批）。",
                BinaryData.FromObjectAsJson(new
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
            ChatMessage.CreateSystemMessage(GetAgentPrompt(req.ConfigName)),
            ChatMessage.CreateUserMessage(instruction)
        };

        // 执行过程的文本记录，与 messages[2..] 一一对应；上下文压缩时交给模型总结
        var history = new StringBuilder();
        // 上下文超限但没有可压缩历史时只提示一次，避免每轮刷屏
        bool lengthWarned = false;

        // 将工具列表注册到完成请求选项中
        var options = new ChatCompletionOptions();
        foreach (var t in tools) options.Tools.Add(t);

        // 迭代上限：配置未设置时默认 16 次，防止无限循环
        int maxIter = cfg.MaxIterations > 0 ? cfg.MaxIterations : 16;
        for (int i = 0; i < maxIter; i++)
        {
            // 上下文过长时先压缩历史再请求，避免超出模型上下文窗口
            if (cfg.MaxContextTokens > 0)
            {
                int estimated = EstimateContextTokens(messages);
                if (estimated > cfg.MaxContextTokens)
                {
                    // 历史部分占比过小时，超长主要来自系统提示词或指令本身，压缩收益有限
                    if (EstimateHistoryTokens(messages) < cfg.MaxContextTokens / 4)
                    {
                        if (!lengthWarned)
                        {
                            lengthWarned = true;
                            _output.Write($"AI/{cfg.Model}", LogLevel.Warning,
                                $"上下文估算 {estimated} tokens 已超过阈值 {cfg.MaxContextTokens}，" +
                                "但没有足够的历史可供压缩，继续请求可能超出模型窗口");
                        }
                    }
                    else
                    {
                        await CompressContextAsync(chat, cfg, messages, history, req);
                    }
                }
            }

            // 请求模型完成一次对话
            var response = await chat.CompleteChatAsync(messages, options, req.CancellationToken);
            // 模型直接给出最终回答，结束循环
            if (response.Value.FinishReason == ChatFinishReason.Stop)
                return (cfg.Model, response.Value.Content.Count > 0 ? response.Value.Content[0].Text : string.Empty);

            // 模型请求调用工具：将助手消息与工具结果追加到对话历史后继续迭代
            if (response.Value.FinishReason == ChatFinishReason.ToolCalls)
            {
                messages.Add(ChatMessage.CreateAssistantMessage(response.Value));
                AppendAssistantTranscript(history, response.Value);
                foreach (var tc in response.Value.ToolCalls)
                {
                    // 仅处理函数类型调用
                    if (tc.Kind == ChatToolCallKind.Function)
                    {
                        string argsJson = tc.FunctionArguments?.ToString() ?? "{}";
                        string toolResult;
                        try
                        {
                            toolResult = await ExecuteToolAsync(req, cfg.Model, tc.FunctionName, argsJson);
                        }
                        catch (Exception ex)
                        {
                            // 工具执行失败时返回错误文本，避免中断整个迭代
                            toolResult = $"工具执行错误: {ex.Message}";
                            _output.Write($"AI/{cfg.Model}", LogLevel.Error, toolResult);
                        }
                        messages.Add(ChatMessage.CreateToolMessage(tc.Id, toolResult));
                        history.AppendLine($"工具 {tc.FunctionName} 返回: {toolResult}");
                    }
                }
                continue;
            }
            // 其他结束原因（如长度限制），直接跳出循环
            break;
        }
        return (cfg.Model, "达到最大迭代次数");
    }

    // ---------- 上下文长度控制（自动压缩）----------
    /// <summary>
    /// 压缩智能体的执行历史：把此前的工具调用与返回交给模型总结成一段摘要，
    /// 再用摘要替换 messages 中除“系统提示 + 原始指令”之外的对话记录，从而缩短上下文。
    /// 摘要按阈值比例设上限，使压缩后的长度远低于触发条件，避免每轮反复压缩。
    /// </summary>
    /// <param name="chat">当前使用的聊天客户端。</param>
    /// <param name="cfg">当前 AI 配置，提供阈值与压缩提示词。</param>
    /// <param name="messages">会被就地改写的对话消息列表。</param>
    /// <param name="history">与 messages 对应的执行过程文本记录，压缩后同步替换为摘要。</param>
    /// <param name="req">发起本次调用的请求，提供取消令牌。</param>
    private async Task CompressContextAsync(
        ChatClient chat, AIConfig cfg, List<ChatMessage> messages, StringBuilder history, Request req)
    {
        int before = EstimateContextTokens(messages);
        var transcript = history.ToString();

        // 摘要字符上限：按阈值比例收敛，同时给出下限避免摘要被压得过短而丢失关键信息
        int summaryCharLimit = SummaryCharLimit(cfg.MaxContextTokens);
        var summary = await SummarizeHistoryAsync(chat, cfg, transcript, req);
        if (summary.Length > summaryCharLimit)
            summary = summary[..summaryCharLimit] + "…";

        // 用摘要替换除“系统提示 + 原始指令”以外的全部对话记录
        messages.RemoveRange(2, messages.Count - 2);
        messages.Add(ChatMessage.CreateSystemMessage(
            "以下是本次任务此前执行过程的摘要，请据此继续，不要重复已完成的步骤：\n" + summary));

        // 记录同步换血：后续再次压缩时会把先前的摘要一并纳入
        history.Clear();
        history.AppendLine("【历史摘要】").AppendLine(summary);

        _output.Write($"AI/{cfg.Model}", LogLevel.Success,
            $"上下文已压缩: {before} -> {EstimateContextTokens(messages)} 估算 tokens（摘要 {summary.Length} 字符）");
    }

    /// <summary>
    /// 调用模型把执行记录总结为摘要；模型调用失败或未返回文本时退化为截断压缩，
    /// 保证压缩不会中断任务。
    /// </summary>
    /// <param name="chat">当前使用的聊天客户端。</param>
    /// <param name="cfg">当前 AI 配置，提供压缩提示词。</param>
    /// <param name="transcript">待压缩的执行记录文本。</param>
    /// <param name="req">发起本次调用的请求，提供取消令牌。</param>
    /// <returns>压缩后的摘要文本。</returns>
    private async Task<string> SummarizeHistoryAsync(ChatClient chat, AIConfig cfg, string transcript, Request req)
    {
        // 压缩请求本身也必须放进模型窗口：记录过长时只保留尾部（预算约为阈值的一半），
        // 否则摘要调用会因输入超长直接失败
        int transcriptBudget = TranscriptCharBudget(cfg.MaxContextTokens);
        if (EstimateTokens(transcript) > transcriptBudget)
            transcript = TruncateTranscript(transcript, transcriptBudget);

        var prompt = string.IsNullOrWhiteSpace(cfg.ContextCompressPrompt)
            ? AIConfig.DefaultCompressPrompt
            : cfg.ContextCompressPrompt;

        // 模板未包含占位符时把执行记录追加到末尾，保证记录一定被送入模型
        prompt = prompt.Contains("{transcript}")
            ? prompt.Replace("{transcript}", transcript)
            : prompt + "\n\n执行记录：\n" + transcript;

        try
        {
            var response = await chat.CompleteChatAsync(
                new List<ChatMessage> { ChatMessage.CreateUserMessage(prompt) },
                cancellationToken: req.CancellationToken);

            var text = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : null;
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();

            _output.Write($"AI/{cfg.Model}", LogLevel.Warning, "上下文压缩未返回文本，改用截断压缩");
        }
        catch (Exception ex)
        {
            // 压缩失败不应中断任务：退化为截断压缩，并保留原因便于排查
            _output.Write($"AI/{cfg.Model}", LogLevel.Error, $"上下文压缩失败，改用截断压缩: {ex.Message}");
        }

        return TruncateTranscript(transcript, SummaryCharLimit(cfg.MaxContextTokens));
    }

    /// <summary>
    /// 截断压缩兜底：保留执行记录的尾部（最近的步骤最关键），并按字符上限裁剪。
    /// </summary>
    /// <param name="transcript">待压缩的执行记录文本。</param>
    /// <param name="charLimit">允许保留的最大字符数。</param>
    /// <returns>裁剪后的记录文本。</returns>
    private static string TruncateTranscript(string transcript, int charLimit)
        => transcript.Length <= charLimit ? transcript : "（更早的记录已省略）\n" + transcript[^charLimit..];

    /// <summary>
    /// 根据压缩阈值计算摘要允许的最大字符数（约为阈值的八分之一，且不低于 400）。
    /// </summary>
    /// <param name="maxContextTokens">上下文压缩阈值。</param>
    /// <returns>摘要字符上限。</returns>
    private static int SummaryCharLimit(int maxContextTokens) => Math.Max(400, maxContextTokens / 8);

    /// <summary>
    /// 根据压缩阈值计算送入模型的执行记录允许的最大字符数（约为阈值的一半，且不低于 2000）。
    /// 中日韩字符按 1 字符 ≈ 1 token 估算，该预算可保证压缩请求本身不超过阈值。
    /// </summary>
    /// <param name="maxContextTokens">上下文压缩阈值。</param>
    /// <returns>执行记录字符上限。</returns>
    private static int TranscriptCharBudget(int maxContextTokens) => Math.Max(2000, maxContextTokens / 2);

    /// <summary>
    /// 把一次工具调用轮次的助手输出追加到执行记录中。
    /// </summary>
    /// <param name="history">执行记录缓冲区。</param>
    /// <param name="completion">模型返回的完成结果。</param>
    private static void AppendAssistantTranscript(StringBuilder history, ChatCompletion completion)
    {
        var text = completion.Content.Count > 0 ? completion.Content[0].Text : null;
        if (!string.IsNullOrWhiteSpace(text))
            history.AppendLine($"助手: {text.Trim()}");

        foreach (var tc in completion.ToolCalls)
        {
            if (tc.Kind == ChatToolCallKind.Function)
                history.AppendLine($"助手调用工具 {tc.FunctionName}({tc.FunctionArguments})");
        }
    }

    /// <summary>
    /// 估算整个上下文（含工具定义与逐条消息开销）的 token 数。
    /// </summary>
    /// <param name="messages">对话消息列表。</param>
    /// <returns>估算的 token 数。</returns>
    private static int EstimateContextTokens(IReadOnlyList<ChatMessage> messages)
    {
        int total = ToolSchemaReserveTokens;
        for (int i = 0; i < messages.Count; i++)
            total += MessageOverheadTokens + EstimateMessageTokens(messages[i]);
        return total;
    }

    /// <summary>
    /// 估算除“系统提示 + 原始指令”之外的历史部分所占的 token 数。
    /// </summary>
    /// <param name="messages">对话消息列表。</param>
    /// <returns>估算的 token 数。</returns>
    private static int EstimateHistoryTokens(IReadOnlyList<ChatMessage> messages)
    {
        int total = 0;
        for (int i = 2; i < messages.Count; i++)
            total += MessageOverheadTokens + EstimateMessageTokens(messages[i]);
        return total;
    }

    /// <summary>
    /// 估算单条消息文本内容所占的 token 数。
    /// </summary>
    /// <param name="message">对话消息。</param>
    /// <returns>估算的 token 数。</returns>
    private static int EstimateMessageTokens(ChatMessage message)
    {
        int total = 0;
        foreach (var part in message.Content)
            total += EstimateTokens(part.Text);
        return total;
    }

    /// <summary>
    /// 粗略估算文本的 token 数：中日韩字符按 1 字符 ≈ 1 token，其余字符按 4 字符 ≈ 1 token。
    /// 不引入分词器依赖，仅用于判断是否触发压缩，误差由阈值留白吸收。
    /// </summary>
    /// <param name="text">待估算的文本。</param>
    /// <returns>估算的 token 数。</returns>
    private static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        int cjk = 0, other = 0;
        foreach (var ch in text)
        {
            if ((ch >= 0x2E80 && ch <= 0x9FFF) ||   // 中日韩汉字与部首
                (ch >= 0xAC00 && ch <= 0xD7AF) ||   // 韩文音节
                (ch >= 0xF900 && ch <= 0xFAFF) ||   // 兼容汉字
                (ch >= 0xFF00 && ch <= 0xFFEF))     // 全角字符
                cjk++;
            else
                other++;
        }

        return cjk + (other + 3) / 4;
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
    /// 执行模型发起的函数调用（request_permission / execute_command / sleep）。
    /// </summary>
    /// <param name="req">发起本次工具调用的请求，提供来源与取消令牌。</param>
    /// <param name="model">当前使用的模型名称，用于日志标识。</param>
    /// <param name="func">函数名称。</param>
    /// <param name="argsJson">函数参数的 JSON 字符串。</param>
    /// <returns>工具执行结果文本；遇到未知工具时返回"未知工具"。</returns>
    private async Task<string> ExecuteToolAsync(Request req, string model, string func, string argsJson)
    {
        // 解析工具参数 JSON
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        // 向操作员申请命令授权
        if (func == "request_permission")
        {
            var cmd = root.TryGetProperty("command", out var permProp) ? permProp.GetString() : null;
            if (string.IsNullOrEmpty(cmd)) return "空命令";

            // 先拦截不存在的命令/不合法的子命令：不浪费一次授权询问，也不让它进入白名单
            var validation = ValidateCommand(req, cmd);
            if (!validation.IsValid)
            {
                _output.Write($"AI/{model}", LogLevel.Warning,
                    $"拒绝申请授权（命令或参数无效）: {cmd}");
                return validation.Message;
            }

            var reason = root.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : null;

            _output.Write($"AI/{model}", LogLevel.Info, $"申请执行授权: {cmd}");
            var permission = await _permissions.RequestAsync(cmd, reason, BuildRequester(req), req.CancellationToken);

            // 获批后登记一次性放行，使随后的 execute_command 无需重复提问
            if (permission.IsAllowed)
                _permissions.GrantSessionPass(cmd);

            return permission.message;
        }

        // 执行 $ 开头的服务器命令
        if (func == "execute_command")
        {
            var cmd = root.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() : null;
            if (string.IsNullOrEmpty(cmd)) return "空命令";

            // 先拦截不存在的命令/不合法的子命令：避免它被当作“未授权”而触发无意义的授权询问
            var validation = ValidateCommand(req, cmd);
            if (!validation.IsValid)
            {
                _output.Write($"AI/{model}", LogLevel.Warning, $"拒绝执行（命令或参数无效）: {cmd}");
                return validation.Message;
            }

            // 硬性拦截：白名单放行或本次已获批才允许执行，否则就地请求授权
            var message = await EnsureAuthorizedAsync(req, model, cmd);
            if (message != null) return message;

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
            await Task.Delay(dur * 1000, req.CancellationToken);
            return $"已休眠 {dur} 秒";
        }
        // 未匹配任何已知工具
        return "未知工具";
    }

    /// <summary>
    /// 确保一条命令已获授权：优先消费“先询问后执行”留下的会话放行记录，
    /// 其次命中持久化白名单，最后就地发起授权请求。
    /// </summary>
    /// <param name="req">发起本次工具调用的请求。</param>
    /// <param name="model">当前模型名称，用于日志标识。</param>
    /// <param name="command">即将执行的命令。</param>
    /// <returns>已获授权时返回 null；否则返回应当回传给模型的拒绝说明。</returns>
    private async Task<string?> EnsureAuthorizedAsync(Request req, string model, string command)
    {
        // 该命令此前已通过 request_permission 获得一次性放行
        if (_permissions.TryConsumeSessionPass(command))
            return null;

        // 命中持久化白名单，无需询问
        if (_permissions.IsAllowedByPolicy(command))
            return null;

        // 未获授权：就地请求许可，避免模型跳过 request_permission 直接执行
        _output.Write($"AI/{model}", LogLevel.Warning, $"命令未获授权，正在请求确认: {command}");
        var permission = await _permissions.RequestAsync(
            command,
            "模型直接请求执行该命令",
            BuildRequester(req),
            req.CancellationToken);

        return permission.IsAllowed
            ? null
            : $"命令未执行：{permission.message}。请改用 request_permission 再次申请，或向操作员说明情况。";
    }

    /// <summary>
    /// 校验命令是否存在、参数是否合法，并构造供命令自行校验参数用的上下文。
    /// </summary>
    /// <param name="req">发起本次工具调用的请求。</param>
    /// <param name="command">待校验的完整命令文本。</param>
    /// <returns>校验结果。</returns>
    private CommandValidation ValidateCommand(Request req, string command)
    {
        // 复用同一套依赖构造上下文，使命令的参数校验钩子能访问服务器注册表等
        var context = new CommandArgs(
            CommandInvocationValidator.ExtractRawArgs(command),
            _serverRegistry,
            this,
            _configStore)
        {
            Parser = _commandParser
        };

        return CommandInvocationValidator.Validate(_commandParser, command, context);
    }

    /// <summary>
    /// 由请求来源构造授权来源对象。
    /// </summary>
    /// <param name="req">排队中的请求。</param>
    /// <returns>授权来源；控制台请求对应 <see cref="IAgentPermissionGateway.Requester.Console"/>。</returns>
    private static IAgentPermissionGateway.Requester BuildRequester(Request req)
    {
        if (req.Source is { } source)
            return IAgentPermissionGateway.Requester.FromPlayer(source.Item1, source.Item2);

        return IAgentPermissionGateway.Requester.Console;
    }

    /// <summary>
    /// 生成智能体系统提示词：将命令描述列表注入配置的提示词模板。
    /// </summary>
    /// <param name="configName">AI 配置名称。</param>
    /// <returns>最终的系统提示词文本。</returns>
    private string GetAgentPrompt(string configName)
    {
        var cfg = _configs.GetValueOrDefault(configName);

        // 配置缺失或提示词留空时回退到内置默认；无论走哪条路都必须填充 {commandList}，
        // 否则模型看到的是字面量占位符（内置默认提示词本身就含该占位符）。
        var promptTemplate = string.IsNullOrWhiteSpace(cfg?.AgentPrompt)
            ? new AIConfig().AgentPrompt
            : cfg!.AgentPrompt;

        return promptTemplate.Replace("{commandList}", BuildCommandList());
    }

    /// <summary>
    /// 生成聊天模式的系统提示词：将命令描述列表注入配置的提示词模板。
    /// 聊天模式不挂载工具，提示词只用于约束回答风格与范围。
    /// </summary>
    /// <param name="configName">AI 配置名称。</param>
    /// <returns>最终的系统提示词文本。</returns>
    private string GetChatPrompt(string configName)
    {
        var cfg = _configs.GetValueOrDefault(configName);

        // 同 GetAgentPrompt：内置默认提示词同样含 {commandList}，必须显式替换
        var promptTemplate = string.IsNullOrWhiteSpace(cfg?.ChatPrompt)
            ? new AIConfig().ChatPrompt
            : cfg!.ChatPrompt;

        return promptTemplate.Replace("{commandList}", BuildCommandList());
    }

    /// <summary>
    /// 构造按命令名排序的命令描述列表，供系统提示词的 {commandList} 占位符使用。
    /// </summary>
    /// <returns>每行一条的命令摘要文本。</returns>
    private string BuildCommandList()
        => string.Join("\n", _commandParser.GetCommandDescriptions()
            .OrderBy(k => k.Key)
            .Select(kvp => $"- {kvp.Key}：{kvp.Value}"));

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

        // 消费者退出后仍留在队列里的请求永远不会再被处理：
        // 必须显式取消它们的任务源，否则 await req.Tcs.Task 的调用方（含 fire-and-forget 的
        // 游戏内触发任务）会永久挂起
        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out var pending))
            pending.Tcs.TrySetCanceled();

        // 只有在消费者确实已结束的情况下才释放取消令牌源；
        // 否则仍在进行的模型调用可能会碰到已释放的 token
        if (_processorTask.IsCompleted)
            _cts.Dispose();
    }
}
