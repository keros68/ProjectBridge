namespace LocalProjectBridge.Core.Sessions;

/// <summary>会话状态机（设计文档 9.1）。状态只能按允许的顺序迁移。</summary>
public enum SessionState
{
    Disconnected,
    Checking,
    Starting,
    Authorizing,
    Connected,
    Recovering,
    NeedsAttention,
    Disconnecting
}

public static class SessionStateTransitions
{
    private static readonly Dictionary<SessionState, SessionState[]> Allowed = new()
    {
        [SessionState.Disconnected] = [SessionState.Checking],
        [SessionState.Checking] = [SessionState.Starting, SessionState.NeedsAttention, SessionState.Disconnected],
        [SessionState.Starting] = [SessionState.Authorizing, SessionState.Connected, SessionState.NeedsAttention, SessionState.Disconnecting],
        [SessionState.Authorizing] = [SessionState.Connected, SessionState.NeedsAttention, SessionState.Disconnecting],
        [SessionState.Connected] = [SessionState.Recovering, SessionState.NeedsAttention, SessionState.Disconnecting],
        [SessionState.Recovering] = [SessionState.Connected, SessionState.NeedsAttention, SessionState.Disconnecting],
        [SessionState.NeedsAttention] = [SessionState.Checking, SessionState.Disconnecting, SessionState.Disconnected],
        [SessionState.Disconnecting] = [SessionState.Disconnected, SessionState.NeedsAttention]
    };

    public static bool CanTransition(SessionState from, SessionState to)
        => from == to || (Allowed.TryGetValue(from, out var targets) && targets.Contains(to));
}
