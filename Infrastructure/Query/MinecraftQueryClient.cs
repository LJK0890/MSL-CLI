using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MSL_CLI.Infrastructure.Query;

// ---------------------------------------------------------------------------
// MSL-CLI 内置的 Minecraft Query（GameSpy4 / UT3 查询协议）客户端。
//
// 为什么不用 McQuery.Net：MSL-CLI 只需要“取一次完整状态”这一项能力，不值得为它
// 引入外部包。这里直接按协议实现，类型名与被替换的包保持一致（McQueryClientFactory /
// McQueryClient / GetFullStatusAsync），使 ServerManager 的实现无需改动。
//
// 协议两步（均为 UDP，服务器需在 server.properties 中 enable-query=true）：
//   1. 握手：FE FD 09 + 4 字节会话号
//        ->  09 + 会话号回显(4~N 字节，服务端按 UTF-8 字符串回写) + 十进制 challenge + 00
//   2. 查询：FE FD 00 + 4 字节会话号 + 4 字节 challenge(大端) + 4 字节 0（共 15 字节）
//        ->  00 + 会话号 + "splitnum\0\x80\0" + KV 段 + 玩家段
//
// 两个容易踩的坑（均由服务端 QueryThreadGs4 强制校验）：
//   * challenge 必须原样回填到查询报文的第 7~10 字节，填 0 会被判为 Invalid challenge
//     直接丢弃，客户端只能等到超时；
//   * 查询报文长度必须恰好为 15 字节，否则服务端只回基础状态（rules，无版本/玩家段）。
// ---------------------------------------------------------------------------

/// <summary>
/// 一次完整查询的结果，字段与被替换的 McQuery.Net 结果对象保持一致。
/// </summary>
public sealed class McQueryFullStatus
{
    /// <summary>服务器标语（MOTD）。</summary>
    public string? Motd { get; init; }
    /// <summary>服务端版本，例如 "1.21.1"。</summary>
    public string? Version { get; init; }
    /// <summary>游戏类型，例如 SMP。</summary>
    public string? GameType { get; init; }
    /// <summary>地图名称。</summary>
    public string? Map { get; init; }
    /// <summary>在线玩家数。</summary>
    public int NumPlayers { get; init; }
    /// <summary>最大玩家数。</summary>
    public int MaxPlayers { get; init; }
    /// <summary>服务端监听端口。</summary>
    public int HostPort { get; init; }
    /// <summary>服务端汇报的主机地址；未启用该字段时为空。</summary>
    public string? HostIp { get; init; }
    /// <summary>在线玩家名列表。</summary>
    public IReadOnlyList<string> PlayerList { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 查询客户端工厂，与被替换包的工厂同名，调用方无需改变创建方式。
/// </summary>
public sealed class McQueryClientFactory
{
    /// <summary>
    /// 创建一个查询客户端。调用方应使用 using 释放。
    /// </summary>
    /// <returns>查询客户端。</returns>
    public McQueryClient Get() => new();
}

/// <summary>
/// Minecraft Query 客户端：向指定端点发起一次完整状态查询。
/// </summary>
public sealed class McQueryClient : IDisposable
{
    /// <summary>单次收包等待时间。</summary>
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 释放资源；当前实现不持有长生命周期资源，保留以满足 using 语义。
    /// </summary>
    public void Dispose()
    {
        // 无长生命周期资源需要释放：UDP 套接字由每次查询自行创建并释放
    }

    /// <summary>
    /// 查询服务器的完整状态。
    /// </summary>
    /// <param name="endpoint">服务器查询端点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>查询结果；服务器未响应时抛出异常，由调用方记录并处理。</returns>
    public async Task<McQueryFullStatus> GetFullStatusAsync(
        IPEndPoint endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        // 会话号同时用于握手与查询；服务端会把收到的 4 字节当作 UTF-8 字符串回显，
        // 因此这里生成“可打印且非数字”的字节，保证回显长度固定为 4 且不会与
        // 紧随其后的十进制 challenge 粘连
        int sessionId = CreateSessionId();

        using var udp = new UdpClient(endpoint.AddressFamily);
        udp.Connect(endpoint);

        await SendAsync(udp, BuildHandshake(sessionId), cancellationToken);
        var handshake = await ReceiveAsync(udp, cancellationToken);
        if (handshake.Length < 1 || handshake[0] != 0x09)
            throw new InvalidDataException("Minecraft Query 握手响应无效");

        // 服务端在握手响应中下发一次性 challenge，查询报文必须原样回填，
        // 否则服务端会按 Invalid challenge 丢弃请求（表现为一直收不到响应直到超时）
        int challenge = ParseChallenge(handshake);

        await SendAsync(udp, BuildStatRequest(sessionId, challenge), cancellationToken);
        var response = await ReceiveAsync(udp, cancellationToken);
        if (response.Length < 1 || response[0] != 0x00)
            throw new InvalidDataException("Minecraft Query 查询响应无效");

        return ParseFullStatus(response);
    }

    /// <summary>
    /// 生成握手会话号：4 个字节全部取“可打印且非数字”的 ASCII 字符。
    /// 取值受限的原因见 <see cref="GetFullStatusAsync"/> 与 <see cref="ParseChallenge"/>：
    /// 服务端把会话号按 UTF-8 字符串回显，字节非法时会替换成 U+FFFD 而使回显变长；
    /// 若回显的最后一个字节恰好是数字，从末尾向前解析 challenge 时就会把它一并读进去。
    /// </summary>
    /// <returns>会话号（按大端序解释这 4 个字节）。</returns>
    private static int CreateSessionId()
    {
        // 0x21~0x2F 与 0x3A~0x7E 共 15 + 69 = 84 个候选值
        const int Range = 84;
        Span<byte> bytes = stackalloc byte[4];
        for (int i = 0; i < bytes.Length; i++)
        {
            int value = Random.Shared.Next(Range);
            bytes[i] = (byte)(value < 15 ? 0x21 + value : 0x3A + (value - 15));
        }

        return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    }

    /// <summary>
    /// 从握手响应中解析服务端下发的 challenge。
    /// 响应格式为 <c>09</c> + 会话号回显 + 十进制 challenge + <c>\0</c>；
    /// 会话号回显是服务端把收到的 4 字节按 UTF-8 字符串回写的结果，长度不固定
    /// （含非法字节时会被替换成 U+FFFD 而变长），因此这里从末尾向前读取数字段。
    /// </summary>
    /// <param name="handshake">握手响应报文。</param>
    /// <returns>挑战值。</returns>
    private static int ParseChallenge(byte[] handshake)
    {
        int end = handshake.Length - 1;
        // 跳过结尾的 \0 分隔符
        while (end >= 0 && handshake[end] == 0) end--;

        int start = end;
        while (start >= 0 && handshake[start] is >= (byte)'0' and <= (byte)'9') start--;

        int length = end - start;
        if (length <= 0)
            throw new InvalidDataException("Minecraft Query 握手响应中缺少 challenge");

        if (!int.TryParse(Encoding.ASCII.GetString(handshake, start + 1, length),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var challenge))
            throw new InvalidDataException("Minecraft Query 握手响应中的 challenge 无法解析");

        return challenge;
    }

    /// <summary>
    /// 构造握手报文：FE FD 09 + 4 字节会话号（大端）。
    /// </summary>
    /// <param name="sessionId">会话号。</param>
    /// <returns>报文内容。</returns>
    private static byte[] BuildHandshake(int sessionId)
    {
        var buffer = new byte[7];
        buffer[0] = 0xFE;
        buffer[1] = 0xFD;
        buffer[2] = 0x09;
        WriteInt32BigEndian(buffer, 3, sessionId);
        return buffer;
    }

    /// <summary>
    /// 构造完整状态查询报文：FE FD 00 + 4 字节会话号（大端）+ 4 字节 challenge（大端）
    /// + 4 字节零填充，共 15 字节。
    /// 长度必须恰好为 15，否则服务端只返回基础状态（rules）而不含版本与玩家段。
    /// </summary>
    /// <param name="sessionId">会话号。</param>
    /// <param name="challenge">握手阶段下发的挑战值。</param>
    /// <returns>报文内容。</returns>
    private static byte[] BuildStatRequest(int sessionId, int challenge)
    {
        var buffer = new byte[15];
        buffer[0] = 0xFE;
        buffer[1] = 0xFD;
        buffer[2] = 0x00;
        WriteInt32BigEndian(buffer, 3, sessionId);
        WriteInt32BigEndian(buffer, 7, challenge);
        // 末尾 4 字节保持为 0（填充，用于把长度凑到 15）
        return buffer;
    }

    /// <summary>
    /// 以大端序写入 4 字节整数。
    /// </summary>
    /// <param name="buffer">目标缓冲区。</param>
    /// <param name="offset">写入位置。</param>
    /// <param name="value">写入的值。</param>
    private static void WriteInt32BigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    /// <summary>
    /// 发送一个 UDP 报文。
    /// </summary>
    /// <param name="udp">已连接的 UDP 客户端。</param>
    /// <param name="payload">报文内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task SendAsync(UdpClient udp, byte[] payload, CancellationToken cancellationToken)
        => await udp.SendAsync(payload.AsMemory(), cancellationToken);

    /// <summary>
    /// 接收一个 UDP 报文，超时视为服务器不可达。
    /// </summary>
    /// <param name="udp">已连接的 UDP 客户端。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>收到的报文内容。</returns>
    private static async Task<byte[]> ReceiveAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ReceiveTimeout);
        try
        {
            var result = await udp.ReceiveAsync(timeoutSource.Token);
            return result.Buffer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 超时（而非调用方取消）时给出明确原因，便于上层日志区分
            throw new TimeoutException("Minecraft Query 未收到响应（超时）");
        }
    }

    /// <summary>
    /// 解析完整查询响应：跳过头部后读取键值段与玩家列表。
    /// </summary>
    /// <param name="response">响应报文。</param>
    /// <returns>解析结果。</returns>
    private static McQueryFullStatus ParseFullStatus(byte[] response)
    {
        // 头部：类型(1) + 会话号(4) + "splitnum\0\x80\0"(11) = 16 字节
        int offset = 16;
        if (response.Length < offset)
            throw new InvalidDataException("Minecraft Query 响应长度不足");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 键值段：反复读取 "key\0value\0"，遇到空键结束
        while (true)
        {
            var key = ReadNullTerminatedString(response, ref offset);
            if (key.Length == 0) break;

            var value = ReadNullTerminatedString(response, ref offset);
            values[key] = value;
        }

        // 玩家段：可选的 0x01 标记 + "player_\0" + 一个空串 + 玩家名（各自以 \0 结尾）+ 结尾空串。
        // 原版在这里会多写一个 0x00（即 "player_\0\0"），若把紧随其后的空串当作结束标记，
        // 玩家列表会永远为空；因此改为读到报文末尾并只收集非空项，兼容省略该填充的实现。
        if (offset < response.Length && response[offset] == 0x01) offset++;
        var players = new List<string>();
        if (offset < response.Length)
        {
            var header = ReadNullTerminatedString(response, ref offset);
            if (header == "player_")
            {
                while (offset < response.Length)
                {
                    var player = ReadNullTerminatedString(response, ref offset);
                    if (player.Length > 0) players.Add(player);
                }
            }
        }

        return new McQueryFullStatus
        {
            // 原版服务端把 MOTD 放在 hostname 键下（并非 motd），保留 motd 作为回退，
            // 以兼容部分代理/插件实现
            Motd = GetValue(values, "hostname") ?? GetValue(values, "motd"),
            Version = GetValue(values, "version"),
            GameType = GetValue(values, "gametype"),
            Map = GetValue(values, "map"),
            NumPlayers = ParseInt(GetValue(values, "numplayers")),
            MaxPlayers = ParseInt(GetValue(values, "maxplayers")),
            HostPort = ParseInt(GetValue(values, "hostport")),
            HostIp = GetValue(values, "hostip"),
            PlayerList = players
        };
    }

    /// <summary>
    /// 从字典取值，缺失时返回 null。
    /// </summary>
    /// <param name="values">键值字典。</param>
    /// <param name="key">键名。</param>
    /// <returns>对应的值。</returns>
    private static string? GetValue(Dictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// 解析整数，失败时返回 0。
    /// </summary>
    /// <param name="text">待解析文本。</param>
    /// <returns>解析结果。</returns>
    private static int ParseInt(string? text)
        => int.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>
    /// 从当前位置读取一个以 \0 结尾的 UTF-8 字符串，并把位置推进到分隔符之后。
    /// </summary>
    /// <param name="buffer">响应缓冲区。</param>
    /// <param name="offset">读取位置，读取后被推进。</param>
    /// <returns>读取到的字符串；到达缓冲区末尾时返回空串。</returns>
    private static string ReadNullTerminatedString(byte[] buffer, ref int offset)
    {
        int start = offset;
        while (offset < buffer.Length && buffer[offset] != 0) offset++;

        int length = offset - start;
        // 跳过作为分隔符的 \0
        if (offset < buffer.Length) offset++;

        return length <= 0 ? string.Empty : Encoding.UTF8.GetString(buffer, start, length);
    }
}
