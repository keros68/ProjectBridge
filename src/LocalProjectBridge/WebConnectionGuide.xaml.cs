using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace LocalProjectBridge;

public partial class WebConnectionGuide : UserControl
{
    public const string VerificationPrompt = "请使用我已选中的 ProjectBridge 插件，实际调用 list_projects，列出已授权项目的名称。仅验证连接，不读取文件内容、不修改文件、不执行任务。若项目列表为空，请如实说明。不要只口头确认连接成功，也不要改用其他工具。";
    public const string PluginsUrl = "https://chatgpt.com/plugins";
    public const string DeveloperSettingsUrl = "https://chatgpt.com/#settings/Security";
    private Func<Task<string>>? _createPairing;
    private string? _endpoint;
    private bool _ready;
    private long _generation;
    private bool _pairingBusy;
    private bool _canPair;
    private readonly Action<string> _writeClipboard;

    public WebConnectionGuide() : this(System.Windows.Clipboard.SetText) { }

    internal WebConnectionGuide(Action<string> writeClipboard)
    {
        InitializeComponent();
        _writeClipboard = writeClipboard;
        VerificationPromptText.Text = VerificationPrompt;
    }

    public void ShowSetupFirst()
    {
        var sections = (StackPanel)FirstTimeSteps.Parent;
        sections.Children.Remove(FirstTimeSteps);
        sections.Children.Insert(1, FirstTimeSteps);
        FirstTimeSteps.IsExpanded = true;
    }

    public void ShowPluginSetupOnly()
    {
        ShowSetupFirst();
        GuideGroup.Header = "连接 ChatGPT";
        GuideStatus.Text = "通道已就绪，请先在 ChatGPT 添加 ProjectBridge。";
        VerificationInstruction.Visibility = Visibility.Collapsed;
        VerificationActions.Visibility = Visibility.Collapsed;
        VerificationPromptText.Visibility = Visibility.Collapsed;
        VerificationNote.Visibility = Visibility.Collapsed;
        TroubleshootingSection.Visibility = Visibility.Collapsed;
        FirstTimeFinalStep.Text = "3. 创建插件；快速体验按网页提示完成 OAuth 授权。完成后返回 ProjectBridge，先添加项目，再按新人任务卡完成真实读取验证。";
    }

    public void Configure(ConnectionProfile profile, string? mcpUrl, bool ready, bool verified,
        Func<Task<string>>? createPairing = null)
    {
        var secure = profile.Provider == TunnelProvider.OpenAiSecureTunnel;
        var endpoint = secure ? profile.TunnelId : ready ? mcpUrl : null;
        if (_endpoint != endpoint || _ready != ready)
        {
            _generation++;
            GuidePairingCode.Clear();
            CopyGuidePairingButton.IsEnabled = false;
            GuideFeedback.Text = string.Empty;
        }
        _endpoint = endpoint;
        _ready = ready;
        _createPairing = createPairing;
        GuideStatus.Text = !ready ? "先建立本地连接，再按此引导完成网页设置。"
            : verified ? "本次网页调用已验证，可以继续使用。"
            : "通道已就绪，下一步：从 ChatGPT 发起一次真实工具调用。";
        ProviderInstructions.Text = secure
            ? "连接方式选择“隧道 / Tunnel”，选中或填入下方 Tunnel ID；本程序的固定入口在认证方式中选择“无身份验证”。运行密钥仅保存在本机，无需粘贴到插件。"
            : "连接方式选择服务器 URL，填入下方完整 HTTPS 地址（包含 /mcp）；认证方式选择 OAuth，按网页提示授权。";
        EndpointLabel.Text = secure ? "当前 Tunnel ID" : "当前服务器地址";
        EndpointText.Text = endpoint ?? "建立连接后显示";
        CopyEndpointButton.IsEnabled = !string.IsNullOrWhiteSpace(endpoint);
        PairingPanel.Visibility = secure ? Visibility.Collapsed : Visibility.Visible;
        _canPair = ready && !secure && createPairing is not null;
        GeneratePairingButton.IsEnabled = _canPair && !_pairingBusy;
        TroubleshootingText.Text = secure
            ? "已有同一 Tunnel 的插件可直接复用，切换项目不用重建。找不到 Tunnel 时，检查其关联的 ChatGPT 工作区和 Tunnels Read + Use 权限。工具缺失或更新后仍显示旧能力时，在插件详情中刷新，再在新对话中选中插件测试。列表为空时，在本软件项目页添加项目并允许网页读取。"
            : "先比较插件中的服务器地址和上方当前地址；相同时复用现有插件，临时地址变化时需更新插件地址或重新添加。工具缺失时在插件详情中刷新，再在新对话中选中插件测试。列表为空时，在本软件项目页添加项目并允许网页读取。";
    }

    private void Copy(string value, string feedback)
    {
        try { _writeClipboard(value); GuideFeedback.Text = feedback; }
        catch (Exception) { GuideFeedback.Text = "剪贴板暂不可用，请选择上方文字手动复制。"; }
    }
    private void CopyVerification_Click(object sender, RoutedEventArgs e)
        => Copy(VerificationPrompt, "已复制。请在 ChatGPT 中选中 ProjectBridge 后粘贴发送，等待实际工具调用结果。");
    private void CopyEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_endpoint)) Copy(_endpoint, "连接信息已复制。");
    }
    private void CopyPairing_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(GuidePairingCode.Text)) Copy(GuidePairingCode.Text, "配对码已复制，请在网页授权页使用。");
    }
    private async void GeneratePairing_Click(object sender, RoutedEventArgs e)
    {
        if (!_canPair || _createPairing is null || _pairingBusy) return;
        var generation = _generation;
        _pairingBusy = true;
        GeneratePairingButton.IsEnabled = false;
        try
        {
            var code = await _createPairing();
            if (generation != _generation) return;
            GuidePairingCode.Text = code;
            CopyGuidePairingButton.IsEnabled = true;
            GuideFeedback.Text = "配对码已生成，仅用于本次网页授权。";
        }
        catch (Exception) { if (generation == _generation) GuideFeedback.Text = "无法生成配对码，请确认临时连接仍在运行后重试。"; }
        finally { _pairingBusy = false; GeneratePairingButton.IsEnabled = _canPair; }
    }
    private void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { GuideFeedback.Text = "无法打开浏览器，请手动访问 " + url; }
    }
    private void OpenChat_Click(object sender, RoutedEventArgs e) => Open("https://chatgpt.com/");
    private void OpenPlugins_Click(object sender, RoutedEventArgs e) => Open(PluginsUrl);
    private void OpenDeveloper_Click(object sender, RoutedEventArgs e) => Open(DeveloperSettingsUrl);
    private void OpenDocs_Click(object sender, RoutedEventArgs e) => Open("https://developers.openai.com/plugins/deploy/connect-chatgpt");
}
