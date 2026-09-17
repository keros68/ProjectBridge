namespace LocalProjectBridge.Core.Sessions;

/// <summary>
/// 会话适配器契约（设计文档 6.7）：适配器只做协议转换，不保存第二份项目权限。
/// 生命周期完全由 SessionController 驱动。
/// </summary>
public interface IConnectableAdapter : IAsyncDisposable
{
    string Name { get; }

    /// <summary>该适配器在给定能力组合下是否必需。</summary>
    bool IsRequiredFor(CapabilityFlags capabilities);

    /// <summary>启动前的运行组件检查（设计文档 6.4 连接顺序第 2 步），失败时抛出可分类的异常。</summary>
    Task AssertReadyAsync(SessionPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>启动组件。只允许依据 policy 工作。</summary>
    Task StartAsync(SessionPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>MCP 初始化探测与项目身份核对（设计文档 6.4 连接顺序第 4-5 步）。返回展示给用户的状态摘要。</summary>
    Task<string> VerifyAsync(SessionPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>停止并撤销本适配器持有的资源（令牌、配对、子进程）。必须幂等。</summary>
    Task StopAsync(SessionPolicy policy);

    /// <summary>组件监管发现的最终故障（重试放弃后触发）。</summary>
    event EventHandler<string>? Faulted;
}

/// <summary>供测试与未来统一网关使用的适配器基类，处理能力门控。</summary>
public abstract class ConnectableAdapterBase : IConnectableAdapter
{
    public abstract string Name { get; }
    public abstract bool IsRequiredFor(CapabilityFlags capabilities);
    public abstract Task AssertReadyAsync(SessionPolicy policy, CancellationToken cancellationToken = default);
    public abstract Task StartAsync(SessionPolicy policy, CancellationToken cancellationToken = default);
    public abstract Task<string> VerifyAsync(SessionPolicy policy, CancellationToken cancellationToken = default);
    public abstract Task StopAsync(SessionPolicy policy);
    public abstract ValueTask DisposeAsync();
    public event EventHandler<string>? Faulted;

    protected void RaiseFaulted(string message) => Faulted?.Invoke(this, message);
}
