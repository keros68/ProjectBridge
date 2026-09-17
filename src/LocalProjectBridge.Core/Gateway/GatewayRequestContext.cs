namespace LocalProjectBridge.Core.Gateway;

/// <summary>Trusted connection identity supplied by the local gateway, never by tool arguments.</summary>
public static class GatewayRequestContext
{
    private static readonly AsyncLocal<string?> CurrentClient = new();
    public static string? ClientIdentity => CurrentClient.Value;

    public static IDisposable Push(string? clientIdentity)
    {
        var previous = CurrentClient.Value;
        CurrentClient.Value = clientIdentity;
        return new Scope(() => CurrentClient.Value = previous);
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
