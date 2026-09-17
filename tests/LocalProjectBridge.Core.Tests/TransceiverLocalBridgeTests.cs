using System.Net.Http.Json;
using LocalProjectBridge.Core.Adapters;
using LocalProjectBridge.Core.Sessions;

namespace LocalProjectBridge.Core.Tests;

public sealed class TransceiverLocalBridgeTests
{
    [Fact]
    public async Task LocalTaskBridge_StartsWithoutAdminTunnelAndClosesPort()
    {
        var root = Directory.CreateTempSubdirectory("lpb-task-local-").FullName;
        var project = new ProjectRecord { Name = "local test", Path = root, AllowCodexTasks = true };
        var policy = SessionPolicyFactory.Create(project, CapabilityFlags.WebDelegateCodex);
        await using var bridge = new TransceiverAdapter(root, new RedactingLogger(root), manageTunnel:false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await bridge.AssertReadyAsync(policy, timeout.Token);
            await bridge.StartAsync(policy, timeout.Token);
            await bridge.VerifyAsync(policy, timeout.Token);
            var url = new Uri(bridge.BridgeMcpUrl!);
            using var client = new HttpClient(new HttpClientHandler { UseProxy=false });
            using var message = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new {jsonrpc="2.0",id=1,method="tools/list"})
            };
            message.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            using var response = await client.SendAsync(message, timeout.Token);
            response.EnsureSuccessStatusCode();
            Assert.Contains("codex_task", await response.Content.ReadAsStringAsync(timeout.Token));
            await bridge.StopAsync(policy);
            using var socket = new System.Net.Sockets.TcpClient();
            await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(()=>socket.ConnectAsync(url.Host,url.Port));
        }
        finally { await bridge.DisposeAsync(); Directory.Delete(root,true); }
    }
}
