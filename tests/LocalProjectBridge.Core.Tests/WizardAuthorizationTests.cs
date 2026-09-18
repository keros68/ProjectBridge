using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using LocalProjectBridge.Core.Adapters;
using LocalProjectBridge.Core.Gateway;
using LocalProjectBridge.Core.Sessions;
using LocalProjectBridge.Core.Collaboration;
using LocalProjectBridge.Core.Security;

namespace LocalProjectBridge.Core.Tests;

public sealed class WizardAuthorizationTests
{
    [Fact]
    public async Task OAuthSuccess_AutoAdoptsAndCloseRacePreservesConnection_UnpairedCancelCleansUp()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Root!.Elements().First();
            var ns = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
            var dictionary = new XElement(ns + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), resources.Elements());
            app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
            Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
            {
                try
                {
                    foreach (var scenario in new[] { "auto", "close-race", "unpaired", "cancel-start", "failed-adoption" })
                        await CheckAsync(scenario);
                    await CheckSavedKeyPlaceholderAsync();
                    await CheckEmptyProjectFixedConnectionAsync();
                    await CheckWebGuideAsync();
                    await CheckMain(Path.GetTempPath());
                    completion.SetResult();
                }
                catch (Exception error) { completion.SetException(error); }
                finally { Dispatcher.ExitAllFrames(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }

    private static async Task CheckAsync(string scenario)
    {
        var root = Directory.CreateTempSubdirectory("lpb-wizard-test-").FullName;
        var state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        var project = new ProjectRecord { Name = "A", Path = Directory.CreateDirectory(Path.Combine(root, "A")).FullName };
        var logger = new RedactingLogger(root);
        var runner = new CommandRunner(logger);
        var discovery = new RuntimeDiscovery();
        var node = discovery.Discover().Node!;
        var fake = Path.Combine(root, "fake.cjs");
        await File.WriteAllTextAsync(fake, FakeCli);
        await File.WriteAllTextAsync(Path.Combine(state, "mode.txt"), scenario == "cancel-start" ? "wait" : "success");
        await using var adapter = new C2cAdapter(runner, stateDirectory: state, runtimePaths: new(node, null, null, null, null, fake, node));
        await using var controller = new SessionController([adapter], logger, root);
        var settings = new AppSettings();
        var store = new RegistryStore(Path.Combine(root, "app"));
        var wizard = new SetupWizardWindow(store, new BackendInstallerService(runner, store, discovery), adapter, settings, async (p, token) =>
        {
            if (scenario == "failed-adoption") throw new InvalidOperationException("test handoff failure");
            Assert.True(await controller.AdoptSetupConnectionAsync(p, adapter.ConnectionWorkspace, adapter, token));
        }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowInTaskbar = false };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        try
        {
            wizard.Show();
            Assert.Null(wizard.FindName("ProjectPath"));
            Assert.Null(wizard.FindName("ProjectName"));
            var runtimeKeyInput = Assert.IsType<System.Windows.Controls.PasswordBox>(wizard.FindName("RuntimeKeyInput"));
            var runtimeKeyStatus = Assert.IsType<System.Windows.Controls.TextBlock>(wizard.FindName("RuntimeKeyStatus"));
            Assert.Empty(runtimeKeyInput.Password);
            Assert.Contains("尚未保存", runtimeKeyStatus.Text);
            if (scenario == "auto")
            {
                var secureProvider = Assert.IsType<System.Windows.Controls.RadioButton>(wizard.FindName("SecureProvider"));
                var quickProvider = Assert.IsType<System.Windows.Controls.RadioButton>(wizard.FindName("QuickProvider"));
                secureProvider.IsChecked = true;
                wizard.UpdateLayout();
                Assert.False(quickProvider.IsChecked == true, "connection providers are both selected");
                Assert.Equal(Visibility.Visible,
                    Assert.IsType<System.Windows.Controls.Border>(wizard.FindName("SecureTunnelPanel")).Visibility);
                var setupScroller = Assert.IsType<System.Windows.Controls.ScrollViewer>(wizard.FindName("SetupScrollViewer"));
                wizard.Width = 820;
                wizard.Height = 600;
                wizard.UpdateLayout();
                var visibleElements = new FrameworkElement[] { runtimeKeyInput };
                foreach (var element in visibleElements)
                {
                    var top = element.TransformToAncestor(wizard).Transform(new Point()).Y;
                    Assert.True(top >= 0 && top + element.ActualHeight <= wizard.ActualHeight + 1,
                        $"{element.Name} is outside the default setup window");
                }
                Assert.True(setupScroller.ScrollableHeight <= 1,
                    $"default fixed-tunnel setup still requires scrolling: {setupScroller.ScrollableHeight}");
                Assert.Contains("tunnel_", Assert.IsType<System.Windows.Controls.TextBlock>(wizard.FindName("TunnelIdHelp")).Text);
                Assert.Contains("key_", Assert.IsType<System.Windows.Controls.TextBlock>(wizard.FindName("RuntimeKeyHelp")).Text);
                SaveRender(wizard, "setup-fixed-runtime-key-150dpi.png", 1.5);
                setupScroller.ScrollToHome();
                quickProvider.IsChecked = true;
            }
            Func<CancellationToken, Task> start = ct => (Task)Invoke(wizard, "StartSetupConnectionAsync", ct)!;
            var starting = (Task)Invoke(wizard, "RunOperationAsync", start)!;
            var runtime = Path.Combine(state, "test-runtime.json");
            while (!File.Exists(runtime)) await Task.Delay(30, timeout.Token);
            if (scenario != "cancel-start")
            {
                await starting;
                Invoke(wizard, "ShowStep", 2);
            }
            var before = await File.ReadAllTextAsync(runtime);
            var url = adapter.ObservedMcpUrl;
            if (scenario is "auto" or "close-race" or "failed-adoption")
                await File.WriteAllTextAsync(Path.Combine(state, "authorized"), "true");
            if (scenario == "auto")
            {
                while (!wizard.ConnectionTransferred) await Task.Delay(50, timeout.Token);
                Assert.True(settings.SetupCompleted);
                Assert.Empty(settings.Projects);
                Assert.Null(settings.SelectedProjectId);
                Assert.Empty(store.Load().Projects);
            }
            // No manual local completion or checkbox. The close handler must observe the grant.
            wizard.Close();
            if (scenario == "failed-adoption")
            {
                while (Field(wizard, "_closeCleanup") is not null) await Task.Delay(50, timeout.Token);
                Assert.False(wizard.ConnectionTransferred);
                Assert.True(wizard.IsVisible);
                Assert.False(File.Exists(Path.Combine(state, "revoked.txt")));
                Invoke(wizard, "CancelConnection_Click", wizard, new RoutedEventArgs());
            }
            while (wizard.IsVisible) await Task.Delay(50, timeout.Token);
            try { await starting; } catch (OperationCanceledException) { }
            if (scenario is "auto" or "close-race")
            {
                Assert.True(wizard.ConnectionTransferred);
                Assert.Equal(SessionState.Connected, controller.State);
                Assert.Equal(before, await File.ReadAllTextAsync(runtime));
                Assert.Equal(url, adapter.ObservedMcpUrl);
                Assert.False(File.Exists(Path.Combine(state, "revoked.txt")));
                Assert.True(await adapter.HasAuthorizationAsync(project.Path));
                await controller.DisconnectAsync();
            }
            if (scenario == "failed-adoption") Assert.True(File.Exists(Path.Combine(state, "revoked.txt")));
            else Assert.False(File.Exists(Path.Combine(state, "revoked.txt")));
            using var status = JsonDocument.Parse(before);
            foreach (var key in new[] { "pid", "childPid" })
            {
                try { using var process = Process.GetProcessById(status.RootElement.GetProperty(key).GetInt32()); Assert.True(process.HasExited); }
                catch (ArgumentException) { }
            }
        }
        finally
        {
            // Ensure a failed assertion also cleans the owned process tree and window.
            Invoke(wizard, "CancelConnection_Click", wizard, new RoutedEventArgs());
            await adapter.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    private static async Task CheckMain(string root)
    {
        var mainRoot = Directory.CreateDirectory(Path.Combine(root, "main-" + Guid.NewGuid().ToString("N"))).FullName;
        var longRoot = Directory.CreateDirectory(Path.Combine(
            mainRoot,
            "very-long-project-name-" + new string('x', 64),
            "nested-source-folder-" + new string('y', 48))).FullName;
        var a = new ProjectRecord { Name = "AIdraw 长名称项目", Path = longRoot, AllowCodexTasks = true, LastAccessedAt = DateTimeOffset.Now };
        var b = new ProjectRecord { Name = "coffeecli", Path = Directory.CreateDirectory(Path.Combine(mainRoot, "B")).FullName };
        var settings = new AppSettings
        {
            SetupCompleted = true,
            ConnectionVersion = 3,
            SelectedProjectId = a.Id,
            SelectedProjectPath = a.Path,
            RestoreReadOnlyConnection = false
        };
        settings.Projects.Add(a);
        settings.Projects.Add(b);
        for (var index = 2; index < 10; index++)
            settings.Projects.Add(new ProjectRecord
            {
                Name = $"项目 {index + 1}",
                Path = Directory.CreateDirectory(Path.Combine(mainRoot, $"P{index + 1}")).FullName
            });

        // 窗口的设置、日志、协作记录和修改备份全部落在临时目录，不触碰真实用户数据。
        settings.CheckForUpdates = false;
        var main = new MainWindow(false, new RegistryStore(mainRoot));
        var type = typeof(MainWindow);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await ((SessionController)type.GetField("_controller", flags)!.GetValue(main)!).DisposeAsync();
        var collaboration = (CollaborationStore)type.GetField("_collaborationStore", flags)!.GetValue(main)!;
        type.GetField("_settings", flags)!.SetValue(main, settings);
        type.GetField("_connectionProfile", flags)!.SetValue(main, new ConnectionProfile
        {
            Name = "P1 visual connection",
            Provider = TunnelProvider.CloudflareQuickTunnel
        });
        var fake = new UiFake();
        var controller = new SessionController([fake], new RedactingLogger(mainRoot), mainRoot);
        type.GetField("_controller", flags)!.SetValue(main, controller);
        controller.StateChanged += (_, e) => type.GetMethod("Controller_StateChanged", flags)!.Invoke(main, [controller, e]);
        type.GetMethod("RefreshAuthorizedProjects", flags)!.Invoke(main, null);
        type.GetMethod("ReloadProjects", flags)!.Invoke(main, null);
        var tray = (System.Windows.Forms.NotifyIcon)type.GetField("_tray", flags)!.GetValue(main)!;
        tray.Visible = false;

        await (Task)type.GetMethod("RestoreReadOnlyConnectionAsync", flags)!.Invoke(main, null)!;
        Assert.Equal(CapabilityFlags.WebRead, fake.LastPolicy!.EnabledCapabilities);
        Assert.Equal(1, fake.Starts);

        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = -20000;
        main.Top = -20000;
        main.ShowInTaskbar = false;
        main.Show();
        await Task.Delay(50);
        CheckShellStatus(main, tray);
        main.WindowState = WindowState.Minimized;
        await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.False(main.IsVisible, "minimized main window did not move to the tray");
        Assert.Equal(SessionState.Connected, controller.State);
        Assert.Equal(1, fake.Starts);
        Assert.Equal(0, fake.Stops);

        Invoke(main, "ShowFromTray");
        await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(main.IsVisible, "tray restore did not show the main window");
        Assert.Equal(WindowState.Normal, main.WindowState);
        Assert.Same(controller, Field(main, "_controller"));
        Assert.Equal(1, fake.Starts);
        Assert.Equal(0, fake.Stops);

        await CheckSettingsIsolationAsync(main, controller, fake);
        var guideController = Field(main, "_controller");
        Invoke(main, "ShowWebGuide_Click", main, new RoutedEventArgs());
        Assert.Equal(1, ((System.Windows.Controls.TabControl)main.FindName("MainTabs")).SelectedIndex);
        Assert.Same(guideController, Field(main, "_controller"));
        Assert.True(((System.Windows.Controls.Expander)((WebConnectionGuide)main.FindName("ConnectionWebGuide")).FindName("FirstTimeSteps")).IsExpanded);
        Assert.Equal(1, fake.Starts);
        Assert.Equal(0, fake.Stops);
        main.UpdateLayout();
        SaveRender(main, "main-web-verification-guide.png", 1.0);
        Assert.True(main.ActualWidth >= 1050, "default main window is too narrow for the edit page");
        Assert.True(main.ActualHeight >= 820, "default main window is too short for the edit page");
        Assert.True(main.ActualWidth <= SystemParameters.WorkArea.Width + 1,
            "default window width exceeds the available work area");
        Assert.True(main.ActualHeight <= SystemParameters.WorkArea.Height + 1,
            "default window height exceeds the available work area");
        ((System.Windows.Controls.TabControl)main.FindName("MainTabs")).SelectedIndex = 3;
        main.UpdateLayout();
        var defaultEditScroller = (System.Windows.Controls.ScrollViewer)main.FindName("EditScrollViewer");
        Assert.True(defaultEditScroller.ScrollableHeight <= 1,
            $"default edit page still requires vertical scrolling: {defaultEditScroller.ScrollableHeight}");
        SaveRender(main, "main-edit-default-size.png", 1.0);
        Assert.False((bool)type.GetMethod("ShouldDisplayChange", flags | BindingFlags.Static)!.Invoke(null,
            [new ChangeRecord { ClientId = "test", ProjectRoot = mainRoot, Preview = "restored", Status = ChangeStatus.Restored }])!);
        Assert.True((bool)type.GetMethod("ShouldDisplayChange", flags | BindingFlags.Static)!.Invoke(null,
            [new ChangeRecord { ClientId = "test", ProjectRoot = mainRoot, Preview = "partial", Status = ChangeStatus.RestorePartial }])!);
        ((System.Windows.Controls.TabControl)main.FindName("MainTabs")).SelectedIndex = 0;

        var picker = (System.Windows.Controls.ListBox)main.FindName("ProjectPicker");
        Assert.Equal(10, picker.Items.Count);
        Assert.True(picker.IsEnabled, "project picker disabled while connected");
        Assert.True(((System.Windows.Controls.Button)main.FindName("RemoveProjectButton")).IsEnabled,
            "project removal disabled while connected");
        var delegateBox = (System.Windows.Controls.CheckBox)main.FindName("DelegateTasks");
        Assert.False(delegateBox.IsChecked == true, "read-only restore enabled saved Codex permission");
        Assert.Contains("只读恢复未重新授权", ((System.Windows.Controls.TextBlock)main.FindName("CapabilityAvailabilityText")).Text);

        var registry = (ProjectAuthorizationRegistry)type.GetField("_sharedProjects", flags)!.GetValue(main)!;
        var tools = MultiProjectTools.Create(registry, new CommandRunner(new RedactingLogger(mainRoot)));
        var projectPermissions = (SessionProjectPermissions)type.GetField("_projectPermissions", flags)!.GetValue(main)!;
        var taskRuntime = new TrackingTaskRuntime(a.Id);
        var permissionAdapter = new C2cAdapter(
            new CommandRunner(new RedactingLogger(mainRoot)), sharedProjects: registry);
        typeof(C2cAdapter).GetField("_taskTools", flags)!.SetValue(
            permissionAdapter, new SharedCodexTaskTools(registry, taskRuntime));
        type.GetField("_c2c", flags)!.SetValue(main, permissionAdapter);

        Assert.True(a.AllowCodexTasks, "stored task preference changed during read-only restore");
        Assert.False(projectPermissions.IsCodexTaskEnabled(a, TunnelProvider.CloudflareQuickTunnel));

        delegateBox.IsChecked = true;
        await Task.Delay(50);
        Assert.True(a.AllowCodexTasks);
        Assert.True(delegateBox.IsChecked == true);
        Assert.Equal(1, fake.Starts);
        await taskRuntime.GetBridgeAsync(a, CancellationToken.None);
        Assert.True(taskRuntime.Active);

        delegateBox.IsChecked = false;
        await Task.Delay(50);
        Assert.False(projectPermissions.IsCodexTaskEnabled(a, TunnelProvider.CloudflareQuickTunnel));
        Assert.False(taskRuntime.Active);
        Assert.Equal(1, fake.Starts);

        delegateBox.IsChecked = true;
        await Task.Delay(50);

        picker.SelectedItem = b;
        await (Task)type.GetMethod("SaveSettingsAsync", flags)!.Invoke(main, null)!;
        Assert.True(controller.State == SessionState.Connected && fake.Starts == 1 && fake.Stops == 0,
            "selection restarted or stopped the connection");
        Assert.Equal(b.Id, settings.SelectedProjectId);
        ((System.Windows.Controls.CheckBox)main.FindName("ReviewOnly")).IsChecked = false;
        await Task.Delay(50);
        Assert.True(!b.AllowWebRead && a.AllowWebRead, "permission change affected another project");

        var denied = await tools.Single(t => t.Name == "project_info").Invoke(
            new System.Text.Json.Nodes.JsonObject { ["project_id"] = b.Id.ToString() }, CancellationToken.None);
        var allowed = await tools.Single(t => t.Name == "project_info").Invoke(
            new System.Text.Json.Nodes.JsonObject { ["project_id"] = a.Id.ToString() }, CancellationToken.None);
        Assert.True(denied["isError"]!.GetValue<bool>(), "UI revocation not applied to selected project");
        Assert.False(allowed["isError"]!.GetValue<bool>(), "revoking B affected A");
        await Task.Delay(50);
        Assert.NotNull(a.LastAccessedAt);

        picker.SelectedItem = a;
        Assert.DoesNotContain("尚无", ((System.Windows.Controls.TextBlock)main.FindName("ProjectRecentAccessText")).Text);
        main.Width = 960;
        main.Height = 720;
        main.UpdateLayout();
        SaveRender(main, "main-projects-10-125dpi.png", 1.25);
        SaveRender(main, "main-projects-10-150dpi.png", 1.5);
        SaveRender(main, "main-projects-10-200dpi.png", 2.0);

        type.GetField("_connectionProfile", flags)!.SetValue(main, new ConnectionProfile
        {
            Name = "fixed tunnel",
            Provider = TunnelProvider.OpenAiSecureTunnel,
            TunnelId = "tunnel_visual"
        });
        type.GetMethod("RefreshAuthorizedProjects", flags)!.Invoke(main, null);
        type.GetMethod("SetState", flags)!.Invoke(main, [controller.State]);
        Assert.Equal(Visibility.Visible, delegateBox.Visibility);
        var listed = await tools.Single(t => t.Name == "list_projects").Invoke(
            new System.Text.Json.Nodes.JsonObject(), CancellationToken.None);
        Assert.Contains("WebDelegateCodex", listed.ToJsonString());
        picker.SelectedItem = a;
        var writableBox = (System.Windows.Controls.CheckBox)main.FindName("DelegateWritableTasks");
        Assert.False(writableBox.IsChecked == true);
        writableBox.IsChecked = true;
        await Task.Delay(50);
        Assert.True(projectPermissions.IsCodexTaskWriteEnabled(a, TunnelProvider.OpenAiSecureTunnel));
        listed = await tools.Single(t => t.Name == "list_projects").Invoke(new System.Text.Json.Nodes.JsonObject(), CancellationToken.None);
        Assert.Contains("workspace-write", listed.ToJsonString());
        main.UpdateLayout();
        SaveRender(main, "main-writable-tasks.png", 1.0);
        delegateBox.IsChecked = false;
        await Task.Delay(50);
        delegateBox.IsChecked = true;
        await Task.Delay(50);
        Assert.True(a.AllowCodexWrite);
        Assert.False(writableBox.IsChecked == true);
        Assert.False(projectPermissions.IsCodexTaskWriteEnabled(a, TunnelProvider.OpenAiSecureTunnel));

        ((System.Windows.Controls.TabControl)main.FindName("MainTabs")).SelectedIndex = 1;
        type.GetMethod("ShowError", flags)!.Invoke(main,
        [
            "无法建立外部连接。",
            "检查网络和连接设置后重试。",
            string.Join(Environment.NewLine, Enumerable.Repeat(
                "TimeoutException: 连接目标返回了很长的诊断信息，详细内容应留在折叠区。", 12))
        ]);
        ((System.Windows.Controls.Expander)main.FindName("ErrorDetailsExpander")).IsExpanded = true;
        main.Width = 640;
        main.Height = 420;
        main.UpdateLayout();
        var connectButton = (System.Windows.Controls.Button)main.FindName("ConnectButton");
        var buttonTop = connectButton.TransformToAncestor(main).Transform(new Point()).Y;
        Assert.True(buttonTop + connectButton.ActualHeight <= main.ActualHeight + 1,
            "fixed action bar is outside the small window");
        SaveRender(main, "main-small-long-error.png", 1.0);

        type.GetField("_initializingSettings", flags)!.SetValue(main, true);
        ((System.Windows.Controls.CheckBox)main.FindName("StartWithWindowsSetting")).IsChecked = false;
        ((System.Windows.Controls.CheckBox)main.FindName("RestoreReadOnlyConnectionSetting")).IsChecked = false;
        type.GetField("_initializingSettings", flags)!.SetValue(main, false);
        ((System.Windows.Controls.TabControl)main.FindName("MainTabs")).SelectedIndex = 2;
        main.Width = 960;
        main.Height = 720;
        main.UpdateLayout();
        SaveRender(main, "main-settings-independent.png", 1.0);

        ((System.Windows.Controls.TabControl)main.FindName("MainTabs")).SelectedIndex = 3;
        main.Width = 960;
        main.Height = 720;
        main.UpdateLayout();
        var autoApplyToggle = (System.Windows.Controls.CheckBox)main.FindName("AutoApplyToggle");
        var autoApplyStatus = (System.Windows.Controls.TextBlock)main.FindName("AutoApplyStatusText");
        var autoApplyScope = (System.Windows.Controls.TextBlock)main.FindName("AutoApplyScopeText");
        Assert.Equal(Visibility.Visible, autoApplyScope.Visibility);
        Assert.Contains("固定 Tunnel", autoApplyScope.Text);
        Assert.True(autoApplyToggle.IsChecked == true, "YOLO should default to on for a web-readable project");
        Assert.Contains("验证后自动生效", autoApplyStatus.Text);
        var applyWrite = (System.Windows.Controls.Button)main.FindName("ApplyChangeButton");
        var restoreWrite = (System.Windows.Controls.Button)main.FindName("RestoreChangeButton");
        Assert.False(applyWrite.IsEnabled, "apply enabled without a prepared change");
        Assert.Contains("等待网页调用确认",
            ((System.Windows.Controls.TextBlock)main.FindName("WriteClientText")).Text);
        Assert.Contains("先在 ChatGPT 中完成一次项目读取",
            ((System.Windows.Controls.TextBlock)main.FindName("WriteLeaseStatusText")).Text);

        var verifiedClient = new ConnectionReadiness(true, true, true, DateTimeOffset.Now, "web-ui-test");
        typeof(SessionController).GetProperty(nameof(SessionController.Readiness))!.SetValue(controller, verifiedClient);
        type.GetMethod("ApplyReadinessToUi", flags)!.Invoke(main, [verifiedClient]);
        Assert.Contains("已连接，网页已验证", tray.Text);
        var writeService = (ProjectWriteService)type.GetField("_writeService", flags)!.GetValue(main)!;
        var activeConnection = (ConnectionProfile)type.GetField("_connectionProfile", flags)!.GetValue(main)!;
        Assert.Null(writeService.GetAutoApplyLease(a.Id, activeConnection.Id, "web-ui-test"));
        type.GetMethod("UpdateWriteUi", flags)!.Invoke(main, null);
        Assert.True(autoApplyToggle.IsEnabled);
        Assert.True(autoApplyToggle.IsChecked == true);
        Assert.Contains("已生效", autoApplyStatus.Text);
        var aLease = writeService.GetAutoApplyLease(a.Id, activeConnection.Id, "web-ui-test");
        Assert.NotNull(aLease);
        Assert.True(aLease.AllowDeletion);
        Assert.InRange((aLease.ExpiresAt - aLease.GrantedAt).TotalMinutes, 119.9, 120.1);
        Assert.Equal(1, fake.Starts);
        Assert.Equal(0, fake.Stops);

        picker.SelectedItem = b;
        await Task.Delay(50);
        Assert.False(autoApplyToggle.IsEnabled, "unauthorized project can enable YOLO");
        Assert.False(autoApplyToggle.IsChecked == true);
        Assert.Null(writeService.GetAutoApplyLease(b.Id, activeConnection.Id, "web-ui-test"));
        Assert.NotNull(writeService.GetAutoApplyLease(a.Id, activeConnection.Id, "web-ui-test"));

        ((System.Windows.Controls.CheckBox)main.FindName("ReviewOnly")).IsChecked = true;
        await Task.Delay(50);
        type.GetMethod("UpdateWriteUi", flags)!.Invoke(main, null);
        Assert.True(autoApplyToggle.IsEnabled);
        Assert.True(autoApplyToggle.IsChecked == true);
        Assert.NotNull(writeService.GetAutoApplyLease(b.Id, activeConnection.Id, "web-ui-test"));

        picker.SelectedItem = a;
        await Task.Delay(50);
        Assert.True(autoApplyToggle.IsChecked == true, "switching projects lost A's YOLO state");
        autoApplyToggle.IsChecked = false;
        Assert.True(a.AutoApplyOptOut);
        Assert.Null(writeService.GetAutoApplyLease(a.Id, activeConnection.Id, "web-ui-test"));
        type.GetMethod("UpdateWriteUi", flags)!.Invoke(main, null);
        Assert.Null(writeService.GetAutoApplyLease(a.Id, activeConnection.Id, "web-ui-test"));
        Assert.Contains("已关闭 YOLO", autoApplyStatus.Text);
        picker.SelectedItem = b;
        await Task.Delay(50);
        Assert.True(autoApplyToggle.IsChecked == true, "disabling A revoked B's YOLO state");
        Assert.NotNull(writeService.GetAutoApplyLease(b.Id, activeConnection.Id, "web-ui-test"));
        Assert.True(applyWrite.IsVisible && restoreWrite.IsVisible, "P2 local apply or recovery entry is missing");
        var applyTop = applyWrite.TransformToAncestor(main).Transform(new Point()).Y;
        Assert.True(applyTop >= 0 && applyTop + applyWrite.ActualHeight <= main.ActualHeight + 1,
            "local apply button is outside the default edit view");
        SaveRender(main, "main-edit-p2.png", 1.0);

        main.Width = 720;
        main.Height = 540;
        ((System.Windows.Controls.ScrollViewer)main.FindName("EditScrollViewer")).ScrollToHome();
        main.UpdateLayout();
        SaveRender(main, "main-edit-yolo-small-150dpi.png", 1.5);
        restoreWrite.BringIntoView();
        main.UpdateLayout();
        var actionTop = restoreWrite.TransformToAncestor(main).Transform(new Point()).Y;
        Assert.True(actionTop >= 0 && actionTop + restoreWrite.ActualHeight <= main.ActualHeight + 1,
            "P2 recovery entry cannot be reached in the small window");
        SaveRender(main, "main-edit-p2-small-results-150dpi.png", 1.5);

        picker.SelectedItem = a;
        await Task.Delay(50);

        ((System.Windows.Controls.TabControl)main.FindName("MainTabs")).SelectedIndex = 4;
        var collaborationBox = (System.Windows.Controls.CheckBox)main.FindName("CollaborationPermission");
        collaborationBox.IsChecked = true;
        await Task.Delay(50);
        var currentConnection = (ConnectionProfile)type.GetField("_connectionProfile", flags)!.GetValue(main)!;
        var request = collaboration.Submit(a, currentConnection.Id, "请检查这个测试项目的设计思路。",
            "codex://threads/ui-test", kind: CollaborationKind.Plan);
        type.GetMethod("RefreshCollaborationRequests", flags)!.Invoke(main, [false, false]);
        Assert.Equal("请检查这个测试项目的设计思路。", request.Question);
        Assert.Null(main.FindName("CollaborationQuestionText"));
        var requestList = (System.Windows.Controls.ListBox)main.FindName("CollaborationRequestList");
        var selected = requestList.SelectedItem;
        type.GetMethod("RefreshCollaborationRequests", flags)!.Invoke(main, [false, false]);
        Assert.Same(selected, requestList.SelectedItem);
        main.Width = 960;
        main.Height = 720;
        main.UpdateLayout();
        SaveRender(main, "main-collaboration.png", 1.0);
        main.Width = 640;
        main.Height = 420;
        var cancelRequest = (System.Windows.Controls.Button)main.FindName("CancelCollaborationRequestButton");
        cancelRequest.BringIntoView();
        main.UpdateLayout();
        var cancelTop = cancelRequest.TransformToAncestor(main).Transform(new Point()).Y;
        Assert.True(cancelTop >= 0 && cancelTop + cancelRequest.ActualHeight <= main.ActualHeight + 1);
        SaveRender(main, "main-collaboration-small.png", 1.0);
        collaborationBox.IsChecked = false;
        await Task.Delay(50);
        Assert.Equal(CollaborationStatus.Cancelled, collaboration.Get(request.RequestId)!.Status);
        collaborationBox.IsChecked = true;
        await Task.Delay(50);
        Assert.Equal(CollaborationStatus.Cancelled, collaboration.Get(request.RequestId)!.Status);

        await (Task)type.GetMethod("DisconnectAsync", flags)!.Invoke(main, null)!;
        Assert.Equal(1, fake.Stops);
        Assert.Contains("未连接", tray.Text);
        type.GetMethod("ApplyReadinessToUi", flags)!.Invoke(main, [verifiedClient]);
        Assert.Contains("未连接", tray.Text); // Historical calls cannot turn a stopped tray blue.
        Assert.Null(writeService.GetAutoApplyLease(b.Id, activeConnection.Id, "web-ui-test"));

        await (Task)type.GetMethod("RestoreReadOnlyConnectionAsync", flags)!.Invoke(main, null)!;
        Assert.Equal(2, fake.Starts);
        typeof(SessionController).GetProperty(nameof(SessionController.Readiness))!.SetValue(controller, verifiedClient);
        picker.SelectedItem = b;
        type.GetMethod("UpdateWriteUi", flags)!.Invoke(main, null);
        Assert.True(autoApplyToggle.IsChecked == true);
        // 默认开启：重新连接并验证后重新授予；已关闭的项目保持关闭。
        Assert.NotNull(writeService.GetAutoApplyLease(b.Id, activeConnection.Id, "web-ui-test"));
        Assert.Null(writeService.GetAutoApplyLease(a.Id, activeConnection.Id, "web-ui-test"));
        await (Task)type.GetMethod("DisconnectAsync", flags)!.Invoke(main, null)!;
        Assert.Equal(2, fake.Stops);
        await permissionAdapter.DisposeAsync();
        await controller.DisposeAsync();
        type.GetField("_exiting", flags)!.SetValue(main, true);
        main.Close();
        tray.Dispose();
        ((System.Drawing.Icon)type.GetField("_trayIcon", flags)!.GetValue(main)!).Dispose();
    }

    private static void CheckShellStatus(MainWindow main, System.Windows.Forms.NotifyIcon tray)
    {
        var method = typeof(MainWindow).GetMethod("UpdateShellStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ready = new ConnectionReadiness(true, true, false, null);
        var cases = new[]
        {
            (SessionState.Disconnected, "#8A919E", "未连接"),
            (SessionState.Connected, "#2563EB", "已连接，等待网页验证"),
            (SessionState.Starting, "#D97706", "正在连接或检查"),
            (SessionState.NeedsAttention, "#B42318", "连接异常，需要处理")
        };
        var preview = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal, Width = 640, Height = 100,
            Background = Brushes.White
        };
        foreach (var (state, color, description) in cases)
        {
            method.Invoke(main, [state, ready]);
            Assert.Equal("ProjectBridge · " + description, tray.Text);
            Assert.Equal(tray.Text, main.TaskbarItemInfo.Description);
            var drawing = Assert.IsType<GeometryDrawing>(Assert.IsType<DrawingImage>(main.TaskbarItemInfo.Overlay).Drawing);
            Assert.Equal((Color)ColorConverter.ConvertFromString(color), Assert.IsType<SolidColorBrush>(drawing.Brush).Color);
            var previous = tray.Icon;
            method.Invoke(main, [state, ready]);
            Assert.Same(previous, tray.Icon);
            var panel = new System.Windows.Controls.StackPanel { Width = 160 };
            panel.Children.Add(new System.Windows.Controls.Image { Source = main.Icon, Width = 40, Height = 40 });
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = description, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            preview.Children.Add(panel);
        }
        preview.Measure(new Size(640, 100));
        preview.Arrange(new Rect(0, 0, 640, 100));
        SaveRender(preview, "connection-shell-icons.png", 1.5);
        method.Invoke(main, [SessionState.Connected, ready]);
    }

    private static async Task CheckSettingsIsolationAsync(MainWindow main, SessionController controller, UiFake fake)
    {
        var stateProperty = typeof(SessionController).GetProperty(nameof(SessionController.State))!;
        foreach (var state in new[] { SessionState.Connected, SessionState.NeedsAttention, SessionState.Disconnected })
        {
            stateProperty.SetValue(controller, state);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += async (_, _) =>
            {
                var window = Application.Current.Windows.OfType<SetupWizardWindow>().FirstOrDefault();
                if (window is null) return;
                timer.Stop();
                try
                {
                    Assert.True((bool)Field(window, "_configurationOnly")!, "Settings entered connection setup instead of configuration-only mode");
                    Assert.Null(window.FindName("ProjectPath"));
                    Assert.Same(controller, Field(main, "_controller"));
                    Assert.Equal(0, fake.Stops);
                    Invoke(window, "SaveConnectionConfiguration_Click", window, new RoutedEventArgs());
                    for (var i = 0; window.SavedConnectionProfile is null && i < 100; i++) await Task.Delay(10);
                    Assert.NotNull(window.SavedConnectionProfile);
                    completed.TrySetResult();
                }
                catch (Exception error) { completed.TrySetException(error); }
                finally { window.Close(); }
            };
            timer.Start();
            Invoke(main, "OpenSetupWizard_Click", main, new RoutedEventArgs());
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(60);
            Assert.Same(controller, Field(main, "_controller"));
            Assert.Equal(state, controller.State);
            Assert.Equal(0, fake.Stops);
        }
        stateProperty.SetValue(controller, SessionState.Connected);
    }

    private static async Task CheckSavedKeyPlaceholderAsync()
    {
        var root = Directory.CreateTempSubdirectory("lpb-key-display-").FullName;
        var store = new RegistryStore(root);
        var runner = new CommandRunner(new RedactingLogger(root));
        await using var adapter = new C2cAdapter(runner, stateDirectory: Path.Combine(root, "c2c"));
        var credentials = new DisplayCredentialStore();
        var profile = new ConnectionProfile { Provider = TunnelProvider.OpenAiSecureTunnel, TunnelId = "tunnel_display_test" };
        SetupWizardWindow CreateWindow() => new(store, new BackendInstallerService(runner, store, new RuntimeDiscovery()),
            adapter, new AppSettings(), profile, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask,
            configurationOnly: true, credentialStore: credentials)
            { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowInTaskbar = false };
        var window = CreateWindow();
        window.Show();
        var input = (System.Windows.Controls.PasswordBox)window.FindName("RuntimeKeyInput");
        var placeholder = (System.Windows.Controls.TextBlock)window.FindName("RuntimeKeySavedPlaceholder");
        Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
        input.Password = "synthetic-key-for-ui-test";
        Invoke(window, "SaveConnectionConfiguration_Click", window, new RoutedEventArgs());
        for (var i = 0; window.SavedConnectionProfile is null && i < 100; i++) await Task.Delay(10);
        Assert.NotNull(window.SavedConnectionProfile);
        Assert.Empty(input.Password);
        Assert.Equal(Visibility.Visible, placeholder.Visibility);
        Invoke(window, "EnsureDisplayedConfigurationIsSaved");
        input.Password = "replacement-not-saved";
        Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
        input.Clear();
        Assert.Equal(Visibility.Visible, placeholder.Visibility);
        Assert.Equal("synthetic-key-for-ui-test", credentials.Secret);
        window.UpdateLayout();
        SaveRender(window, "settings-saved-key.png", 1.0);
        Assert.Equal("synthetic-key-for-ui-test", credentials.Secret);
        window.Close();
        await Task.Delay(30);
        var reopened = CreateWindow();
        reopened.Show();
        Assert.Empty(((System.Windows.Controls.PasswordBox)reopened.FindName("RuntimeKeyInput")).Password);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)reopened.FindName("RuntimeKeySavedPlaceholder")).Visibility);
        Assert.Equal(0, credentials.Reads);
        reopened.Close();
        await Task.Delay(30);
    }

    private static async Task CheckEmptyProjectFixedConnectionAsync()
    {
        var root = Directory.CreateTempSubdirectory("lpb-empty-setup-").FullName;
        var store = new RegistryStore(root);
        var runner = new CommandRunner(new RedactingLogger(root));
        var keyFile = Path.Combine(root, "synthetic-key.txt");
        await File.WriteAllTextAsync(keyFile, "synthetic-runtime-key");
        var profile = new ConnectionProfile { Provider = TunnelProvider.OpenAiSecureTunnel,
            TunnelId = "tunnel_empty_project_test", RuntimeCredentialReference = "file:" + keyFile };
        var settings = new AppSettings();
        await using var adapter = new C2cAdapter(runner, stateDirectory: Path.Combine(root, "c2c"));
        var starts = 0;
        var window = new SetupWizardWindow(store, new BackendInstallerService(runner, store, new RuntimeDiscovery()),
            adapter, settings, profile, (_, _) => throw new InvalidOperationException("wrong provider"),
            (connection, _) => { Assert.Same(profile, connection); starts++; return Task.CompletedTask; })
            { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowInTaskbar = false };
        try
        {
            window.Show();
            Assert.Null(window.FindName("ProjectPath"));
            await (Task)Invoke(window, "PrepareAsync", CancellationToken.None)!;
            Assert.Equal(1, starts);
            Assert.True(window.ConnectionTransferred);
            Assert.True(settings.SetupCompleted);
            Assert.Empty(settings.Projects);
            Assert.Null(settings.SelectedProjectId);
            Assert.Empty(store.Load().Projects);
            var guide = (WebConnectionGuide)window.FindName("SuccessWebGuide");
            Assert.True(((System.Windows.Controls.Expander)guide.FindName("FirstTimeSteps")).IsExpanded);
            Assert.Contains("tunnel_empty_project_test", ((System.Windows.Controls.TextBox)guide.FindName("EndpointText")).Text);
            window.UpdateLayout();
            SaveRender(window, "wizard-fixed-web-guide.png", 1.0);
        }
        finally { window.Close(); await Task.Delay(30); Directory.Delete(root, true); }
    }

    private static async Task CheckWebGuideAsync()
    {
        string? copied = null;
        var guide = (WebConnectionGuide)typeof(WebConnectionGuide).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(Action<string>)], null)!
            .Invoke([new Action<string>(value => copied = value)]);
        var profile = new ConnectionProfile { Provider = TunnelProvider.OpenAiSecureTunnel, TunnelId = "tunnel_guide_test" };
        guide.Configure(profile, "https://stale.example/mcp", true, false);
        var status = (System.Windows.Controls.TextBlock)guide.FindName("GuideStatus");
        var endpoint = (System.Windows.Controls.TextBox)guide.FindName("EndpointText");
        var instructions = (System.Windows.Controls.TextBlock)guide.FindName("ProviderInstructions");
        Assert.Equal("tunnel_guide_test", endpoint.Text);
        Assert.Contains("无身份验证", instructions.Text);
        var originalStatus = status.Text;
        Invoke(guide, "CopyVerification_Click", guide, new RoutedEventArgs());
        Assert.Equal(WebConnectionGuide.VerificationPrompt, copied);
        Assert.Contains("list_projects", copied);
        Assert.Contains("不读取文件内容", copied);
        Assert.Equal(originalStatus, status.Text);
        Assert.Contains("已复制", ((System.Windows.Controls.TextBlock)guide.FindName("GuideFeedback")).Text);
        guide.Configure(profile, null, true, true);
        Assert.Contains("本次网页调用已验证", status.Text);
        guide.Configure(profile, null, false, true);
        Assert.DoesNotContain("已验证", status.Text);

        profile.Provider = TunnelProvider.CloudflareQuickTunnel;
        var pending = new TaskCompletionSource<string>();
        var generated = 0;
        guide.Configure(profile, "https://current.example/mcp", true, false,
            () => { generated++; return pending.Task; });
        Assert.Contains("OAuth", instructions.Text);
        Assert.Equal("https://current.example/mcp", endpoint.Text);
        Invoke(guide, "GeneratePairing_Click", guide, new RoutedEventArgs());
        Invoke(guide, "GeneratePairing_Click", guide, new RoutedEventArgs());
        Assert.Equal(1, generated);
        guide.Configure(profile, "https://current.example/mcp", false, false);
        pending.SetResult("expired-test-code");
        await Task.Delay(30);
        Assert.Empty(((System.Windows.Controls.TextBox)guide.FindName("GuidePairingCode")).Text);
        Assert.False(((System.Windows.Controls.Button)guide.FindName("CopyGuidePairingButton")).IsEnabled);
        Assert.False(((System.Windows.Controls.Button)guide.FindName("CopyEndpointButton")).IsEnabled);
        guide.Configure(profile, "https://new.example/mcp", true, false, () => Task.FromResult("test-pairing-code"));
        Invoke(guide, "GeneratePairing_Click", guide, new RoutedEventArgs());
        await Task.Delay(30);
        Assert.Equal("test-pairing-code", ((System.Windows.Controls.TextBox)guide.FindName("GuidePairingCode")).Text);
        Assert.Equal("https://new.example/mcp", endpoint.Text);
        guide.ShowPluginSetupOnly();
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)guide.FindName("VerificationInstruction")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)guide.FindName("VerificationActions")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)guide.FindName("VerificationPromptText")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)guide.FindName("TroubleshootingSection")).Visibility);
        Assert.Contains("先添加项目", ((System.Windows.Controls.TextBlock)guide.FindName("FirstTimeFinalStep")).Text);
        ((System.Windows.Controls.Expander)guide.FindName("FirstTimeSteps")).IsExpanded = true;
        var window = new Window { Content = new System.Windows.Controls.ScrollViewer { Content = guide },
            Width = 680, Height = 600, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        window.Show();
        window.UpdateLayout();
        SaveRender(window, "oauth-web-guide.png", 1.0);
        window.Close();
    }

    private sealed class DisplayCredentialStore : IRuntimeCredentialStore
    {
        public string? Secret { get; private set; }
        public int Reads { get; private set; }
        public void Save(string target, string secret) => Secret = secret;
        public bool Exists(string target) => Secret is not null;
        public string Read(string target) { Reads++; return Secret!; }
        public void Delete(string target) => Secret = null;
    }

    private static void SaveRender(FrameworkElement view, string fileName, double scale)
    {
        var width = Math.Max(1, (int)Math.Ceiling(view.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(view.ActualHeight * scale));
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "ui-preview")).FullName;
        using var file = File.Create(Path.Combine(directory, fileName));
        png.Save(file);
    }

    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    private const string FakeCli = """
        const fs=require('fs'),path=require('path'),http=require('http'),{spawn}=require('child_process');
        const args=process.argv.slice(2),state=process.env.C2C_STATE_DIR,runtime=path.join(state,'test-runtime.json');
        const value=key=>args[args.indexOf(key)+1];
        if(args[0]==='serve') {
          const workspaceRoot=path.resolve(value('--workspace'));
          const workspaceId=require('crypto').createHash('sha256').update(workspaceRoot.toLowerCase()).digest('hex').slice(0,12);
          const server=http.createServer((req,res)=>{res.setHeader('content-type','application/json');res.end(JSON.stringify({service:'c2c-bridge',status:'ok',workspaceId}));});
          server.listen(Number(value('--port')),'127.0.0.1',()=>{
            const child=spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{detached:true,stdio:'ignore'});child.unref();
            fs.writeFileSync(runtime,JSON.stringify({pid:process.pid,childPid:child.pid,port:server.address().port,workspaceRoot}));
          });
        } else if(args[0]==='status') {
          console.log(JSON.stringify({ok:true,running:true,...JSON.parse(fs.readFileSync(runtime,'utf8')),tokenCount:fs.existsSync(path.join(state,'authorized'))?2:0,tunnel:{running:true}}));
        } else if(args[0]==='start') {
          if(fs.readFileSync(path.join(state,'mode.txt'),'utf8')==='wait')setInterval(()=>{},1000);
          else console.log(JSON.stringify({ok:true,mcpUrl:'https://example.invalid/mcp',workspaceName:'test',connectorName:'test'}));
        } else if(args[0]==='unpair') fs.writeFileSync(path.join(state,'revoked.txt'),'true');
        """;
}

class UiFake : IConnectableAdapter
{
    public string Name => "fake";
    public int Starts;
    public int Stops;
    public SessionPolicy? LastPolicy { get; private set; }
    public bool IsRequiredFor(CapabilityFlags flags) => true;
    public Task AssertReadyAsync(SessionPolicy p, CancellationToken ct = default) => Task.CompletedTask;
    public Task StartAsync(SessionPolicy p, CancellationToken ct = default)
    {
        Starts++;
        LastPolicy = p;
        return Task.CompletedTask;
    }
    public Task<string> VerifyAsync(SessionPolicy p, CancellationToken ct = default)
        => Task.FromResult("共享连接已建立。");
    public Task StopAsync(SessionPolicy p)
    {
        Stops++;
        return Task.CompletedTask;
    }
    public event EventHandler<string>? Faulted { add { } remove { } }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class TrackingTaskRuntime(Guid projectId) : ISharedCodexTaskRuntime
{
    private readonly ICodexTaskBridge _bridge = new NoopTaskBridge();
    public bool Active { get; private set; }

    public Task<ICodexTaskBridge> GetBridgeAsync(ProjectRecord project, CancellationToken cancellationToken)
    {
        Assert.Equal(projectId, project.Id);
        Active = true;
        return Task.FromResult(_bridge);
    }

    public Task ReconcileAsync(IEnumerable<ProjectRecord> projects, CancellationToken cancellationToken = default)
    {
        Active = Active && projects.Any(project => project.Id == projectId && project.AllowCodexTasks);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Active = false;
        return ValueTask.CompletedTask;
    }
}

sealed class NoopTaskBridge : ICodexTaskBridge
{
    public Task<JsonObject?> CallToolAsync(string bridgeTool, JsonObject arguments, CancellationToken cancellationToken)
        => Task.FromResult<JsonObject?>(new JsonObject());
}




