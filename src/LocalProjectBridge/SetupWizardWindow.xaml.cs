using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Navigation;
using LocalProjectBridge.Core.Processes;
using LocalProjectBridge.Core.Security;

namespace LocalProjectBridge;

public partial class SetupWizardWindow : Window
{
    private const string ChatGptDeveloperModeUrl = WebConnectionGuide.DeveloperSettingsUrl;
    private const string ChatGptConnectorUrl = WebConnectionGuide.PluginsUrl;
    private readonly RegistryStore _store;
    private readonly BackendInstallerService _installer;
    private readonly C2cAdapter _c2c;
    private readonly AppSettings _settings;
    private readonly ConnectionProfile _connectionProfile;
    private readonly IRuntimeCredentialStore _credentialStore;
    private readonly Func<ConnectionProfile, CancellationToken, Task> _completeConnection;
    private readonly Func<ConnectionProfile, CancellationToken, Task> _completeStableConnection;
    public bool ConnectionTransferred { get; private set; }
    public ConnectionProfile? SavedConnectionProfile { get; private set; }
    public Action<Window>? OpenPluginGuide { get; set; }
    private readonly bool _configurationOnly;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private int _step = 1;
    private bool _setupStartAttempted;
    private bool _closing;
    private bool _closeAllowed;
    private bool _discardRequested;
    private bool _hasSavedRuntimeKey;
    private string? _activeProjectPath;
    private Task? _activeOperation;
    private Task<bool>? _closeCleanup;
    private readonly System.Windows.Threading.DispatcherTimer _authorizationTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public SetupWizardWindow(
        RegistryStore store,
        BackendInstallerService installer,
        C2cAdapter c2c,
        AppSettings settings,
        Func<ConnectionProfile, CancellationToken, Task> completeConnection)
        : this(
            store,
            installer,
            c2c,
            settings,
            new ConnectionProfile(),
            completeConnection,
            (_, _) => throw new InvalidOperationException("此调用方未提供 OpenAI Secure Tunnel 设置流程。"),
            false)
    {
    }

    public SetupWizardWindow(RegistryStore store, BackendInstallerService installer, C2cAdapter c2c, AppSettings settings,
        ConnectionProfile connectionProfile,
        Func<ConnectionProfile, CancellationToken, Task> completeConnection,
        Func<ConnectionProfile, CancellationToken, Task> completeStableConnection,
        bool configurationOnly = false,
        IRuntimeCredentialStore? credentialStore = null)
    {
        InitializeComponent();
        _store = store;
        _installer = installer;
        _c2c = c2c;
        _settings = settings;
        _connectionProfile = connectionProfile;
        _credentialStore = credentialStore ?? new WindowsRuntimeCredentialStore();
        _completeConnection = completeConnection;
        _completeStableConnection = completeStableConnection;
        _configurationOnly = configurationOnly;
        SecureProvider.IsChecked = connectionProfile.Provider == TunnelProvider.OpenAiSecureTunnel;
        QuickProvider.IsChecked = connectionProfile.Provider != TunnelProvider.OpenAiSecureTunnel;
        TunnelId.Text = connectionProfile.TunnelId ?? string.Empty;
        RuntimeKeyReference.Text = IsAdvancedCredentialReference(connectionProfile.RuntimeCredentialReference)
            ? connectionProfile.RuntimeCredentialReference
            : string.Empty;
        UpdateRuntimeKeyStatus();
        SecureTunnelPanel.Visibility = SecureProvider.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (_configurationOnly)
        {
            Title = "连接设置";
            WizardTitle.Text = "共享连接设置";
            ProjectConnectionHelp.Text = "所有项目共用此连接，项目权限在主窗口分别管理。";
            StepIndicator.Text = "保存后将在下次连接时生效";
            NextButton.Content = "关闭";
            LaterButton.Visibility = Visibility.Collapsed;
        }
        Closing += SetupWizardWindow_Closing;
        _authorizationTimer.Tick += AuthorizationTimer_Tick;
    }

    private void OpenPluginGuide_Click(object sender, RoutedEventArgs e)
    {
        if (OpenPluginGuide is not null) { OpenPluginGuide(this); return; }
        var guide = new WebConnectionGuide();
        var profile = ConnectionProfileConfiguration.Clone(_connectionProfile);
        profile.Provider = SecureProvider.IsChecked == true ? TunnelProvider.OpenAiSecureTunnel : TunnelProvider.CloudflareQuickTunnel;
        profile.TunnelId = TunnelId.Text.Trim();
        guide.Configure(profile, null, false, false);
        guide.CreateHelpWindow(this).ShowDialog();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        WizardError.Text = string.Empty;
        if (_configurationOnly)
        {
            Close();
            return;
        }
        var requestedStep = _step;
        try
        {
            if (_step == 1) await RunOperationAsync(PrepareAsync);
            else if (_step == 2) await RunOperationAsync(CompleteAsync);
            else Close();
        }
        catch (Exception error)
        {
            if (_closing) return;
            WizardError.Text = error is OperationCanceledException ? "连接操作超时，请检查网络后重试。" : error.Message;
            ShowStep(requestedStep);
        }
    }

    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        EnsureDisplayedConfigurationIsSaved();
        if (_connectionProfile.Provider == TunnelProvider.OpenAiSecureTunnel)
        {
            await new SecureTunnelRuntime(new CommandRunner(new RedactingLogger(_store.AppDataDirectory)))
                .AssertRuntimeCredentialReadyAsync(_connectionProfile);
            ShowStep(0);
            NextButton.IsEnabled = false;
            LaterButton.IsEnabled = false;
            try
            {
                InstallMessage.Text = "正在启动本地网关与 OpenAI Secure Tunnel…";
                await _completeStableConnection(_connectionProfile, cancellationToken);
                ConnectionTransferred = true;
                _settings.SetupCompleted = true;
                _settings.ConnectionVersion = 3;
                await _store.SaveAsync(_settings);
                SuccessTitle.Text = "固定通道已就绪";
                SuccessDescription.Text = "本地服务与 OpenAI Secure Tunnel 已连接。此入口无需 OAuth 账号授权；请在 ChatGPT 中选择此 Tunnel，并执行一次项目读取以完成网页验证。";
                SuccessExitNote.Text = "关闭此窗口不会停止连接；点击“断开”或退出程序会回收本程序启动的连接进程。";
                ShowStep(3);
                return;
            }
            finally
            {
                if (!_closing)
                {
                    NextButton.IsEnabled = true;
                    LaterButton.IsEnabled = true;
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        ShowStep(0);
        NextButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(message => { if (!_closing) InstallMessage.Text = message; });
            await _installer.InstallOrUpdateAsync(progress, cancellationToken);
            var choice = await _c2c.GetTunnelChoiceAsync(_c2c.ConnectionWorkspace, cancellationToken);
            if (choice.NeedsChoice)
                await _c2c.ChooseConnectionAsync(_c2c.ConnectionWorkspace, fixedDomain: false, domain: null, cancellationToken);
            var result = await StartSetupConnectionAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ConnectorName.Text = result.ConnectorName;
            ConnectorUrl.Text = result.McpUrl;
            SetupWebGuide.Configure(_connectionProfile, result.McpUrl, true, false,
                () => _c2c.CreatePairingCodeAsync(_c2c.ConnectionWorkspace));
            SetupWebGuide.ShowPluginSetupOnly();
            ShowStep(2);
        }
        finally
        {
            if (!_closing)
            {
                NextButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
            }
        }
    }

    private async Task CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ConnectionTransferred) return;
        if (_activeProjectPath is null || !await _c2c.HasAuthorizationAsync(_activeProjectPath, cancellationToken))
            throw new InvalidOperationException("尚未收到网页授权。请在 ChatGPT 中完成 OAuth 配对，程序会自动确认。");
        await _completeConnection(_connectionProfile, cancellationToken);
        // Ownership has moved to the main session. Closing the wizard must no longer stop it,
        // including if close/cancellation arrived while verification was finishing.
        ConnectionTransferred = true;
        _authorizationTimer.Stop();
        _setupStartAttempted = false;
        _settings.SetupCompleted = true;
        if (_c2c.IsSharedConnection) _settings.ConnectionVersion = 2;
        try { await _store.SaveAsync(_settings); }
        catch (Exception) { WizardError.Text = "连接已保留，但设置保存失败。下次启动可能需要重新设置。"; }
        ShowStep(3);
    }

    private async void AuthorizationTimer_Tick(object? sender, EventArgs e)
    {
        if (_closing || _step != 2 || _activeOperation is { IsCompleted: false }) return;
        try
        {
            await RunOperationAsync(async token =>
            {
                if (_activeProjectPath is not null && await _c2c.HasAuthorizationAsync(_activeProjectPath, token))
                {
                    WizardError.Text = string.Empty;
                    await CompleteAsync(token);
                }
            });
        }
        catch (Exception error)
        {
            if (!_closing) WizardError.Text = $"自动确认暂未完成：{error.Message}";
        }
    }

    private void ShowStep(int step)
    {
        if (step == 3)
        {
            var secure = _connectionProfile.Provider == TunnelProvider.OpenAiSecureTunnel;
            SuccessWebGuide.Configure(_connectionProfile, _c2c.ObservedMcpUrl, true,
                !secure && _c2c.Readiness.LastVerifiedCall is not null,
                secure ? null : () => _c2c.CreatePairingCodeAsync(_c2c.ConnectionWorkspace));
            SuccessWebGuide.Visibility = secure ? Visibility.Visible : Visibility.Collapsed;
            if (secure) SuccessWebGuide.ShowPluginSetupOnly();
        }
        _step = step;
        if (step == 2 && !_closing) _authorizationTimer.Start();
        else _authorizationTimer.Stop();
        if (step == 2) Height = Math.Max(Height, 680);
        ProjectPanel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PreparingPanel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConnectPanel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        SuccessPanel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepIndicator.Text = step switch
        {
            0 => "正在准备连接",
            2 => "连接 ChatGPT",
            3 => "共享连接已建立 · 下一步回首页添加项目并验证",
            _ => "选择连接方式"
        };
        NextButton.Content = step switch
        {
            2 => "检查授权",
            3 => "关闭",
            _ => "准备连接"
        };
        LaterButton.Visibility = step == 3 ? Visibility.Collapsed : Visibility.Visible;
        CancelConnectionButton.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        LaterButton.Content = step == 2 ? "关闭设置" : "稍后";
    }

    private void OpenChatGpt_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(ChatGptConnectorUrl) { UseShellExecute = true });
        System.Windows.MessageBox.Show(this,
            "在 ChatGPT 页面新建连接：\n\n1. 名称：回到本窗口复制\n2. 服务器地址：回到本窗口复制\n3. 认证方式：选择 OAuth\n4. 点击连接或授权",
            "添加连接",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenDeveloperMode_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(ChatGptDeveloperModeUrl) { UseShellExecute = true });
        System.Windows.MessageBox.Show(this,
            "在 ChatGPT 的安全设置中开启“开发人员模式”。完成后回到本窗口，勾选“已经开启开发人员模式”。",
            "开启开发人员模式",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void DeveloperModeReady_Checked(object sender, RoutedEventArgs e)
    {
        if (OpenConnectorButton is not null) OpenConnectorButton.IsEnabled = DeveloperModeReady.IsChecked == true;
    }

    private void OpenLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void CopyName_Click(object sender, RoutedEventArgs e) => CopyText(ConnectorName.Text);
    private void CopyUrl_Click(object sender, RoutedEventArgs e) => CopyText(ConnectorUrl.Text);
    private void CopyPairing_Click(object sender, RoutedEventArgs e) => CopyText(PairingCodeText.Text);

    private static void CopyText(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) System.Windows.Clipboard.SetText(value);
    }

    private async void CreatePairingCode_Click(object sender, RoutedEventArgs e)
    {
        WizardError.Text = string.Empty;
        PairButton.IsEnabled = false;
        try
        {
            await RunOperationAsync(async cancellationToken =>
            {
                var pairingCode = await _c2c.CreatePairingCodeAsync(_c2c.ConnectionWorkspace, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                PairingCodeText.Text = pairingCode;
                CopyPairingButton.Visibility = Visibility.Visible;
            });
        }
        catch (Exception error) { if (!_closing) WizardError.Text = error is OperationCanceledException ? "操作超时，请重试。" : error.Message; }
        finally { if (!_closing) PairButton.IsEnabled = true; }
    }

    private void FixedConnection_Checked(object sender, RoutedEventArgs e)
    {
        if (DomainName is not null) DomainName.IsEnabled = FixedConnection.IsChecked == true;
    }

    private async void ApplyConnectionMode_Click(object sender, RoutedEventArgs e)
    {
        WizardError.Text = string.Empty;
        try
        {
            await RunOperationAsync(async cancellationToken =>
            {
                await DisconnectSetupConnectionAsync();
                await _c2c.ChooseConnectionAsync(_c2c.ConnectionWorkspace, FixedConnection.IsChecked == true, DomainName.Text, cancellationToken);
                var result = await StartSetupConnectionAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ConnectorName.Text = result.ConnectorName;
                ConnectorUrl.Text = result.McpUrl;
                PairingCodeText.Text = string.Empty;
                CopyPairingButton.Visibility = Visibility.Collapsed;
            });
        }
        catch (Exception error) { if (!_closing) WizardError.Text = error is OperationCanceledException ? "操作超时，请重试。" : error.Message; }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CancelConnection_Click(object sender, RoutedEventArgs e)
    {
        _discardRequested = true;
        Close();
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_closing || _activeOperation is { IsCompleted: false }) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(150));
        var task = operation(timeout.Token);
        _activeOperation = task;
        try { await task; }
        finally
        {
            if (ReferenceEquals(_activeOperation, task)) _activeOperation = null;
        }
    }

    private async Task<C2cSetupResult> StartSetupConnectionAsync(CancellationToken cancellationToken)
    {
        // Mark this before starting: a failed start can already have spawned node or cloudflared.
        _activeProjectPath = _c2c.ConnectionWorkspace;
        _setupStartAttempted = true;
        try { return await _c2c.StartForSetupAsync(_connectionProfile, cancellationToken); }
        catch
        {
            await DisconnectSetupConnectionAsync();
            throw;
        }
    }

    private async void SetupWizardWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeAllowed) return;
        e.Cancel = true;
        _closeCleanup ??= CleanupBeforeCloseAsync();
        if (await _closeCleanup)
        {
            _closeAllowed = true;
            _lifetimeCancellation.Dispose();
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        else
        {
            _closeCleanup = null;
            LaterButton.Content = "重试关闭";
            LaterButton.IsEnabled = true;
        }
    }

    private async Task<bool> CleanupBeforeCloseAsync()
    {
        _closing = true;
        _authorizationTimer.Stop();
        if (_configurationOnly) return true;
        if (ConnectionTransferred) return true;
        // A web OAuth success can arrive between the last poll and the close click.
        // Finish an in-flight adoption and check once more before revoking anything.
        if (_step == 2 && !_discardRequested)
        {
            if (_activeOperation is { } operation)
            {
                try { await operation; } catch (Exception) { }
            }
            if (ConnectionTransferred) return true;
            using var checkTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (_activeProjectPath is not null && await _c2c.HasAuthorizationAsync(_activeProjectPath, checkTimeout.Token))
                {
                    await CompleteAsync(checkTimeout.Token);
                    return true;
                }
            }
            catch (Exception error)
            {
                // An indeterminate status is not permission to destroy a possibly paired connection.
                _closing = false;
                _authorizationTimer.Start();
                WizardError.Text = $"无法确认授权状态，连接暂时保留：{error.Message}";
                return false;
            }
        }
        _lifetimeCancellation.Cancel();
        WizardError.Text = ConnectionTransferred ? string.Empty : "正在停止首次设置启动的本地服务…";
        NextButton.IsEnabled = false;
        LaterButton.IsEnabled = false;

        var cleanup = FinishActiveOperationAndDisconnectAsync();
        if (await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(15))) != cleanup)
        {
            if (ConnectionTransferred) return true;
            // DisposeAsync is the last-resort cleanup path for a cancelled CLI operation.
            WizardError.Text = "停止本地服务超过 15 秒，正在执行强制清理…";
            try { await _c2c.DisposeAsync(); }
            catch (Exception error) { WizardError.Text = $"清理本地服务失败：{error.Message}"; return false; }
        }
        try { await cleanup; return true; }
        catch (Exception error) { WizardError.Text = $"清理本地服务失败：{error.Message}"; return false; }
    }

    private async Task FinishActiveOperationAndDisconnectAsync()
    {
        var operation = _activeOperation;
        if (operation is not null)
        {
            try { await operation; }
            catch (Exception) { /* The action handler owns its error; cleanup still has to run. */ }
        }
        await DisconnectSetupConnectionAsync();
    }

    private async Task DisconnectSetupConnectionAsync()
    {
        if (!ConnectionTransferred && _setupStartAttempted && _activeProjectPath is not null)
        {
            var workspace = _activeProjectPath;
            _setupStartAttempted = false;
            _activeProjectPath = null;
            if (_discardRequested) await _c2c.ForgetAuthorizationAsync();
            await _c2c.DisconnectAsync(workspace);
        }
    }

    private void ConnectionProvider_Checked(object sender, RoutedEventArgs e)
    {
        if (SecureTunnelPanel is not null)
            SecureTunnelPanel.Visibility = SecureProvider.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SaveConnectionConfiguration_Click(object sender, RoutedEventArgs e)
    {
        WizardError.Text = string.Empty;
        SaveConnectionConfigurationButton.IsEnabled = false;
        try
        {
            var saved = await ConnectionProfileConfiguration.SaveAsync(
                _connectionProfile,
                SecureProvider.IsChecked == true ? TunnelProvider.OpenAiSecureTunnel : TunnelProvider.CloudflareQuickTunnel,
                TunnelId.Text,
                RuntimeKeyInput.Password,
                RuntimeKeyReference.Text,
                _credentialStore,
                _store.SaveConnectionAsync);
            ConnectionProfileConfiguration.CopyTo(saved, _connectionProfile);
            SavedConnectionProfile = ConnectionProfileConfiguration.Clone(saved);
            RuntimeKeyInput.Clear();
            RuntimeKeyReference.Text = ConnectionProfileConfiguration.IsAdvancedCredentialReference(saved.RuntimeCredentialReference)
                ? saved.RuntimeCredentialReference
                : string.Empty;
            UpdateRuntimeKeyStatus();
            ConnectionConfigurationStatus.Text = _configurationOnly
                ? "已保存；当前连接不重启，下次连接生效。"
                : "连接配置已保存。";
        }
        catch (Exception error)
        {
            WizardError.Text = "连接配置保存失败；已保存的配置保持不变。" + Environment.NewLine + error.Message;
        }
        finally { SaveConnectionConfigurationButton.IsEnabled = true; }
    }

    private void EnsureDisplayedConfigurationIsSaved()
    {
        var displayedProvider = SecureProvider.IsChecked == true
            ? TunnelProvider.OpenAiSecureTunnel
            : TunnelProvider.CloudflareQuickTunnel;
        if (displayedProvider != _connectionProfile.Provider
            || displayedProvider == TunnelProvider.OpenAiSecureTunnel
               && (!string.Equals(TunnelId.Text.Trim(), _connectionProfile.TunnelId, StringComparison.Ordinal)
                   || RuntimeKeyInput.Password.Length > 0
                   || RuntimeKeyReference.Text.Trim().Length > 0
                      && !string.Equals(RuntimeKeyReference.Text.Trim(), _connectionProfile.RuntimeCredentialReference, StringComparison.Ordinal)))
            throw new InvalidOperationException("连接配置有未保存的改动。请先点击“保存连接配置”。");
    }

    private void UpdateRuntimeKeyStatus()
    {
        if (RuntimeKeyStatus is null) return;
        var reference = _connectionProfile.RuntimeCredentialReference;
        _hasSavedRuntimeKey = SecureTunnelRuntime.HasUsableRuntimeCredentialReference(reference, _credentialStore);
        RuntimeKeyStatus.Text = _hasSavedRuntimeKey
            ? "运行密钥已保存，可直接连接。"
            : IsAdvancedCredentialReference(reference)
                ? "已有密钥配置当前不可用，请检查后重试。"
                : "尚未保存运行密钥。";
        UpdateRuntimeKeyPlaceholder();
    }

    private void RuntimeKeyInput_Changed(object sender, RoutedEventArgs e) => UpdateRuntimeKeyPlaceholder();

    private void UpdateRuntimeKeyPlaceholder()
    {
        if (RuntimeKeySavedPlaceholder is null || RuntimeKeyInput is null) return;
        // Visual placeholder only: never load a secret or submit bullet characters as a key.
        RuntimeKeySavedPlaceholder.Visibility = _hasSavedRuntimeKey && RuntimeKeyInput.Password.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsAdvancedCredentialReference(string? reference)
        => ConnectionProfileConfiguration.IsAdvancedCredentialReference(reference);
}
