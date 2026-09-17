namespace LocalProjectBridge.Core.Sessions;

/// <summary>连接的四个独立事实；本地或隧道就绪不能替代真实网页调用。</summary>
public sealed record ConnectionReadiness(
    bool LocalReady,
    bool TransportReady,
    bool ClientAuthorized,
    DateTimeOffset? LastVerifiedCall,
    string? ClientIdentity = null)
{
    public static ConnectionReadiness Stopped { get; } = new(false, false, false, null);
}

public sealed record ConnectionReadinessChangedEventArgs(ConnectionReadiness Readiness);

/// <summary>由能分层观察连接状态的适配器实现。</summary>
public interface IConnectionReadinessSource
{
    ConnectionReadiness Readiness { get; }
    event EventHandler<ConnectionReadinessChangedEventArgs>? ReadinessChanged;
}
