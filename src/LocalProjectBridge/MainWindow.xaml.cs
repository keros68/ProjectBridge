using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LocalProjectBridge.Core.Gateway;
using LocalProjectBridge.Core.Security;
using Forms = System.Windows.Forms;

namespace LocalProjectBridge;

public partial class MainWindow : Window
{
    private readonly RegistryStore _store;
    private readonly RuntimeDiscovery _discovery;
    private readonly ObservableCollection<ProjectRecord> _projects = [];
    private readonly Forms.NotifyIcon _tray;
    private readonly System.Drawing.Icon _trayIcon;
    private readonly Dictionary<string, ConnectionStatusIcon> _connectionIcons = [];
    private readonly RedactingLogger _logger;
    private SessionController _controller;
    private readonly CommandRunner _runner;
    private readonly BackendInstallerService _installer;
    private C2cAdapter? _c2c;
    private GatewayAdapter? _secureTunnel;
    private IConnectableAdapter _connectionAdapter;
    private readonly ProjectAuthorizationRegistry _sharedProjects = new();
    private readonly ChangeJournal _changeJournal;
    private readonly ProjectWriteService _writeService;
    private readonly ObservableCollection<ChangeRecord> _changes = [];
    private AppSettings _settings = new();
    private ConnectionProfile _connectionProfile;
    private RuntimePaths _paths = new(null, null, null, null, null, null, null);
    private bool _exiting;
    private bool _suppressCapabilitySync;
    private bool _reloadingProjects;
    private bool _initializingSettings = true;
    private UpdateInfo? _availableUpdate;
    private readonly SessionProjectPermissions _projectPermissions = new();
    private readonly bool _startHidden;
    private readonly DispatcherTimer _autoApplyTimer;
    private bool _suppressAutoApplyUi;

    public MainWindow(bool startHidden = false)
    {
        InitializeComponent();
        _startHidden = startHidden;
        _store = new RegistryStore();
        _logger = new RedactingLogger(_store.AppDataDirectory);
        _runner = new CommandRunner(_logger);
        _discovery = new RuntimeDiscovery();
        _installer = new BackendInstallerService(_runner, _store, _discovery);
        _settings = _store.Load();
        _connectionProfile = _store.LoadConnection();
        _collaborationStore = new LocalProjectBridge.Core.Collaboration.CollaborationStore(
            Path.Combine(_store.AppDataDirectory, "collaboration"));
        _changeJournal = new ChangeJournal(Path.Combine(_store.AppDataDirectory, "changes"));
        _writeService = new ProjectWriteService(_sharedProjects, new WriteLeaseStore(), _changeJournal);
        _changeJournal.Changed += ChangeJournal_Changed;
        _sharedProjects.ProjectAccessed += SharedProjects_ProjectAccessed;
        RefreshAuthorizedProjects();
        if (_settings.ConnectionVersion < 2) _settings.SetupCompleted = false;
        _connectionAdapter = CreateConnectionAdapter();
        // 设计 6.4：连接与断开只由 SessionController 执行
        _controller = BuildController();
        _controller.StateChanged += Controller_StateChanged;
        _controller.ReadinessChanged += Controller_ReadinessChanged;
        ProjectPicker.ItemsSource = _projects;
        ChangeList.ItemsSource = _changes;
        InitializeCollaborationUi();
        StartWithWindowsSetting.IsChecked = StartupRegistration.IsEnabled();
        RestoreReadOnlyConnectionSetting.IsChecked = _settings.RestoreReadOnlyConnection;
        CheckForUpdatesSetting.IsChecked = _settings.CheckForUpdates;
        CurrentVersionText.Text = $"当前版本 v{CurrentVersion.ToString(3)}。有新版本时在窗口顶部提示，点“立即更新”会下载并校验安装程序，自动退出、安装并重新启动；项目和连接设置不受影响。";
        _initializingSettings = false;
        _trayIcon = LoadTrayIcon();
        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "ProjectBridge",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        UpdateShellStatus(SessionState.Disconnected, ConnectionReadiness.Stopped);
        _autoApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _autoApplyTimer.Tick += (_, _) => UpdateWriteUi();
        _autoApplyTimer.Start();
        Loaded += async (_, _) => { await InitializeAsync(); UpdateOnboardingUi(); await CheckForUpdatesAsync(); };
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/ProjectBridge;component/Assets/ProjectBridge.ico"));
        if (resource is null) return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        using var icon = new System.Drawing.Icon(resource.Stream);
        return (System.Drawing.Icon)icon.Clone();
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("断开", null, async (_, _) => await Dispatcher.InvokeAsync(() => _ = DisconnectAsync()));
        menu.Items.Add("退出", null, async (_, _) => await Dispatcher.InvokeAsync(() => _ = ExitAsync()));
        return menu;
    }

    private void UpdateShellStatus(SessionState state, ConnectionReadiness readiness)
    {
        if (_tray is null) return;
        var profile = _controller.CurrentConnection ?? _connectionProfile;
        var ready = state == SessionState.Connected && readiness.LocalReady && readiness.TransportReady;
        HeaderWebGuideButton.Content = ready && readiness.LastVerifiedCall is null
            ? "下一步：添加插件并验证" : "添加插件 / 验证连接";
        var adapter = _c2c;
        ConnectionWebGuide.Configure(profile, adapter?.ObservedMcpUrl, ready,
            ready && readiness.ClientAuthorized && readiness.LastVerifiedCall is not null,
            adapter is null ? null : () => adapter.CreatePairingCodeAsync(adapter.ConnectionWorkspace));
        var (color, description) = state switch
        {
            SessionState.Disconnected => ("#8A919E", "未连接"),
            SessionState.NeedsAttention => ("#B42318", "连接异常，需要处理"),
            SessionState.Connected when readiness.LocalReady && readiness.TransportReady =>
                ("#2563EB", readiness.ClientAuthorized && readiness.LastVerifiedCall is not null
                    ? "已连接，网页已验证" : "已连接，等待网页验证"),
            SessionState.Disconnecting => ("#D97706", "正在断开"),
            SessionState.Authorizing => ("#D97706", "正在授权"),
            _ => ("#D97706", "正在连接或检查")
        };
        if (!_connectionIcons.TryGetValue(color, out var statusIcon))
            _connectionIcons[color] = statusIcon = new ConnectionStatusIcon(_trayIcon, color);
        _tray.Icon = statusIcon.TrayIcon;
        _tray.Text = "ProjectBridge · " + description;
        Icon = statusIcon.WindowIcon;
        TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
        TaskbarItemInfo.Overlay = statusIcon.Overlay;
        TaskbarItemInfo.Description = _tray.Text;
    }

    private async Task InitializeAsync()
    {
        ReloadProjects();
        RefreshChanges();
        RefreshDiagnostics();
        await _controller.CleanupStaleSessionsAsync();
        SetState(_controller.State);
        if (!_settings.SetupCompleted && !_startHidden)
        {
            await ShowSetupWizardAsync(configurationOnly: false);
            return;
        }
        if (_settings.SetupCompleted && _settings.RestoreReadOnlyConnection)
            await RestoreReadOnlyConnectionAsync();
    }

    private SessionController BuildController()
    {
        return new SessionController([_connectionAdapter], _logger, _store.AppDataDirectory);
    }

    private IConnectableAdapter CreateConnectionAdapter()
    {
        _c2c = null;
        _secureTunnel = null;
        if (_connectionProfile.Provider == TunnelProvider.OpenAiSecureTunnel)
        {
            var activeProfile = ConnectionProfileConfiguration.Clone(_connectionProfile);
            _secureTunnel = new GatewayAdapter(
                _logger,
                _runner,
                _discovery,
                connectionProfile: activeProfile,
                authorizations: _sharedProjects,
                stateDirectory: Path.Combine(_store.AppDataDirectory, "secure-tunnel"),
                writeService: _writeService,
                collaborationStore: _collaborationStore);
            return _secureTunnel;
        }
        _c2c = new C2cAdapter(_runner, _discovery, sharedProjects: _sharedProjects,
            writeService: _writeService, connectionId: _connectionProfile.Id,
            collaborationStore: _collaborationStore);
        return _c2c;
    }

    private async Task RebuildControllerAsync()
    {
        _controller.StateChanged -= Controller_StateChanged;
        _controller.ReadinessChanged -= Controller_ReadinessChanged;
        await _controller.DisposeAsync();
        _connectionAdapter = CreateConnectionAdapter();
        _controller = BuildController();
        _controller.StateChanged += Controller_StateChanged;
        _controller.ReadinessChanged += Controller_ReadinessChanged;
        SetState(_controller.State);
    }

    private void ReloadProjects()
    {
        var selectedId = _settings.SelectedProjectId;
        _reloadingProjects = true;
        _projects.Clear();
        foreach (var project in _settings.Projects.Where(p => Directory.Exists(p.Path))) _projects.Add(project);
        ProjectPicker.SelectedItem = _projects.FirstOrDefault(p => p.Id == selectedId) ?? _projects.FirstOrDefault();
        SyncCapabilityControls();
        _reloadingProjects = false;
    }

    private bool SupportsCodexTasks => SessionProjectPermissions.SupportsCodexTasks(_connectionProfile.Provider);

    private bool IsCodexTaskEnabled(ProjectRecord project)
        => _projectPermissions.IsCodexTaskEnabled(project, _connectionProfile.Provider);

    private bool IsCodexTaskWriteEnabled(ProjectRecord project)
        => _projectPermissions.IsCodexTaskWriteEnabled(project, _connectionProfile.Provider);

    private void RefreshAuthorizedProjects()
    {
        _sharedProjects.ReplaceProjects(_settings.Projects.Select(project => new ProjectRecord
        {
            Id = project.Id,
            Name = project.Name,
            Path = project.Path,
            AllowWebRead = project.AllowWebRead,
            AllowCodexTasks = IsCodexTaskEnabled(project),
            AllowCodexWrite = IsCodexTaskWriteEnabled(project),
            AllowCodexAskWeb = project.AllowCodexAskWeb,
            LastConnectedAt = project.LastConnectedAt,
            LastAccessedAt = project.LastAccessedAt,
            LastFault = project.LastFault
        }));
        foreach (var project in _settings.Projects.Where(project => !project.AllowWebRead || !project.AllowCodexAskWeb))
            CancelProjectCollaboration(project.Id);
    }

    private void RefreshDiagnostics()
    {
        _paths = _discovery.Discover();
        RuntimeStatus.Text = _connectionProfile.Provider == TunnelProvider.OpenAiSecureTunnel
            ? $"连接方式：OpenAI Secure Tunnel    tunnel-client {Mark(_paths.TunnelReady)}\n运行只需要 Tunnel ID 和受限运行密钥，不使用管理员密钥。"
            : $"连接方式：临时 OAuth 连接    Node {Mark(_paths.NodeReady)}    项目连接组件 {Mark(_paths.C2cReady)}\n组件由程序共用，连接与授权状态见上方。";
    }

    private static string Mark(bool ready) => ready ? "✓" : "×";

    private void SyncCapabilityControls()
    {
        if (ProjectPicker.SelectedItem is not ProjectRecord project)
        {
            _suppressCapabilitySync = true;
            ReviewOnly.IsChecked = true;
            DelegateTasks.IsChecked = false;
            DelegateWritableTasks.IsChecked = false;
            DelegateWritableTasks.Visibility = Visibility.Collapsed;
            DelegateWritableTasks.IsEnabled = false;
            CollaborationPermission.IsChecked = false;
            SelectedProjectName.Text = "选择一个项目";
            ProjectPathText.Text = ProjectPermissionText.Text = ProjectRecentAccessText.Text = string.Empty;
            _suppressCapabilitySync = false;
            RefreshCollaborationRequests();
            return;
        }
        _suppressCapabilitySync = true;
        ReviewOnly.IsChecked = project.AllowWebRead;
        DelegateTasks.Visibility = SupportsCodexTasks ? Visibility.Visible : Visibility.Collapsed;
        DelegateTasks.IsChecked = IsCodexTaskEnabled(project);
        DelegateWritableTasks.Visibility = SupportsCodexTasks ? Visibility.Visible : Visibility.Collapsed;
        DelegateWritableTasks.IsChecked = IsCodexTaskWriteEnabled(project);
        DelegateWritableTasks.IsEnabled = ProjectPicker.IsEnabled && SupportsCodexTasks && IsCodexTaskEnabled(project);
        CollaborationPermission.IsChecked = project.AllowCodexAskWeb;
        SelectedProjectName.Text = project.Name;
        ProjectPathText.Text = project.Path;
        ProjectPermissionText.Text = ProjectPermissionSummary(project);
        ProjectRecentAccessText.Text = "最近成功访问：" + FormatTime(project.LastAccessedAt);
        CapabilityAvailabilityText.Text = _projectPermissions.DescribeCodexTasks(project, _connectionProfile.Provider);
        _suppressCapabilitySync = false;
        RefreshChanges();
        UpdateWriteUi();
        RefreshCollaborationRequests();
    }

    private string ProjectPermissionSummary(ProjectRecord project)
    {
        var permissions = new List<string>();
        if (project.AllowWebRead) permissions.Add("网页只读");
        if (IsCodexTaskWriteEnabled(project)) permissions.Add("可写 Codex 委派");
        else if (IsCodexTaskEnabled(project)) permissions.Add("只读 Codex 委派");
        if (project.AllowCodexAskWeb) permissions.Add("网页协作请求");
        return permissions.Count == 0 ? "当前未授权访问。" : "当前权限：" + string.Join("、", permissions) + "。";
    }

    private async void Capability_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressCapabilitySync || ProjectPicker.SelectedItem is not ProjectRecord project) return;
        project.AllowWebRead = ReviewOnly.IsChecked == true;
        if (ReferenceEquals(sender, CollaborationPermission))
            project.AllowCodexAskWeb = CollaborationPermission.IsChecked == true;
        if (SupportsCodexTasks && ReferenceEquals(sender, DelegateTasks))
        {
            await SetCodexTaskPermissionAsync(project, DelegateTasks.IsChecked == true);
            return;
        }
        if (SupportsCodexTasks && ReferenceEquals(sender, DelegateWritableTasks))
        {
            await SetCodexTaskWritePermissionAsync(project, DelegateWritableTasks.IsChecked == true);
            return;
        }
        SyncCapabilityControls();
        await SaveSettingsAsync();
    }

    private async Task SetCodexTaskPermissionAsync(ProjectRecord project, bool enabled)
    {
        if (!_projectPermissions.SetCodexTaskEnabled(project, _connectionProfile.Provider, enabled)) return;
        RefreshAuthorizedProjects();
        SyncCapabilityControls();
        await SaveSettingsAsync();
    }

    private async Task SetCodexTaskWritePermissionAsync(ProjectRecord project, bool enabled)
    {
        if (!_projectPermissions.SetCodexTaskWriteEnabled(project, _connectionProfile.Provider, enabled)) return;
        RefreshAuthorizedProjects();
        SyncCapabilityControls();
        await SaveSettingsAsync();
    }

    private async Task RevokeCodexTaskWritePermissionsAsync()
    {
        if (!_projectPermissions.RevokeCodexTaskWriteApprovals()) return;
        RefreshAuthorizedProjects();
        try { await RefreshTaskBridgePermissionsAsync(); }
        catch (Exception error)
        {
            await _logger.WriteAsync("error", $"更新 Codex 委派权限失败: {error.Message}");
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ClearError();
        _projectPermissions.BeginInteractiveSession();
        RefreshAuthorizedProjects();
        await ShowSetupWizardAsync(configurationOnly: false);
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await DisconnectAsync();

    private async Task DisconnectAsync()
    {
        _writeService.RevokeAllAutoApply();
        await RevokeCodexTaskWritePermissionsAsync();
        SyncCapabilityControls();
        await _logger.WriteAsync("info", "user requested shared connection disconnect");
        ClearError();
        UpdateWriteUi();
        await _controller.DisconnectAsync();
        await SaveSettingsAsync();
    }

    private void Controller_StateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        if (e.State != SessionState.Connected) _writeService.RevokeAllAutoApply();
        Dispatcher.Invoke(() =>
        {
            if (e.State != SessionState.Connected) _ = RevokeCodexTaskWritePermissionsAsync();
            SetState(e.State);
            if (e.State is SessionState.Connected or SessionState.Disconnected or SessionState.NeedsAttention) SyncCapabilityControls();
            if (e.State is SessionState.Disconnected or SessionState.NeedsAttention)
            {
                UpdateWriteUi();
            }
            StatusDetail.Text = e.Detail;
            if (e.State == SessionState.Connected) ApplyReadinessToUi(_controller.Readiness);
            if (e.State == SessionState.NeedsAttention && _controller.LastError is { } error)
                ShowError(error.What, error.NextAction, error.Detail);
            else if (e.State is SessionState.Connected or SessionState.Checking or SessionState.Starting
                     or SessionState.Authorizing or SessionState.Disconnecting or SessionState.Disconnected)
                ClearError();
        });
    }

    private void SetState(SessionState state)
    {
        StatusText.Text = state switch
        {
            SessionState.Checking => "正在检查",
            SessionState.Starting => "正在连接",
            SessionState.Authorizing => "正在授权",
            SessionState.Connected => "通道已就绪，等待网页验证",
            SessionState.Recovering => "正在恢复",
            SessionState.NeedsAttention => "需要处理",
            SessionState.Disconnecting => "正在断开",
            _ => "未连接"
        };
        StatusDot.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(state switch
        {
            SessionState.Checking or SessionState.Starting or SessionState.Authorizing or SessionState.Disconnecting
                or SessionState.Connected or SessionState.Recovering => "#D97706",
            SessionState.NeedsAttention => "#B42318",
            _ => "#8A919E"
        }));
        ConnectButton.IsEnabled = state is (SessionState.Disconnected or SessionState.NeedsAttention)
            && !_controller.HasPendingCleanup;
        OpenSetupButton.IsEnabled = state is SessionState.Disconnected or SessionState.NeedsAttention or SessionState.Connected;
        CopyConnectionButton.Content = _connectionProfile.Provider == TunnelProvider.OpenAiSecureTunnel ? "复制 Tunnel ID" : "复制连接地址";
        CopyConnectionButton.IsEnabled = state == SessionState.Connected
            && (!string.IsNullOrEmpty(_c2c?.ObservedMcpUrl) || !string.IsNullOrEmpty(_connectionProfile.TunnelId));
        ForgetPairingButton.IsEnabled = _connectionProfile.Provider == TunnelProvider.CloudflareQuickTunnel
            && state is (SessionState.Disconnected or SessionState.NeedsAttention);
        DisconnectButton.IsEnabled = state is SessionState.Checking or SessionState.Starting or SessionState.Authorizing
            or SessionState.Connected or SessionState.Recovering || _controller.HasPendingCleanup;
        ProjectPicker.IsEnabled = state is SessionState.Disconnected or SessionState.NeedsAttention or SessionState.Connected;
        RemoveProjectButton.IsEnabled = ProjectPicker.IsEnabled && ProjectPicker.SelectedItem is ProjectRecord;
        AddProjectButton.IsEnabled = ProjectPicker.IsEnabled;
        var canEditCapabilities = ProjectPicker.IsEnabled;
        ReviewOnly.IsEnabled = canEditCapabilities;
        DelegateTasks.IsEnabled = canEditCapabilities && SupportsCodexTasks;
        DelegateWritableTasks.IsEnabled = canEditCapabilities && SupportsCodexTasks
            && ProjectPicker.SelectedItem is ProjectRecord selectedProject && IsCodexTaskEnabled(selectedProject);
        CollaborationPermission.IsEnabled = canEditCapabilities;
        ConnectionIdentityText.Text = $"{ConnectionProviderName()} · {_connectionProfile.Name}\n连接 ID：{_connectionProfile.Id:D}";
        SyncCapabilityControls();
        UpdateWriteUi();
        UpdateShellStatus(state, _controller.Readiness);
        UpdateOnboardingUi();
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.IsBusy && _controller.State != SessionState.Connected) return;
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择本地项目文件夹",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

        var path = Path.GetFullPath(dialog.SelectedPath);
        var project = _settings.Projects.FirstOrDefault(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            project = new ProjectRecord { Name = string.IsNullOrWhiteSpace(name) ? path : name, Path = path };
            _settings.Projects.Add(project);
        }
        _settings.SelectedProjectId = project.Id;
        _settings.SelectedProjectPath = project.Path;
        await SaveSettingsAsync();
        ReloadProjects();
        UpdateOnboardingUi();
    }

    private ProjectRecord? OnboardingProject()
        => _settings.Projects.FirstOrDefault(project => project.AllowWebRead && Directory.Exists(project.Path));

    private void UpdateOnboardingUi()
    {
        var project = OnboardingProject();
        var connectionReady = _settings.SetupCompleted;
        var projectAdded = project is not null;
        var projectVerified = project?.LastAccessedAt is not null;
        var webVerified = projectVerified || _controller.Readiness.LastVerifiedCall is not null || _connectionProfile.LastVerifiedCall is not null;
        var completed = connectionReady && webVerified && projectAdded && projectVerified;

        OnboardingPanel.Visibility = completed ? Visibility.Collapsed : Visibility.Visible;
        if (completed) return;

        var done = new[] { connectionReady, webVerified, projectAdded, projectVerified }.Count(value => value);
        OnboardingProgressText.Text = $"{done} / 4 已完成";
        OnboardingConnectionStatus.Text = connectionReady ? "✓ 共享连接已设置" : "○ 建立共享连接";
        OnboardingWebStatus.Text = webVerified ? "✓ ChatGPT 已完成真实工具调用" : "○ 连接 ChatGPT";
        OnboardingProjectStatus.Text = projectAdded ? $"✓ 已添加项目：{project!.Name}" : "○ 添加第一个项目";
        OnboardingVerificationStatus.Text = projectVerified ? "✓ 项目读取验证成功" : "○ 验证项目读取";

        if (!connectionReady || _controller.State != SessionState.Connected)
        {
            OnboardingActionButton.Content = "建立 / 恢复连接";
            OnboardingHintText.Text = "先建立共享连接。首次使用建议选择“快速体验”。";
        }
        else if (!projectAdded)
        {
            OnboardingActionButton.Content = "去添加项目";
            OnboardingHintText.Text = "选择一个本地项目文件夹，默认仅授权网页读取。";
        }
        else
        {
            OnboardingActionButton.Content = "复制项目验证提示词";
            OnboardingHintText.Text = "把提示词发送到 ChatGPT。成功列出该项目根目录后，这张卡会自动完成。";
        }
    }

    private async void ContinueOnboarding_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.SetupCompleted || _controller.State != SessionState.Connected)
        {
            await ShowSetupWizardAsync(configurationOnly: false);
            UpdateOnboardingUi();
            return;
        }

        var project = OnboardingProject();
        if (project is null)
        {
            MainTabs.SelectedIndex = 0;
            AddProject_Click(sender, e);
            return;
        }

        if (project.LastAccessedAt is not null)
        {
            UpdateOnboardingUi();
            return;
        }

        System.Windows.Clipboard.SetText(BuildProjectVerificationPrompt(project));
        OnboardingHintText.Text = $"已复制“{project.Name}”的验证提示词。到 ChatGPT 选中 ProjectBridge 后直接粘贴发送。";
    }

    private static string BuildProjectVerificationPrompt(ProjectRecord project)
        => $"请使用我已选中的 ProjectBridge 插件，先实际调用 list_projects，确认项目“{project.Name}”（project_id: {project.Id:D}）存在；然后实际调用 list_directory，project_id 使用 {project.Id:D}，path 传空字符串，列出该项目根目录。仅验证连接，不读取文件内容、不修改文件、不执行任务。不要只口头确认连接成功，也不要改用其他工具。";

    private async void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.IsBusy && _controller.State != SessionState.Connected) return;
        if (ProjectPicker.SelectedItem is not ProjectRecord project) return;
        var answer = System.Windows.MessageBox.Show(
            this,
            $"移除“{project.Name}”的访问权限和登记？项目文件不会删除。",
            "移除项目",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        _settings.Projects.Remove(project);
        RefreshAuthorizedProjects();
        CancelProjectCollaboration(project.Id);
        var next = _settings.Projects.FirstOrDefault(item => Directory.Exists(item.Path));
        _settings.SelectedProjectId = next?.Id;
        _settings.SelectedProjectPath = next?.Path;
        await SaveSettingsAsync();
        ReloadProjects();
        UpdateOnboardingUi();
        UpdateWriteUi();
    }

    private async void ProjectPicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_reloadingProjects) return;
        SyncCapabilityControls();
        await SaveSelectionAsync();
    }

    private async Task SaveSelectionAsync()
    {
        var selected = ProjectPicker.SelectedItem as ProjectRecord;
        _settings.SelectedProjectId = selected?.Id;
        _settings.SelectedProjectPath = selected?.Path;
        await SaveSettingsAsync();
    }

    private async Task<bool> SaveSettingsAsync()
    {
        RefreshAuthorizedProjects();
        try
        {
            await _store.SaveAsync(_settings);
            await RefreshTaskBridgePermissionsAsync();
            return true;
        }
        catch (Exception error)
        {
            await _logger.WriteAsync("error", $"保存项目登记失败: {error.Message}");
            ShowError("项目设置未保存。", "请重试；若仍失败，请关闭其他 ProjectBridge 窗口。", error.Message);
            return false;
        }
    }

    private async Task RefreshTaskBridgePermissionsAsync()
    {
        if (_c2c is not null) await _c2c.RefreshSharedPermissionsAsync();
        if (_secureTunnel is not null) await _secureTunnel.RefreshSharedPermissionsAsync();
    }

    private void RefreshDiagnostics_Click(object sender, RoutedEventArgs e) => RefreshDiagnostics();

    private void CopyConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.State != SessionState.Connected) return;
        if (_c2c?.ObservedMcpUrl is { Length: > 0 } url) System.Windows.Clipboard.SetText(url);
        else if (_connectionProfile.TunnelId is { Length: > 0 } tunnelId) System.Windows.Clipboard.SetText(tunnelId);
    }

    private void ShowWebGuide_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 1;
        ConnectionWebGuide.ShowSetupFirst();
        ConnectionScrollViewer.ScrollToHome();
        ConnectionWebGuide.BringIntoView();
    }

    private async void OpenSetupWizard_Click(object sender, RoutedEventArgs e)
        => await ShowSetupWizardAsync(configurationOnly: true);

    private async void ForgetPairing_Click(object sender, RoutedEventArgs e)
    {
        if (_c2c is null || _controller.IsBusy) return;
        var answer = System.Windows.MessageBox.Show(
            this,
            "解除配对后，ChatGPT 需要重新完成 OAuth 授权。项目登记不会删除。",
            "解除配对",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            await _c2c.ForgetAuthorizationAsync();
            _connectionProfile.LastAuthorizedAt = null;
            _connectionProfile.LastVerifiedCall = null;
            _connectionProfile.LastVerifiedClientId = null;
            await _store.SaveConnectionAsync(_connectionProfile);
            StatusDetail.Text = "配对已解除。下次连接需要重新完成网页授权。";
        }
        catch (Exception error)
        {
            ShowError("解除配对失败。", "保留现有配对；确认连接组件可用后重试。", error.Message);
        }
    }

    private void Controller_ReadinessChanged(object? sender, ConnectionReadinessChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var activeProfile = _controller.CurrentConnection;
            if (activeProfile is not null) _ = PersistActiveReadinessAsync(activeProfile);
            ApplyReadinessToUi(e.Readiness);
            UpdateWriteUi();
        });
    }

    private async Task PersistActiveReadinessAsync(ConnectionProfile activeProfile)
    {
        try
        {
            var persisted = await _store.TryUpdateConnectionReadinessAsync(activeProfile);
            if (persisted is null) return;
            if (ConnectionProfileConfiguration.RuntimeConfigurationEquals(_connectionProfile, persisted))
                ConnectionProfileConfiguration.CopyTo(persisted, _connectionProfile);
        }
        catch (Exception error)
        {
            await _logger.WriteAsync("error", "saving connection readiness failed: " + error.Message);
        }
    }

    private void ApplyReadinessToUi(ConnectionReadiness readiness)
    {
        StatusText.Text = readiness switch
        {
            { LocalReady: true, TransportReady: true, ClientAuthorized: true, LastVerifiedCall: not null } => "网页已验证",
            { LocalReady: true, TransportReady: true, ClientAuthorized: true } => "已授权，等待网页验证",
            { TransportReady: true } => "通道已就绪，等待网页验证",
            { LocalReady: true } => "本地服务已就绪",
            _ when _controller.State == SessionState.Connected => "连接状态待确认",
            _ => StatusText.Text
        };
        StatusDot.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            readiness is { LocalReady: true, TransportReady: true, ClientAuthorized: true, LastVerifiedCall: not null }
                ? "#15803D"
                : readiness.LocalReady || readiness.TransportReady ? "#D97706" : "#8A919E"));
        LocalStatusText.Text = ReadyText(readiness.LocalReady);
        TunnelStatusText.Text = ReadyText(readiness.TransportReady);
        AuthorizationStatusText.Text = readiness.ClientAuthorized ? "已授权" : "未确认";
        var historicalProfile = _controller.CurrentConnection ?? _connectionProfile;
        LastWebCallText.Text = readiness.LastVerifiedCall is { } currentCall
            ? FormatTime(currentCall)
            : historicalProfile.LastVerifiedCall is { } historicalCall
                ? $"本次尚无（上次：{FormatTime(historicalCall)}）"
                : "尚无";
        HeaderRecentAccess.Text = "本次网页访问：" + FormatTime(readiness.LastVerifiedCall);
        StatusDetail.Text = readiness.LastVerifiedCall is not null && (!readiness.LocalReady || !readiness.TransportReady)
            ? "最近访问是历史记录，当前连接仍需重新验证。"
            : readiness.LocalReady && readiness.TransportReady && !readiness.ClientAuthorized
                ? "通道已就绪。请在 ChatGPT 中执行一次项目读取；验证成功后可在本机授权写入。"
                : string.Empty;
        UpdateShellStatus(_controller.State, readiness);
    }

    private static string ReadyText(bool ready) => ready ? "已就绪" : "未确认";

    private static string FormatTime(DateTimeOffset? value)
        => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无";

    private string ConnectionProviderName()
        => _connectionProfile.Provider == TunnelProvider.OpenAiSecureTunnel
            ? "OpenAI Secure Tunnel" : "临时 OAuth 连接";

    private async Task RestoreReadOnlyConnectionAsync()
    {
        if (_controller.State != SessionState.Disconnected) return;
        _projectPermissions.BeginReadOnlyRestore();
        RefreshAuthorizedProjects();
        SyncCapabilityControls();
        StatusDetail.Text = "正在恢复只读连接…";
        var workspace = Directory.CreateDirectory(
            Path.Combine(_store.AppDataDirectory, "connection", _connectionProfile.Id.ToString("N"))).FullName;
        if (!await _controller.ConnectAsyncWithTimeout(
                _connectionProfile, workspace, CapabilityFlags.WebRead).ConfigureAwait(false))
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_controller.LastError is { } error)
                    ShowError(error.What, error.NextAction, error.Detail);
            });
        }
    }

    private async Task ShowSetupWizardAsync(bool configurationOnly)
    {
        if (configurationOnly)
        {
            var pendingProfile = ConnectionProfileConfiguration.Clone(_connectionProfile);
            await _logger.WriteAsync("info", "opening connection settings; keeping existing controller and runtime");
            await using var settingsC2c = new C2cAdapter(_runner, _discovery, sharedProjects: _sharedProjects,
                writeService: _writeService, connectionId: pendingProfile.Id, collaborationStore: _collaborationStore);
            var settingsWizard = new SetupWizardWindow(
                _store, _installer, settingsC2c,
                _settings, pendingProfile,
                (_, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                configurationOnly: true) { Owner = this };
            settingsWizard.ShowDialog();
            if (settingsWizard.SavedConnectionProfile is { } saved)
            {
                _connectionProfile = saved;
                RefreshDiagnostics();
                SetState(_controller.State);
                if (_controller.State == SessionState.Connected) ApplyReadinessToUi(_controller.Readiness);
            }
            return;
        }
        if (_controller.IsBusy) return;
        _projectPermissions.BeginInteractiveSession();
        RefreshAuthorizedProjects();
        await RebuildControllerAsync();
        SetState(SessionState.Authorizing);
        StatusDetail.Text = "正在设置共享连接，等待网页授权。";
        var setupC2c = _c2c ?? new C2cAdapter(_runner, _discovery, sharedProjects: _sharedProjects,
            writeService: _writeService, connectionId: _connectionProfile.Id,
            collaborationStore: _collaborationStore);
        var wizard = new SetupWizardWindow(_store, _installer, setupC2c, _settings, _connectionProfile, async (profile, token) =>
        {
            RefreshAuthorizedProjects();
            if (!await _controller.AdoptSetupConnectionAsync(profile, setupC2c.ConnectionWorkspace, setupC2c, token))
                throw new InvalidOperationException(_controller.LastError?.What ?? "无法接管已配对的连接。");
            _c2c = setupC2c;
            _secureTunnel = null;
            _connectionAdapter = setupC2c;
            await _store.SaveConnectionAsync(_connectionProfile);
            await _logger.WriteAsync("info", "setup OAuth verified; shared connection adopted by main window");
        }, async (profile, token) =>
        {
            _connectionProfile = profile;
            RefreshAuthorizedProjects();
            await _store.SaveConnectionAsync(_connectionProfile);
            await RebuildControllerAsync();
            var workspace = Directory.CreateDirectory(Path.Combine(_store.AppDataDirectory, "connection", profile.Id.ToString("N"))).FullName;
            if (!await _controller.ConnectAsyncWithTimeout(profile, workspace, CapabilityFlags.WebRead, token))
                throw new InvalidOperationException(_controller.LastError?.What ?? "无法启动 OpenAI Secure Tunnel。");
            await _logger.WriteAsync("info", "secure tunnel local service and transport ready; awaiting real web call");
        }) { Owner = this };
        wizard.ShowDialog();
        // Cancel/failure can dispose the adapter as a last-resort cleanup. Start fresh next time.
        if (!wizard.ConnectionTransferred) await RebuildControllerAsync();
        ReloadProjects();
        RefreshDiagnostics();
        SetState(_controller.State);
        await SaveSettingsAsync();
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var project = ProjectPicker.SelectedItem as ProjectRecord;
        var session = _controller.CurrentSession;
        var lines = new List<string>
        {
            "Local Project Bridge",
            $"状态: {StatusText.Text}",
            $"项目: {project?.Name ?? "未选择"}",
            $"连接: {_connectionProfile.Provider} / {_connectionProfile.Id:D}",
            $"本地就绪: {_controller.Readiness.LocalReady}",
            $"隧道就绪: {_controller.Readiness.TransportReady}",
            $"客户端授权: {_controller.Readiness.ClientAuthorized}",
            $"最近网页调用: {_controller.Readiness.LastVerifiedCall?.ToString("O") ?? "无"}",
            RuntimeStatus.Text,
            $"故障摘要: {ErrorSummaryText.Text}",
            $"日志: {_logger.LogPath}"
        };
        if (session is not null) lines.Add($"会话: {session.SessionId:D}");
        if (_controller.LastError?.Detail is { } detail) lines.Add($"技术详情: {detail}");
        System.Windows.Clipboard.SetText(string.Join("\n", lines));
    }

    private async void StartWithWindowsSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingSettings) return;
        try
        {
            var enabled = StartWithWindowsSetting.IsChecked == true;
            StartupRegistration.SetEnabled(enabled);
            _settings.StartWithWindows = enabled;
            await _store.SaveAsync(_settings);
        }
        catch (Exception error)
        {
            ShowError("无法更新 Windows 启动设置。", "当前设置保持不变。", error.Message);
            _initializingSettings = true;
            StartWithWindowsSetting.IsChecked = StartupRegistration.IsEnabled();
            _initializingSettings = false;
        }
    }

    private static Version CurrentVersion
        => typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private async Task CheckForUpdatesAsync()
    {
        if (!_settings.CheckForUpdates) return;
        try
        {
            if (await new UpdateChecker().CheckAsync(CurrentVersion) is not { } update) return;
            _availableUpdate = update;
            UpdateBannerText.Text = $"新版本 v{update.Version.ToString(3)} 可用（当前 v{CurrentVersion.ToString(3)}）。";
            InstallUpdateButton.Visibility = update.CanInstall ? Visibility.Visible : Visibility.Collapsed;
            UpdateBanner.Visibility = Visibility.Visible;
        }
        catch (Exception error)
        {
            // 离线或 GitHub 不可达时静默跳过，不影响使用。
            await _logger.WriteAsync("info", $"update check skipped: {error.GetType().Name}");
        }
    }

    private void OpenUpdatePage_Click(object sender, RoutedEventArgs e)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_availableUpdate?.PageUrl ?? UpdateChecker.ReleasesPage) { UseShellExecute = true });

    /// <summary>下载并校验安装程序，静默安装到当前程序目录；安装程序等本程序退出后替换文件并重新启动。</summary>
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { CanInstall: true } update) return;
        var appDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;
        InstallUpdateButton.IsEnabled = false;
        try
        {
            UpdateBannerText.Text = "正在下载新版本…";
            var progress = new Progress<int>(percent => UpdateBannerText.Text = $"正在下载新版本… {percent}%");
            var installer = await new UpdateChecker().DownloadInstallerAsync(update,
                Path.Combine(Path.GetTempPath(), "ProjectBridge-Update"), progress);
            UpdateBannerText.Text = "下载完成，正在退出并安装新版本…";
            await _logger.WriteAsync("info", $"installing update {update.Tag} into {appDirectory}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer)
            {
                ArgumentList = { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/AUTOUPDATE=1", $"/DIR={appDirectory}" },
                UseShellExecute = false
            });
            await ExitAsync();
        }
        catch (Exception error)
        {
            await _logger.WriteAsync("error", $"自动更新失败: {error.Message}");
            UpdateBannerText.Text = $"自动更新失败：{error.Message} 可点击“查看更新说明”手动下载。";
            InstallUpdateButton.IsEnabled = true;
        }
    }

    private async void CheckForUpdatesSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingSettings) return;
        _settings.CheckForUpdates = CheckForUpdatesSetting.IsChecked == true;
        await _store.SaveAsync(_settings);
        if (_settings.CheckForUpdates) await CheckForUpdatesAsync();
        else UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private async void RestoreReadOnlyConnectionSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingSettings) return;
        _settings.RestoreReadOnlyConnection = RestoreReadOnlyConnectionSetting.IsChecked == true;
        await _store.SaveAsync(_settings);
    }

    private void SharedProjects_ProjectAccessed(object? sender, ProjectAccessedEventArgs e)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            var project = _settings.Projects.FirstOrDefault(candidate => candidate.Id == e.ProjectId);
            if (project is null) return;
            project.LastAccessedAt = e.At;
            UpdateOnboardingUi();
            if (ProjectPicker.SelectedItem is ProjectRecord selected && selected.Id == e.ProjectId)
                SyncCapabilityControls();
            await _store.SaveAsync(_settings);
        });
    }

    private string? CurrentWriteClient()
        => _controller.State == SessionState.Connected ? _controller.Readiness.ClientIdentity : null;

    // WriteLeaseStore 上限为两小时；到期后由 UpdateWriteUi 的定时刷新自动续期。
    private static readonly TimeSpan AutoApplyLeaseDuration = TimeSpan.FromHours(2);

    private async void AutoApply_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingSettings || _suppressAutoApplyUi) return;
        if (ProjectPicker.SelectedItem is not ProjectRecord project) return;
        project.AutoApplyOptOut = AutoApplyToggle.IsChecked != true;
        if (project.AutoApplyOptOut) _writeService.RevokeAutoApply(project.Id);
        UpdateWriteUi();
        try { await _store.SaveAsync(_settings); }
        catch (Exception error) { await _logger.WriteAsync("error", $"保存 YOLO 设置失败: {error.Message}"); }
    }

    /// <summary>YOLO 默认开启：为每个已授权网页读取、未单独关闭的项目，给当前已验证主体授予或续期写入授权。</summary>
    private void EnsureDefaultAutoApply(string clientId)
    {
        foreach (var project in _settings.Projects.Where(p => p.AllowWebRead && !p.AutoApplyOptOut && Directory.Exists(p.Path)))
        {
            if (_writeService.GetAutoApplyLease(project.Id, _connectionProfile.Id, clientId) is not null) continue;
            try { _writeService.GrantAutoApply(project.Id, _connectionProfile.Id, clientId, AutoApplyLeaseDuration); }
            catch (Exception error) { _ = _logger.WriteAsync("error", $"YOLO 授权失败（{project.Name}）: {error.Message}"); }
        }
    }

    private void UpdateWriteUi()
    {
        var project = ProjectPicker.SelectedItem as ProjectRecord;
        var clientId = CurrentWriteClient();
        if (!string.IsNullOrWhiteSpace(clientId)) EnsureDefaultAutoApply(clientId);
        var eligible = project is { AllowWebRead: true } && !string.IsNullOrWhiteSpace(clientId);
        var lease = eligible
            ? _writeService.GetAutoApplyLease(project!.Id, _connectionProfile.Id, clientId)
            : null;
        WriteProjectText.Text = project is null ? "项目：未选择" : $"项目：{project.Name}";
        WriteClientText.Text = clientId is null
            ? "连接主体：等待网页调用确认"
            : $"连接主体：{clientId}";
        WriteLeaseStatusText.Text = clientId is null
            ? "先在 ChatGPT 中完成一次项目读取；验证后网页修改按下方模式处理。"
            : lease is not null
                ? "YOLO 写入模式已开启。"
                : "本机确认模式：网页可提交修改预览，文件修改与恢复需逐次确认。";

        _suppressAutoApplyUi = true;
        AutoApplyToggle.IsEnabled = project is { AllowWebRead: true };
        AutoApplyToggle.IsChecked = project is { AllowWebRead: true, AutoApplyOptOut: false };
        AutoApplyScopeText.Visibility = _connectionProfile.Provider == TunnelProvider.OpenAiSecureTunnel
            ? Visibility.Visible : Visibility.Collapsed;
        AutoApplyStatusText.Text = project is null
            ? "请选择项目。"
            : !project.AllowWebRead
                ? "当前项目未授权网页读取，不能开启。"
                : project.AutoApplyOptOut
                    ? "此项目已关闭 YOLO，网页修改需在本机确认。"
                    : clientId is null
                        ? "网页连接验证后自动生效。"
                        : "已生效，网页修改直接应用。";
        _suppressAutoApplyUi = false;
    }

    private void ChangeJournal_Changed(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(RefreshChanges);

    private void RefreshChanges_Click(object sender, RoutedEventArgs e) => RefreshChanges();

    private void RefreshChanges()
    {
        var selectedChange = ChangeList.SelectedItem as ChangeRecord;
        var projectId = (ProjectPicker.SelectedItem as ProjectRecord)?.Id;
        _changes.Clear();
        if (projectId is not null)
            foreach (var change in _changeJournal.List(projectId).Where(ShouldDisplayChange)) _changes.Add(change);
        ChangeList.SelectedItem = selectedChange is null ? _changes.FirstOrDefault()
            : _changes.FirstOrDefault(change => change.ChangeId == selectedChange.ChangeId) ?? _changes.FirstOrDefault();
        ShowSelectedChange();
    }

    private void ChangeList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => ShowSelectedChange();

    private void ShowSelectedChange()
    {
        if (ChangeList.SelectedItem is not ChangeRecord change)
        {
            ChangePreviewText.Text = "尚无待处理修改。已完整恢复的记录仍保留在本机恢复资料中。";
            ChangeResultText.Text = string.Empty;
            ApplyChangeButton.IsEnabled = false;
            RejectChangeButton.IsEnabled = false;
            ConfirmDeletionButton.Content = "确认删除";
            ConfirmDeletionButton.IsEnabled = RestoreChangeButton.IsEnabled = false;
            return;
        }
        ChangePreviewText.Text = change.Preview;
        var fileSummary = string.Join("；", change.Files.Select(file =>
            $"{file.Operation}:{file.Path}={file.Status}" + (string.IsNullOrWhiteSpace(file.Error) ? string.Empty : $"（{file.Error}）")));
        var hasDeletion = change.Files.Any(file => file.Operation == ChangeOperationType.Delete);
        var deletionSummary = hasDeletion
            ? change.DeletionConfirmed ? "删除已确认，可在本机应用修改。" : "删除尚未确认。"
            : string.Empty;
        ChangeResultText.Text = $"状态：{change.Status}" + (string.IsNullOrWhiteSpace(change.Error) ? string.Empty : $"。{change.Error}")
            + (string.IsNullOrEmpty(deletionSummary) ? string.Empty : Environment.NewLine + deletionSummary)
            + Environment.NewLine + fileSummary;
        ConfirmDeletionButton.Content = hasDeletion && change.DeletionConfirmed ? "删除已确认" : "确认删除";
        ConfirmDeletionButton.IsEnabled = change.RequiresDeletionConfirmation && change.Status == ChangeStatus.Prepared;
        ApplyChangeButton.IsEnabled = change.Status == ChangeStatus.Prepared && !change.RequiresDeletionConfirmation;
        RejectChangeButton.IsEnabled = change.Status == ChangeStatus.Prepared;
        RestoreChangeButton.IsEnabled = change.Status is ChangeStatus.Applied or ChangeStatus.Partial
            or ChangeStatus.NeedsRecovery or ChangeStatus.RestorePartial;
    }

    private async void ApplyChange_Click(object sender, RoutedEventArgs e)
    {
        if (ChangeList.SelectedItem is not ChangeRecord change) return;
        var answer = System.Windows.MessageBox.Show(this,
            $"应用当前预览中的 {change.Files.Count} 个文件操作？\n\n应用前会再次核对文件状态，并保存恢复资料。",
            "应用修改", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            var applied = await _writeService.ApplyLocallyAsync(change.ChangeId);
            if (applied.Status is ChangeStatus.Partial or ChangeStatus.NeedsRecovery or ChangeStatus.Interrupted)
                ShowError("修改未完全应用。", "请查看修改结果，并按需要使用安全恢复。", applied.Error);
            else if (applied.Status != ChangeStatus.Applied)
                ShowError("修改未能应用。", "请查看修改结果后重试。", applied.Error);
            RefreshChanges();
        }
        catch (Exception error) { ShowError("无法应用修改。", "检查文件是否在预览后发生变化。", error.Message); }
    }

    private async void RejectChange_Click(object sender, RoutedEventArgs e)
    {
        if (ChangeList.SelectedItem is not ChangeRecord change) return;
        try
        {
            await _writeService.RejectLocallyAsync(change.ChangeId);
            RefreshChanges();
        }
        catch (Exception error) { ShowError("无法拒绝修改。", "刷新修改记录后重试。", error.Message); }
    }

    private static bool ShouldDisplayChange(ChangeRecord change)
        => change.Status is not (ChangeStatus.Restored or ChangeStatus.Rejected);

    private async void ConfirmDeletion_Click(object sender, RoutedEventArgs e)
    {
        if (ChangeList.SelectedItem is not ChangeRecord change) return;
        var files = string.Join(Environment.NewLine, change.Files
            .Where(file => file.Operation == ChangeOperationType.Delete).Select(file => "• " + file.Path));
        var answer = System.Windows.MessageBox.Show(this,
            $"确认删除以下文件？原始内容将在应用前保存为恢复资料。\n\n{files}",
            "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            await _changeJournal.ConfirmDeletionAsync(change.ChangeId);
            RefreshChanges();
        }
        catch (Exception error) { ShowError("无法确认删除。", "刷新修改记录后重试。", error.Message); }
    }

    private async void RestoreChange_Click(object sender, RoutedEventArgs e)
    {
        if (ChangeList.SelectedItem is not ChangeRecord change) return;
        var answer = System.Windows.MessageBox.Show(this,
            "仅恢复这次修改仍保持提交后版本的文件；后续人工编辑不会被覆盖。继续吗？",
            "安全恢复", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            var restored = await _writeService.RestoreLocallyAsync(change.ChangeId);
            if (restored.Status == ChangeStatus.RestorePartial)
                ShowError("部分文件未恢复。", "保留了检测到的后续人工编辑，请查看修改结果。", restored.Error);
            RefreshChanges();
        }
        catch (Exception error) { ShowError("无法恢复修改。", "查看修改状态和文件占用后重试。", error.Message); }
    }

    private void ShowError(string summary, string action, string? detail)
    {
        ErrorSummaryText.Text = summary;
        ErrorActionText.Text = action;
        ErrorDetailsText.Text = string.IsNullOrWhiteSpace(detail) ? summary : detail;
        ErrorDetailsExpander.IsExpanded = false;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorSummaryText.Text = ErrorActionText.Text = ErrorDetailsText.Text = string.Empty;
        ErrorDetailsExpander.IsExpanded = false;
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        Hide();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (!_exiting && WindowState == WindowState.Minimized) Hide();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task ExitAsync()
    {
        await _logger.WriteAsync("info", "user requested application exit");
        _exiting = true;
        _autoApplyTimer.Stop();
        _writeService.RevokeAllAutoApply();
        await _controller.DisposeAsync();
        var persistedProfile = _store.LoadConnection();
        if (!ConnectionProfileConfiguration.RuntimeConfigurationEquals(persistedProfile, _connectionProfile))
            _connectionProfile = persistedProfile;
        await _store.SaveConnectionAsync(_connectionProfile);
        StopCollaborationPolling();
        _tray.Visible = false;
        _tray.Dispose();
        foreach (var statusIcon in _connectionIcons.Values) statusIcon.Dispose();
        _connectionIcons.Clear();
        _trayIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
