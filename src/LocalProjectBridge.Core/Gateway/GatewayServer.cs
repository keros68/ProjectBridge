using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LocalProjectBridge.Core.Gateway;

/// <summary>
/// 统一 MCP 网关本地监听器（设计文档 5.1）：只绑定 127.0.0.1 动态端口，
/// 公网暴露交给隧道组件。每次会话创建一个实例，会话结束即停止。
/// 使用 TcpListener 直接应答 HTTP，避免 http.sys 的 URL ACL 授权要求。
/// </summary>
public sealed class GatewayServer : IAsyncDisposable
{
    private readonly McpDispatcher _dispatcher;
    private readonly RedactingLogger _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private readonly string? _accessKey;
    private readonly string _requestPath;
    private readonly bool _trustAllRequestsAsRemote;
    private readonly string _trustedRemoteIdentity;
    private readonly HttpClient _healthClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(4) };

    public GatewayServer(
        McpDispatcher dispatcher,
        RedactingLogger logger,
        string? accessKey = null,
        string requestPath = "/mcp",
        bool trustAllRequestsAsRemote = false,
        string trustedRemoteIdentity = "secure-tunnel-connection")
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _accessKey = accessKey;
        _requestPath = NormalizeRequestPath(requestPath);
        _trustAllRequestsAsRemote = trustAllRequestsAsRemote;
        _trustedRemoteIdentity = trustedRemoteIdentity;
    }

    public event EventHandler<VerifiedRemoteCallEventArgs>? VerifiedRemoteCall;

    /// <summary>本机监听地址；隧道应把公网入口转发到这里。</summary>
    public string? ListenUrl { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null) return Task.CompletedTask;
        var port = Processes.TcpPortAllocator.GetFreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        _cancellation = new CancellationTokenSource();
        ListenUrl = $"http://127.0.0.1:{port}{_requestPath}";
        _ = AcceptLoopAsync(listener, _cancellation.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                using (stream)
                {
                    client.ReceiveTimeout = 15_000;
                    client.SendTimeout = 15_000;
                    var request = await ReadHttpRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (request is null)
                    {
                        await WriteResponseAsync(stream, 400, """{"error":"请求格式无效。"}""", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (request.Value.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                        && IsProtectedResourceMetadataPath(request.Value.Path))
                    {
                        // ProjectBridge does not expose an OAuth authorization server. Returning metadata here
                        // makes clients start an account-link flow that can never complete. A 404 explicitly
                        // advertises the supported no-OAuth MCP mode to Secure Tunnel discovery.
                        await WriteResponseAsync(stream, 404, "{\"error\":\"not_found\"}", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (_accessKey is not null && request.Value.Authorization != "Bearer " + _accessKey)
                    {
                        await WriteResponseAsync(stream, 401, "{\"error\":\"unauthorized\"}", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (_accessKey is not null && request.Value.Method == "POST" && request.Value.Path == "/probe")
                    {
                        var result = await ProbePublicHealthAsync(request.Value.Body, cancellationToken).ConfigureAwait(false);
                        await WriteResponseAsync(stream, 200, result, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (!request.Value.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                        || request.Value.Path != _requestPath)
                    {
                        await WriteResponseAsync(stream, 405, """{"error":"仅支持 POST /mcp。"}""", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    var trustedRemote = _trustAllRequestsAsRemote || request.Value.RemoteSource == "c2c-oauth";
                    // Fixed Tunnel identity comes from local configuration, never request headers.
                    var identity = _trustAllRequestsAsRemote ? _trustedRemoteIdentity
                        : trustedRemote ? string.IsNullOrWhiteSpace(request.Value.RemoteClient)
                            ? "c2c-oauth-client" : request.Value.RemoteClient
                        : null;
                    using var requestContext = GatewayRequestContext.Push(identity);
                    var response = await _dispatcher.HandleAsync(request.Value.Body, cancellationToken).ConfigureAwait(false);
                    if (response is null)
                    {
                        await WriteResponseAsync(stream, 202, string.Empty, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (IsSuccessfulToolCall(request.Value.Body, response)
                        && trustedRemote)
                    {
                        VerifiedRemoteCall?.Invoke(this, new VerifiedRemoteCallEventArgs(DateTimeOffset.Now, identity!));
                    }
                    await WriteResponseAsync(stream, 200, response, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error)
        {
            await _logger.WriteAsync("error", $"gateway request failed: {error.Message}").ConfigureAwait(false);
        }
    }

    private async Task<string> ProbePublicHealthAsync(string body, CancellationToken cancellationToken)
    {
        string? detail = null;
        try
        {
            using var input = System.Text.Json.JsonDocument.Parse(body);
            var url = new Uri(input.RootElement.GetProperty("url").GetString()!);
            if (url.Scheme != "https" || !url.IsDefaultPort || !url.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase)
                || url.AbsolutePath != "/health" || url.UserInfo.Length > 0 || url.Query.Length > 0)
                throw new InvalidOperationException("Unexpected health probe address");
            using var response = await _healthClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) detail = $"Public health returned HTTP {(int)response.StatusCode}";
            else
            {
                using var health = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                if (health.RootElement.GetProperty("service").GetString() == "c2c-bridge" && health.RootElement.GetProperty("status").GetString() == "ok")
                    return "{\"ready\":true}";
                detail = "Public endpoint did not identify the bridge";
            }
        }
        catch (Exception error) { detail = error.GetType().Name + ": " + error.Message; }
        return System.Text.Json.JsonSerializer.Serialize(new { ready = false, detail });
    }

    private static async Task<(string Method, string Path, string Body, string? Authorization, string? RemoteSource, string? RemoteClient)?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var head = new StringBuilder();
        var buffer = new byte[1];
        // 逐字节读取到 \r\n\r\n，本机小请求足够；避免引入额外解析依赖
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            head.Append((char)buffer[0]);
            if (head.Length >= 4 && head.ToString(head.Length - 4, 4) == "\r\n\r\n") break;
            if (head.Length > 32 * 1024) return null;
        }
        var headText = head.ToString();
        var lines = headText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;
        var contentLength = 0;
        string? authorization = null;
        string? remoteSource = null;
        string? remoteClient = null;
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            if (line[..separator].Trim().Equals("Authorization", StringComparison.OrdinalIgnoreCase)) authorization = line[(separator + 1)..].Trim();
            if (line[..separator].Trim().Equals("X-ProjectBridge-Remote", StringComparison.OrdinalIgnoreCase)) remoteSource = line[(separator + 1)..].Trim();
            if (line[..separator].Trim().Equals("X-ProjectBridge-Client", StringComparison.OrdinalIgnoreCase)) remoteClient = line[(separator + 1)..].Trim();
            if (line[..separator].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[(separator + 1)..].Trim(), out var length))
            {
                if (length < 0 || length > 4 * 1024 * 1024) return null;
                contentLength = length;
            }
        }
        var bodyBuffer = new byte[contentLength];
        var total = 0;
        while (total < contentLength)
        {
            var read = await stream.ReadAsync(bodyBuffer.AsMemory(total, contentLength - total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        if (total != contentLength) return null;
        return (requestLine[0], requestLine[1], Encoding.UTF8.GetString(bodyBuffer, 0, total), authorization, remoteSource, remoteClient);
    }

    private static string NormalizeRequestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Contains('?') || path.Contains('#'))
            throw new ArgumentException("本机网关路径无效。", nameof(path));
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private bool IsProtectedResourceMetadataPath(string path)
    {
        const string metadataRoot = "/.well-known/oauth-protected-resource";
        return path.Equals(metadataRoot, StringComparison.Ordinal)
               || path.Equals(metadataRoot + _requestPath, StringComparison.Ordinal);
    }

    private static bool IsSuccessfulToolCall(string requestBody, string responseBody)
    {
        try
        {
            using var request = System.Text.Json.JsonDocument.Parse(requestBody);
            if (!request.RootElement.TryGetProperty("method", out var method) || method.GetString() != "tools/call") return false;
            using var response = System.Text.Json.JsonDocument.Parse(responseBody);
            if (!response.RootElement.TryGetProperty("result", out var result)) return false;
            return !result.TryGetProperty("isError", out var isError) || isError.ValueKind != System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string body, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var status = statusCode switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Internal Server Error"
        };
        var header =
            $"HTTP/1.1 {statusCode} {status}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > 0)
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        if (_listener is not null)
        {
            try { _listener.Stop(); }
            catch (ObjectDisposedException) { }
            _listener = null;
        }
        ListenUrl = null;
        return ValueTask.CompletedTask;
    }
}

public sealed record VerifiedRemoteCallEventArgs(DateTimeOffset At, string ClientIdentity);
