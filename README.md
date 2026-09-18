<div align="center">

<img src="src/LocalProjectBridge/Assets/ProjectBridge.png" width="112" alt="ProjectBridge 图标">

# ProjectBridge

[English](README_en.md) · [下载](https://github.com/keros68/ProjectBridge/releases/latest) · [快速开始](#快速开始) · [使用说明](docs/guide.md) · [技术说明](docs/technical.md) · [许可证](#许可证)

**ChatGPT 网页端读写本机项目，并把开发任务交给本机 Codex。**

</div>

ProjectBridge 是 Windows 托盘程序。一个连接可访问多个本地项目，每个项目单独授权，随时撤销。

同类开源方案通常要分别下载后端、手动安装依赖、自行启动隧道，再把地址和密钥拼进配置文件。ProjectBridge 把这些合成一个免安装程序包：两个上游后端、运行依赖、cloudflared 和 tunnel-client 随包发布，另外只需安装 Node.js。建立连接、添加 ChatGPT 插件和验证都在程序内由向导完成，不需要命令行。

## 功能

- **多项目访问**：ChatGPT 可列出目录、读取和搜索文件、查看 Git 差异。切换项目不需要重新连接。
- **网页修改**：ChatGPT 提交的修改先生成预览，本机确认后写入，写入前自动备份，可恢复。开启限时 YOLO 模式后可直接写入。
- **委派 Codex**：ChatGPT 可把开发任务交给本机 Codex 执行，并查询进度或停止任务。
- **本地与网页协作**：Codex 执行任务时，可请 ChatGPT 规划方案或复核改动。
- **两种连接方式**：OpenAI Secure Tunnel 提供固定入口；OAuth 临时连接无需额外账号配置。

## 快速开始

适用于 Windows 10/11（x64），程序界面为中文。

**1. 安装 Node.js**

到 [nodejs.org](https://nodejs.org/) 下载长期支持版（LTS）并安装，全程默认选项即可。已安装可跳过。

**2. 下载并运行**

从 [Releases](https://github.com/keros68/ProjectBridge/releases/latest) 下载 `ProjectBridge.zip`，解压到任意目录，运行 `ProjectBridge.exe`。程序免安装，卸载时直接删除目录。若 Windows 提示“已保护你的电脑”，点“更多信息”后选择“仍要运行”。

首次启动会打开设置向导。“登录 Windows 时启动”和“打开软件后自动连接”默认关闭，可以先不管。

**3. 建立连接**

点击“建立连接”，选择连接方式：

| | OAuth 临时连接 | OpenAI Secure Tunnel |
| --- | --- | --- |
| 额外配置 | 无 | 需要 Tunnel ID 和具有 Tunnels Read + Use 权限的运行密钥 |
| 公网地址 | 程序重启后可能变化，变化后需在 ChatGPT 中更新 | 固定，插件长期复用 |
| 适合 | 首次试用 | 日常使用 |

不确定时先选 OAuth 临时连接，之后可随时改用固定入口。Secure Tunnel 所需的 Tunnel ID 和运行密钥由向导中的官方入口创建，粘贴后保存到 Windows 凭据管理器。

**4. 在 ChatGPT 中添加插件**

通道就绪后点击“添加 ChatGPT 插件”，展开“首次使用：添加插件”，按三步操作：

1. 在 ChatGPT 的设置 → 安全与登录中开启“开发人员模式”。入口不可用时，检查账号及工作区权限。
2. 打开 ChatGPT 插件页，点击添加（+），名称填 ProjectBridge。
3. 填入连接信息：固定入口选择 Tunnel ID 和“无身份验证”，临时连接填当前 HTTPS 地址并完成 OAuth 授权。

向导里的“打开开发者设置”“打开插件页面”按钮可直接跳转，“复制连接信息”提供本次要填的地址或 Tunnel ID。

**5. 验证连接**

点击“复制验证提示词”，在 ChatGPT 对话的工具菜单（输入框旁的 + 或 @）中选中 ProjectBridge，粘贴发送。该提示词只调用项目列表，程序收到成功调用后显示“网页已验证”。之后任何一次成功的工具调用都会完成验证，不必每次对话单独测试。

**6. 添加项目**

在首页添加项目目录并勾选权限，然后在 ChatGPT 中选择 ProjectBridge 开始对话。网页提交的修改默认出现在“编辑”页等待确认。

完整步骤、YOLO 模式、委派 Codex 和本地协作的设置见[使用说明](docs/guide.md)。

## 遇到问题

- **ChatGPT 里找不到工具，或提示工具不存在**：在 ChatGPT 的连接设置中刷新 ProjectBridge 插件，再回到对话。程序升级后新增的工具也需要刷新一次。
- **重启后 ChatGPT 连不上**：临时连接的公网地址在程序重启后可能变化，按向导更新插件地址。需要长期固定地址时改用 OpenAI Secure Tunnel。
- **托盘图标是蓝色，但显示未验证**：蓝色表示通道已连接，验证需要网页实际调用一次工具，见第 5 步。
- **网页说改好了，文件却没变**：默认模式下修改停在“编辑”页，本机确认后写入。
- **Codex 任务启动失败**：本机 Codex CLI 需已登录，任务使用 Codex 的额度。

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
