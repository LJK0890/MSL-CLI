using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSL_CLI.Infrastructure.OpenAi;

// ---------------------------------------------------------------------------
// MSL-CLI 内置的“OpenAI 兼容”聊天客户端。
//
// 为什么不用官方 OpenAI SDK：MSL-CLI 只用到其中很小一部分能力（Chat Completions +
// Function Calling），为此引入完整 SDK 并不划算。这里用 BCL 的 HttpClient +
// System.Text.Json 自行实现这部分能力；类型名与官方 SDK 保持一致，使 AgentService
// 的实现无需为了更换客户端而改动。
//
// 请求：POST {Endpoint}/chat/completions，Authorization: Bearer <ApiKey>
// 支持：system/user/assistant/tool 消息、工具定义、工具调用往返。
// ---------------------------------------------------------------------------

/// <summary>
/// API 密钥凭据。对应官方 SDK 的 ApiKeyCredential。
/// </summary>
public sealed class ApiKeyCredential
{
    /// <summary>密钥明文。</summary>
    public string Key { get; }

    /// <summary>
    /// 构造凭据。
    /// </summary>
    /// <param name="key">API 密钥；为空时请求会因鉴权失败而被服务端拒绝。</param>
    public ApiKeyCredential(string key) => Key = key ?? string.Empty;
}

/// <summary>
/// 客户端选项。对应官方 SDK 的 OpenAIClientOptions，这里只保留服务地址。
/// </summary>
public sealed class OpenAIClientOptions
{
    /// <summary>服务基地址，例如 https://api.openai.com/v1。</summary>
    public Uri? Endpoint { get; set; }
}

/// <summary>
/// 指向某个 OpenAI 兼容服务的客户端。
/// </summary>
public sealed class OpenAIClient
{
    /// <summary>服务基地址；未配置时回退到 OpenAI 官方地址。</summary>
    private readonly Uri _endpoint;
    /// <summary>API 密钥。</summary>
    private readonly string _apiKey;

    /// <summary>
    /// 构造客户端。
    /// </summary>
    /// <param name="credential">API 密钥凭据。</param>
    /// <param name="options">客户端选项，提供 Endpoint。</param>
    public OpenAIClient(ApiKeyCredential credential, OpenAIClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _apiKey = credential.Key;
        _endpoint = options?.Endpoint ?? new Uri("https://api.openai.com/v1");
    }

    /// <summary>
    /// 获取针对指定模型的聊天客户端。
    /// </summary>
    /// <param name="model">模型名称，例如 deepseek-chat。</param>
    /// <returns>聊天客户端。</returns>
    public ChatClient GetChatClient(string model) => new(_endpoint, _apiKey, model);
}

/// <summary>
/// 包装一次调用结果，对应官方 SDK 的 ClientResult，使调用方仍以 <c>result.Value</c> 取值。
/// </summary>
/// <typeparam name="T">结果类型。</typeparam>
public sealed class ClientResult<T>
{
    /// <summary>实际结果。</summary>
    public T Value { get; }

    /// <summary>
    /// 构造结果包装。
    /// </summary>
    /// <param name="value">实际结果。</param>
    public ClientResult(T value) => Value = value;
}

/// <summary>
/// 模型结束本轮生成的原因。
/// </summary>
public enum ChatFinishReason
{
    /// <summary>模型给出了最终回答。</summary>
    Stop,
    /// <summary>模型请求调用工具。</summary>
    ToolCalls,
    /// <summary>达到长度上限。</summary>
    Length,
    /// <summary>被内容过滤拦截。</summary>
    ContentFilter,
    /// <summary>其他未知原因。</summary>
    Unknown
}

/// <summary>
/// 工具调用类型；目前只支持函数。
/// </summary>
public enum ChatToolCallKind
{
    /// <summary>函数调用。</summary>
    Function
}

/// <summary>
/// 一段二进制/JSON 负载，对应官方 SDK 的 BinaryData；这里仅承载 JSON 文本。
/// </summary>
public sealed class BinaryData
{
    /// <summary>序列化所用的选项：不转义中文，便于日志排查。</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>JSON 文本。</summary>
    private readonly string _json;

    /// <summary>
    /// 直接以 JSON 文本构造。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    private BinaryData(string json) => _json = json;

    /// <summary>
    /// 把对象序列化为 JSON 负载。
    /// </summary>
    /// <typeparam name="T">对象类型。</typeparam>
    /// <param name="value">待序列化的对象。</param>
    /// <returns>承载 JSON 的负载。</returns>
    public static BinaryData FromObjectAsJson<T>(T value)
        => new(JsonSerializer.Serialize(value, SerializerOptions));

    /// <summary>
    /// 由已有的 JSON 文本构造负载（用于把服务端返回的 arguments 原样回传）。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>承载 JSON 的负载。</returns>
    public static BinaryData FromJson(string json) => new(json);

    /// <summary>
    /// 返回 JSON 文本，使 <c>FunctionArguments?.ToString()</c> 与原实现一致。
    /// </summary>
    /// <returns>JSON 文本。</returns>
    public override string ToString() => _json;
}

/// <summary>
/// 消息内容分片；当前只承载文本。
/// </summary>
public sealed class ChatMessageContentPart
{
    /// <summary>文本内容。</summary>
    public string Text { get; }

    /// <summary>
    /// 构造文本分片。
    /// </summary>
    /// <param name="text">文本内容。</param>
    public ChatMessageContentPart(string text) => Text = text ?? string.Empty;
}

/// <summary>
/// 一条对话消息，对应官方 SDK 的 ChatMessage。
/// </summary>
public sealed class ChatMessage
{
    /// <summary>消息角色：system / user / assistant / tool。</summary>
    internal string Role { get; private init; } = "user";
    /// <summary>文本内容分片；助手消息只调用工具时可能为空。</summary>
    public IReadOnlyList<ChatMessageContentPart> Content { get; private init; } = Array.Empty<ChatMessageContentPart>();
    /// <summary>助手消息携带的工具调用。</summary>
    public IReadOnlyList<ChatToolCall> ToolCalls { get; private init; } = Array.Empty<ChatToolCall>();
    /// <summary>工具消息对应的调用 ID。</summary>
    internal string? ToolCallId { get; private init; }

    /// <summary>原始文本（无分片时使用）。</summary>
    private string Text => Content.Count > 0 ? Content[0].Text : string.Empty;

    /// <summary>构造系统消息。</summary>
    /// <param name="content">系统提示词。</param>
    /// <returns>系统消息。</returns>
    public static ChatMessage CreateSystemMessage(string content) => new()
    {
        Role = "system",
        Content = new[] { new ChatMessageContentPart(content) }
    };

    /// <summary>构造用户消息。</summary>
    /// <param name="content">用户内容。</param>
    /// <returns>用户消息。</returns>
    public static ChatMessage CreateUserMessage(string content) => new()
    {
        Role = "user",
        Content = new[] { new ChatMessageContentPart(content) }
    };

    /// <summary>
    /// 由上一轮模型回复构造助手消息，使工具调用可以回传给模型。
    /// </summary>
    /// <param name="completion">模型回复。</param>
    /// <returns>助手消息。</returns>
    public static ChatMessage CreateAssistantMessage(ChatCompletion completion) => new()
    {
        Role = "assistant",
        Content = completion.Content,
        ToolCalls = completion.ToolCalls
    };

    /// <summary>构造工具结果消息。</summary>
    /// <param name="toolCallId">对应的工具调用 ID。</param>
    /// <param name="content">工具返回文本。</param>
    /// <returns>工具消息。</returns>
    public static ChatMessage CreateToolMessage(string toolCallId, string content) => new()
    {
        Role = "tool",
        ToolCallId = toolCallId,
        Content = new[] { new ChatMessageContentPart(content) }
    };

    /// <summary>
    /// 序列化为 Chat Completions 的消息对象。
    /// </summary>
    /// <returns>可写入请求体的 JsonObject。</returns>
    internal System.Text.Json.Nodes.JsonObject ToJson()
    {
        var node = new System.Text.Json.Nodes.JsonObject { ["role"] = Role };

        if (Role == "tool")
        {
            node["tool_call_id"] = ToolCallId;
            node["content"] = Text;
            return node;
        }

        if (Role == "assistant" && ToolCalls.Count > 0)
        {
            // 官方约定：带有工具调用且没有文本时 content 为 null
            node["content"] = string.IsNullOrEmpty(Text) ? null : Text;
            var calls = new System.Text.Json.Nodes.JsonArray();
            foreach (var call in ToolCalls)
            {
                calls.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["name"] = call.FunctionName,
                        ["arguments"] = call.FunctionArguments?.ToString() ?? "{}"
                    }
                });
            }
            node["tool_calls"] = calls;
            return node;
        }

        node["content"] = Text;
        return node;
    }
}

/// <summary>
/// 模型可调用的函数工具，对应官方 SDK 的 ChatTool。
/// </summary>
public sealed class ChatTool
{
    /// <summary>函数名。</summary>
    internal string Name { get; private init; } = string.Empty;
    /// <summary>函数说明。</summary>
    internal string Description { get; private init; } = string.Empty;
    /// <summary>参数 JSON Schema。</summary>
    internal string ParametersJson { get; private init; } = "{}";

    /// <summary>
    /// 构造一个函数工具。
    /// </summary>
    /// <param name="name">函数名。</param>
    /// <param name="description">函数说明。</param>
    /// <param name="parameters">参数 JSON Schema。</param>
    /// <returns>函数工具。</returns>
    public static ChatTool CreateFunctionTool(string name, string description, BinaryData parameters) => new()
    {
        Name = name,
        Description = description,
        ParametersJson = parameters?.ToString() ?? "{}"
    };
}

/// <summary>
/// 一次完成请求的选项，对应官方 SDK 的 ChatCompletionOptions。
/// </summary>
public sealed class ChatCompletionOptions
{
    /// <summary>本次请求挂载的工具列表。</summary>
    public IList<ChatTool> Tools { get; } = new List<ChatTool>();
}

/// <summary>
/// 模型请求的一次函数调用，对应官方 SDK 的 ChatToolCall。
/// </summary>
public sealed class ChatToolCall
{
    /// <summary>调用类型。</summary>
    public ChatToolCallKind Kind { get; internal init; }
    /// <summary>调用 ID，回传工具结果时使用。</summary>
    public string Id { get; internal init; } = string.Empty;
    /// <summary>被调用的函数名。</summary>
    public string FunctionName { get; internal init; } = string.Empty;
    /// <summary>函数参数 JSON。</summary>
    public BinaryData FunctionArguments { get; internal init; } = BinaryData.FromObjectAsJson(new { });
}

/// <summary>
/// 一次模型完成的结果，对应官方 SDK 的 ChatCompletion。
/// </summary>
public sealed class ChatCompletion
{
    /// <summary>回复文本分片。</summary>
    public IReadOnlyList<ChatMessageContentPart> Content { get; internal init; } = Array.Empty<ChatMessageContentPart>();
    /// <summary>结束原因。</summary>
    public ChatFinishReason FinishReason { get; internal init; }
    /// <summary>模型请求调用的工具列表。</summary>
    public IReadOnlyList<ChatToolCall> ToolCalls { get; internal init; } = Array.Empty<ChatToolCall>();
}

/// <summary>
/// 聊天客户端：向 OpenAI 兼容的 /chat/completions 端点发起请求。
/// </summary>
public sealed class ChatClient
{
    /// <summary>共享的 HTTP 客户端；模型回复可能较慢，超时放宽到 5 分钟。</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>序列化选项：不转义中文。</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>请求地址。</summary>
    private readonly Uri _completionsUri;
    /// <summary>API 密钥。</summary>
    private readonly string _apiKey;
    /// <summary>模型名称。</summary>
    private readonly string _model;

    /// <summary>
    /// 构造聊天客户端。
    /// </summary>
    /// <param name="endpoint">服务基地址。</param>
    /// <param name="apiKey">API 密钥。</param>
    /// <param name="model">模型名称。</param>
    internal ChatClient(Uri endpoint, string apiKey, string model)
    {
        _completionsUri = BuildCompletionsUri(endpoint);
        _apiKey = apiKey;
        _model = model;
    }

    /// <summary>
    /// 由服务基地址推导 /chat/completions 地址；若用户已直接填写完整端点则原样使用。
    /// </summary>
    /// <param name="endpoint">服务基地址。</param>
    /// <returns>补全后的请求地址。</returns>
    private static Uri BuildCompletionsUri(Uri endpoint)
    {
        var text = endpoint.ToString().TrimEnd('/');
        if (text.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return new Uri(text);
        return new Uri(text + "/chat/completions");
    }

    /// <summary>
    /// 发起一次不带工具的完成请求（对话模式）。
    /// </summary>
    /// <param name="messages">对话消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>包装后的完成结果。</returns>
    public Task<ClientResult<ChatCompletion>> CompleteChatAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
        => CompleteChatAsync(messages, options: null, cancellationToken);

    /// <summary>
    /// 发起一次完成请求（可挂载工具，用于智能体模式）。
    /// </summary>
    /// <param name="messages">对话消息。</param>
    /// <param name="options">请求选项，提供工具列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>包装后的完成结果。</returns>
    public async Task<ClientResult<ChatCompletion>> CompleteChatAsync(
        IEnumerable<ChatMessage> messages, ChatCompletionOptions? options, CancellationToken cancellationToken = default)
    {
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = _model
        };

        var messageArray = new System.Text.Json.Nodes.JsonArray();
        foreach (var message in messages) messageArray.Add(message.ToJson());
        payload["messages"] = messageArray;

        // 只有挂载了工具时才发送 tools 字段，对话模式保持请求体最小
        if (options is { Tools.Count: > 0 })
        {
            var toolArray = new System.Text.Json.Nodes.JsonArray();
            foreach (var tool in options.Tools)
            {
                toolArray.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = System.Text.Json.Nodes.JsonNode.Parse(tool.ParametersJson)
                    }
                });
            }
            payload["tools"] = toolArray;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _completionsUri)
        {
            Content = new StringContent(payload.ToJsonString(SerializerOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"AI 服务返回 {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 500)}");

        return new ClientResult<ChatCompletion>(ParseCompletion(body));
    }

    /// <summary>
    /// 解析 Chat Completions 响应体。
    /// </summary>
    /// <param name="body">响应 JSON 文本。</param>
    /// <returns>完成结果。</returns>
    private static ChatCompletion ParseCompletion(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException($"AI 服务未返回任何结果: {Truncate(body, 500)}");

        var choice = choices[0];
        var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;

        var content = Array.Empty<ChatMessageContentPart>();
        var toolCalls = new List<ChatToolCall>();

        if (choice.TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("content", out var contentElement) &&
                contentElement.ValueKind == JsonValueKind.String)
            {
                var text = contentElement.GetString();
                if (!string.IsNullOrEmpty(text))
                    content = new[] { new ChatMessageContentPart(text) };
            }

            if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                CollectToolCalls(calls, toolCalls);
        }

        return new ChatCompletion
        {
            Content = content,
            ToolCalls = toolCalls,
            // 有工具调用时优先按工具调用处理，避免服务端漏填 finish_reason 导致循环提前结束
            FinishReason = toolCalls.Count > 0 ? ChatFinishReason.ToolCalls : MapFinishReason(finishReason)
        };
    }

    /// <summary>
    /// 解析 tool_calls 数组。
    /// </summary>
    /// <param name="calls">响应中的 tool_calls 元素。</param>
    /// <param name="sink">收集结果的目标列表。</param>
    private static void CollectToolCalls(JsonElement calls, List<ChatToolCall> sink)
    {
        foreach (var call in calls.EnumerateArray())
        {
            var id = call.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
            if (!call.TryGetProperty("function", out var function)) continue;

            var name = function.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            var arguments = function.TryGetProperty("arguments", out var argsElement) ? argsElement.GetString() : null;

            sink.Add(new ChatToolCall
            {
                Kind = ChatToolCallKind.Function,
                Id = id,
                FunctionName = name,
                FunctionArguments = BinaryData.FromJson(arguments ?? "{}")
            });
        }
    }

    /// <summary>
    /// 把服务端的 finish_reason 文本映射为枚举。
    /// </summary>
    /// <param name="finishReason">服务端返回的原因文本。</param>
    /// <returns>映射后的枚举值。</returns>
    private static ChatFinishReason MapFinishReason(string? finishReason) => finishReason switch
    {
        "stop" => ChatFinishReason.Stop,
        "tool_calls" => ChatFinishReason.ToolCalls,
        "length" => ChatFinishReason.Length,
        "content_filter" => ChatFinishReason.ContentFilter,
        _ => ChatFinishReason.Stop
    };

    /// <summary>
    /// 截断过长的错误正文，避免日志被整页响应体淹没。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <param name="limit">保留的最大字符数。</param>
    /// <returns>截断后的文本。</returns>
    private static string Truncate(string text, int limit)
        => text.Length <= limit ? text : text[..limit] + "…";
}
