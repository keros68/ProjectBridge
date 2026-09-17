using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LocalProjectBridge.Core.Collaboration;
using Forms = System.Windows.Forms;
using Clipboard = System.Windows.Clipboard;

namespace LocalProjectBridge;

public partial class MainWindow
{
    private readonly CollaborationStore _collaborationStore;
    private readonly ObservableCollection<CollaborationRequest> _collaborationRequests = [];
    private readonly DispatcherTimer _collaborationRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<Guid> _knownAnsweredCollaborationRequests = [];
    private readonly HashSet<Guid> _knownCliPendingCollaborationRequests = [];
    private bool _collaborationNotificationsPrimed;
    private string? _lastCollaborationRefreshError;

    private void InitializeCollaborationUi()
    {
        CollaborationRequestList.ItemsSource = _collaborationRequests;
        _collaborationRefreshTimer.Tick += CollaborationRefreshTimer_Tick;
        RefreshCollaborationRequests();
        _collaborationRefreshTimer.Start();
    }

    private void UpdateCollaborationProjectUi()
    {
        var project = ProjectPicker.SelectedItem as ProjectRecord;
        var enabled = project is { AllowWebRead: true, AllowCodexAskWeb: true };
        CollaborationProjectText.Text = project is null
            ? "项目：未选择"
            : enabled
                ? $"项目：{project.Name}"
                : $"项目：{project.Name}（未启用网页协作）";
    }

    private void RefreshCollaborationRequests_Click(object sender, RoutedEventArgs e)
        => RefreshCollaborationRequests(showErrors: true);

    private void RefreshCollaborationRequests(bool showErrors = false, bool notifyChanges = false)
    {
        try
        {
            UpdateCollaborationProjectUi();
            var selectedRequestId = (CollaborationRequestList.SelectedItem as CollaborationRequest)?.RequestId;
            var allRequests = _collaborationStore.List();
            NotifyCollaborationChanges(allRequests, notifyChanges);
            var projectId = (ProjectPicker.SelectedItem as ProjectRecord)?.Id;
            var visible = allRequests.Where(request => request.ProjectId == projectId).ToArray();
            if (!_collaborationRequests.SequenceEqual(visible))
            {
                _collaborationRequests.Clear();
                foreach (var request in visible)
                    _collaborationRequests.Add(request);
                CollaborationRequestList.SelectedItem = selectedRequestId is { } id
                    ? _collaborationRequests.FirstOrDefault(request => request.RequestId == id)
                    : _collaborationRequests.FirstOrDefault();
                ShowSelectedCollaborationRequest();
            }
            _lastCollaborationRefreshError = null;
        }
        catch (Exception error)
        {
            if (showErrors || !string.Equals(_lastCollaborationRefreshError, error.Message, StringComparison.Ordinal))
            {
                ShowError("无法刷新协作记录。", "请检查本机协作目录后重试。", error.Message);
                _lastCollaborationRefreshError = error.Message;
            }
        }
    }

    private void NotifyCollaborationChanges(IReadOnlyList<CollaborationRequest> requests, bool notifyChanges)
    {
        var answered = requests.Where(request => request.Status == CollaborationStatus.Answered)
            .Select(request => request.RequestId).ToHashSet();
        var cliPending = requests.Where(request => request.Status == CollaborationStatus.Pending && IsCliRequest(request))
            .Select(request => request.RequestId).ToHashSet();

        if (_collaborationNotificationsPrimed && notifyChanges)
        {
            if (answered.Any(requestId => !_knownAnsweredCollaborationRequests.Contains(requestId)))
                ShowCollaborationNotification("协作请求已收到网页回复。");
            if (cliPending.Any(requestId => !_knownCliPendingCollaborationRequests.Contains(requestId)))
                ShowCollaborationNotification("检测到新的命令行协作请求。");
        }

        _knownAnsweredCollaborationRequests.Clear();
        _knownAnsweredCollaborationRequests.UnionWith(answered);
        _knownCliPendingCollaborationRequests.Clear();
        _knownCliPendingCollaborationRequests.UnionWith(cliPending);
        _collaborationNotificationsPrimed = true;
    }

    private static bool IsCliRequest(CollaborationRequest request)
        => !string.Equals(request.Source, "ProjectBridge", StringComparison.OrdinalIgnoreCase);

    private void ShowCollaborationNotification(string text)
    {
        try { _tray.ShowBalloonTip(3000, "ProjectBridge", text, Forms.ToolTipIcon.Info); }
        catch (InvalidOperationException) { }
    }

    private void CollaborationRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_exiting) return;
        RefreshCollaborationRequests(notifyChanges: true);
    }

    private void CollaborationRequestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ShowSelectedCollaborationRequest();

    private void ShowSelectedCollaborationRequest()
    {
        if (CollaborationRequestList.SelectedItem is not CollaborationRequest request)
        {
            CollaborationStatusText.Text = "尚无协作请求。";
            CollaborationRequestDetailText.Text = string.Empty;
            CollaborationReplyText.Text = string.Empty;
            CopyCollaborationReplyButton.IsEnabled = false;
            CancelCollaborationRequestButton.IsEnabled = false;
            return;
        }

        CollaborationStatusText.Text = $"状态：{FormatCollaborationStatus(request.Status)}，到期：{request.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        CollaborationRequestDetailText.Text = request.Question;
        CollaborationReplyText.Text = request.Reply ?? "尚未收到网页回复。";
        CopyCollaborationReplyButton.IsEnabled = !string.IsNullOrWhiteSpace(request.Reply);
        CancelCollaborationRequestButton.IsEnabled = request.Status is CollaborationStatus.Pending or CollaborationStatus.Read;
    }

    private static string FormatCollaborationStatus(CollaborationStatus status) => status switch
    {
        CollaborationStatus.Pending => "等待读取",
        CollaborationStatus.Read => "已读取，等待回复",
        CollaborationStatus.Answered => "已回复",
        CollaborationStatus.Cancelled => "已取消",
        CollaborationStatus.Expired => "已过期",
        _ => "未知"
    };

    private void CopyCollaborationReply_Click(object sender, RoutedEventArgs e)
    {
        if (CollaborationRequestList.SelectedItem is not CollaborationRequest { Reply: { Length: > 0 } reply }) return;
        try { Clipboard.SetText(reply); }
        catch (Exception error) { ShowError("无法复制网页回复。", "请关闭占用剪贴板的程序后重试。", error.Message); }
    }

    private void CancelCollaborationRequest_Click(object sender, RoutedEventArgs e)
    {
        if (CollaborationRequestList.SelectedItem is not CollaborationRequest request) return;
        try
        {
            _collaborationStore.Cancel(request.RequestId);
            RefreshCollaborationRequests();
        }
        catch (Exception error)
        {
            ShowError("无法取消协作请求。", "刷新协作记录后重试。", error.Message);
        }
    }

    private void StopCollaborationPolling()
    {
        _collaborationRefreshTimer.Stop();
        _collaborationRefreshTimer.Tick -= CollaborationRefreshTimer_Tick;
        _collaborationStore.Dispose();
    }

    private void CancelProjectCollaboration(Guid projectId)
    {
        try { _collaborationStore.CancelProject(projectId); }
        catch (Exception error)
        {
            ShowError("协作记录暂时不可用，项目权限仍按当前设置生效。", "检查本机协作目录后重试。", error.Message);
        }
    }

    private void InstallCodexSkill_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = CodexSkillInstaller.Install(AppContext.BaseDirectory);
            CodexSkillInstallStatus.Text = result.Message + Environment.NewLine + result.Destination;
        }
        catch (Exception error)
        {
            CodexSkillInstallStatus.Text = "安装失败：" + error.Message;
        }
    }
}
