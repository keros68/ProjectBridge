<div align="center">

<img src="src/LocalProjectBridge/Assets/ProjectBridge.png" width="112" alt="ProjectBridge icon">

# ProjectBridge

[简体中文](README.md) · [Download](https://github.com/keros68/ProjectBridge/releases/latest) · [Get started](#get-started) · [User guide (Chinese)](docs/guide.md) · [Technical notes (Chinese)](docs/technical.md) · [License](#license)

**Let ChatGPT on the web read and edit your local projects, and hand development tasks to Codex on your machine.**

</div>

ProjectBridge is a Windows tray app. One connection gives access to multiple local projects. Each project is authorized separately and can be revoked at any time.

## Features

- **Multi-project access**: ChatGPT can list directories, read and search files, and view Git diffs. Switching projects does not require reconnecting.
- **Web edits**: Changes proposed by ChatGPT are shown as a preview and written after local confirmation, with automatic backups and restore. With time-limited YOLO mode enabled, changes are written directly.
- **Codex delegation**: ChatGPT can hand development tasks to local Codex, check their progress, or stop them.
- **Local and web collaboration**: While running a task, Codex can ask ChatGPT to plan an approach or review changes.
- **Two connection modes**: OpenAI Secure Tunnel provides a fixed endpoint; the temporary OAuth connection needs no extra account setup.

## Get started

1. Install [Node.js LTS](https://nodejs.org/).
2. Download `ProjectBridge.zip` from [Releases](https://github.com/keros68/ProjectBridge/releases/latest), extract it, and run `ProjectBridge.exe`.
3. Click "建立连接" (Connect) and choose a connection mode in the wizard.
4. Follow the "添加 ChatGPT 插件" (Add ChatGPT plugin) guide to enable developer mode in ChatGPT and add the plugin.
5. Add a project on the home page, select its permissions, then choose ProjectBridge in a ChatGPT conversation.

Supports Windows 10/11 (x64). The app interface is in Chinese. See the [user guide](docs/guide.md) for full steps.

## Permissions and security

- Each project is authorized separately. After revoking access, disconnecting, or exiting, web requests are rejected.
- `.env` files, private keys, cloud credentials, and Git credentials cannot be read or modified. Paths cannot leave the project directory.
- YOLO mode and Codex write access must be enabled manually for each connection and are not restored automatically.
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
