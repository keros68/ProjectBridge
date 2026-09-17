<div align="center">

<img src="src/LocalProjectBridge/Assets/ProjectBridge.png" width="112" alt="ProjectBridge 图标">

# ProjectBridge

[English](README_en.md) · [下载](https://github.com/keros68/ProjectBridge/releases/latest) · [快速开始](#快速开始) · [使用说明](docs/guide.md) · [技术说明](docs/technical.md) · [许可证](#许可证)

**让 ChatGPT 网页端读取、修改你的本地项目，并把开发任务交给本机 Codex。**

</div>

ProjectBridge 是 Windows 托盘程序。一个连接可访问多个本地项目，每个项目单独授权，随时撤销。

## 功能

- **多项目访问**：ChatGPT 可列出目录、读取和搜索文件、查看 Git 差异。切换项目不需要重新连接。
- **网页修改**：ChatGPT 提交的修改先生成预览，本机确认后写入，写入前自动备份，可恢复。开启限时 YOLO 模式后可直接写入。
- **委派 Codex**：ChatGPT 可把开发任务交给本机 Codex 执行，并查询进度或停止任务。
- **本地与网页协作**：Codex 执行任务时，可请 ChatGPT 规划方案或复核改动。
- **两种连接方式**：OpenAI Secure Tunnel 提供固定入口；OAuth 临时连接无需额外账号配置。

## 快速开始

1. 安装 [Node.js 长期支持版](https://nodejs.org/)。
2. 从 [Releases](https://github.com/keros68/ProjectBridge/releases/latest) 下载 `ProjectBridge.zip`，解压后运行 `ProjectBridge.exe`。
3. 点击“建立连接”，按向导选择连接方式。
4. 按“添加 ChatGPT 插件”引导，在 ChatGPT 中开启开发人员模式并添加插件。
5. 在首页添加项目并勾选权限，然后在 ChatGPT 中选择 ProjectBridge 开始对话。

适用于 Windows 10/11（x64）。完整步骤见[使用说明](docs/guide.md)。

## 权限与安全

- 每个项目单独授权；撤销、断开或退出后，网页请求立即失效。
- `.env`、私钥、云凭据和 Git 凭据文件不可读取或修改，路径不能越出项目目录。
- YOLO 模式和 Codex 写入权限都需要每次连接后手动开启，不会自动恢复。
- Codex 可写任务直接修改文件，不经过预览、备份和敏感文件规则；开启前请提交或备份项目。
- 运行密钥保存在 Windows 凭据管理器，日志自动隐藏 Token 和 API Key。

详细限制见[技术说明](docs/technical.md)。

## 从源码构建

需要 .NET 10 SDK 和 Node.js。

```powershell
dotnet build LocalProjectBridge.slnx
dotnet test LocalProjectBridge.slnx
```

依赖组件和发布方法见[技术说明](docs/technical.md#构建与测试)。

## 许可证

ProjectBridge 采用 [MIT 许可证](LICENSE)。发布包附带的 Transceiver（AGPL-3.0）、codex-with-chatgpt（MIT）、tunnel-client 与 cloudflared（Apache-2.0）沿用各自许可证，见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
