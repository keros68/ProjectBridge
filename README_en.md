<div align="center">

<img src="src/LocalProjectBridge/Assets/ProjectBridge.png" width="112" alt="ProjectBridge icon">

# ProjectBridge

[简体中文](README.md) · [Download](https://github.com/keros68/ProjectBridge/releases/latest) · [Get started](#get-started) · [User guide (Chinese)](docs/guide.md) · [Technical notes (Chinese)](docs/technical.md) · [License](#license)

**ChatGPT on the web reads and edits local projects, and hands development tasks to Codex on the same machine.**

</div>

ProjectBridge is a Windows tray app. One connection gives access to multiple local projects. Each project is authorized separately and can be revoked at any time.

Comparable open-source setups usually require downloading the backends separately, installing dependencies by hand, starting a tunnel yourself, and then assembling addresses and keys into config files. ProjectBridge ships all of it as one portable package: two upstream backends, their runtime dependencies, cloudflared, and tunnel-client are bundled, and only Node.js is installed separately. Connecting, adding the ChatGPT plugin, and verifying are done through in-app wizards, with no command line.

## Features

- **Multi-project access**: ChatGPT can list directories, read and search files, and view Git diffs. Switching projects does not require reconnecting.
- **Web edits**: YOLO mode is on by default, so changes from ChatGPT are written directly, with automatic backups and restore. You can turn it off per project to review a preview and confirm locally instead.
- **Codex delegation**: ChatGPT can hand development tasks to local Codex, check their progress, or stop them.
- **Local and web collaboration**: While running a task, Codex can ask ChatGPT to plan an approach or review changes.
- **Two connection modes**: OpenAI Secure Tunnel provides a fixed endpoint; the temporary OAuth connection needs no extra account setup.

## Get started

Supports Windows 10/11 (x64). The app interface is in Chinese; the Chinese labels are given below in quotes.

**1. Install Node.js**

Download the LTS release from [nodejs.org](https://nodejs.org/) and install it with the default options. Skip if it is already installed.

**2. Download and run**

Download `ProjectBridge-Setup.exe` from [Releases](https://github.com/keros68/ProjectBridge/releases/latest) and run it (no administrator rights needed; it adds a Start menu shortcut). If Windows shows a "protected your PC" prompt, choose "More info" then "Run anyway".

Prefer no installer? Download the portable `ProjectBridge.zip`, extract it, and run `ProjectBridge.exe`; delete the folder to remove it.

On startup the app checks for a newer release and shows a notice at the top of the window. Click "立即更新" (Update now): the app downloads and verifies the installer, exits, installs into its current folder, and restarts; the connection drops once during the update. Projects and connection settings live in your user profile and survive updates and uninstall. You can turn the check off under Settings → Updates.

The setup wizard opens on first launch. Start on Windows login and auto-connect on launch are both off by default and can be left alone.

**3. Connect**

Click "建立连接" (Connect) and pick a mode:

| | Quick start | Long-term use |
| --- | --- | --- |
| Technical mode | Temporary OAuth | OpenAI Secure Tunnel |
| Extra setup | None | A Tunnel ID and a runtime key with Tunnels Read + Use |
| Public address | May change after a restart; update it in ChatGPT when it does | Fixed, so the plugin is reused long term |
| Best for | First use | Daily use |

If unsure, choose "快速体验" (Quick start); you can switch to the fixed endpoint later. The Tunnel ID and runtime key are created through the official links in the wizard, and the pasted key is stored in Windows Credential Manager.

**4. Add the plugin in ChatGPT**

Once the channel is ready, the first-time setup window shows the ChatGPT guide automatically. Follow three steps:

1. Enable developer mode in ChatGPT under Settings → Security and sign-in. If the entry is missing, check your account and workspace permissions.
2. Open the ChatGPT plugin page, click add (+), and name it ProjectBridge.
3. Fill in the connection details: for the fixed endpoint, select the Tunnel ID and "no authentication"; for the temporary connection, enter the current HTTPS address and complete OAuth authorization.

The wizard has buttons that jump straight to the developer settings and the plugin page, and "复制连接信息" (Copy connection details) gives you the address or Tunnel ID this connection needs.

**5. Add the first project**

After the connection is established, return to the home page. The first-use card prompts you to add a local project. Web read access is enabled by default; creating the connection does not expose other folders.

**6. Verify project access**

Click "复制项目验证提示词" (Copy project verification prompt) on the first-use card, select ProjectBridge in ChatGPT, then paste and send. The prompt calls `list_projects` first and then `list_directory` for that project root without reading file contents. The first-use card completes and hides after a successful project access.

See the [user guide](docs/guide.md) for full steps, YOLO mode, Codex delegation, and local collaboration.

## Troubleshooting

- **ChatGPT cannot find the tools**: refresh the ProjectBridge plugin in ChatGPT's connection settings, then return to the conversation. Tools added by an app update also need one refresh.
- **No connection after a restart**: the temporary connection's public address may change when the app restarts; update the plugin address as the wizard describes. For a permanently fixed address, switch to OpenAI Secure Tunnel.
- **Tray icon is blue but the app says not verified**: blue means the channel is connected; verification requires an actual tool call from the web, as in step 6.
- **The web says it edited a file but nothing changed**: in the default mode, changes wait on the "编辑" (Edit) page and are written after local confirmation.
- **A Codex task fails to start**: the local Codex CLI must be signed in, and tasks consume your Codex quota.

## Permissions and security

- Each project is authorized separately. After revoking access, disconnecting, or exiting, web requests are rejected.
- `.env` files, private keys, cloud credentials, and Git credentials cannot be read or modified. Paths cannot leave the project directory.
- YOLO mode is on by default: it takes effect for web-readable projects once the web connection is verified and is revoked on disconnect or exit. Untick it on the Edit tab to require local confirmation for a project; the choice is remembered. Codex write access must be enabled manually for each connection.
- Writable Codex tasks modify files directly, without preview, backup, or sensitive-file rules. Commit or back up the project first.
- Runtime keys are stored in Windows Credential Manager. Logs redact tokens and API keys.

See the [technical notes](docs/technical.md) for details.

## Build from source

Requires the .NET 10 SDK and Node.js.

```powershell
dotnet build LocalProjectBridge.slnx
dotnet test LocalProjectBridge.slnx
```

See the [technical notes](docs/technical.md#构建与测试) for bundled components and release packaging.

## License

ProjectBridge is released under the [MIT License](LICENSE). Bundled components keep their own licenses: Transceiver (AGPL-3.0), codex-with-chatgpt (MIT), tunnel-client and cloudflared (Apache-2.0). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
