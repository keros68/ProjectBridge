# 技术说明

## 行为与限制

- 连接与断开由统一 Session Controller 执行。回滚或停止失败时保留待清理对象，完成清理前不启动新连接。
- 连接使用独立的连接 UUID；界面的项目选择只保存 `SelectedProjectId`。添加项目或 A→B→A 切换不会更换连接身份、URL 或连接进程。
- 状态分别记录本地服务、隧道、客户端授权和最近一次成功网页工具调用。本地健康检查、隧道 readiness 或 OAuth 成功不会显示为“网页已验证”。
- 项目权限由共享注册表管理，请求执行前后均检查授权。撤销或删除项目后，其请求被拒绝；其他项目继续使用同一连接。
- 已完整安装的组件直接复用。添加项目只修改权限登记，不再重复执行依赖安装或构建。
- 路径防护拒绝 `..`、符号链接、目录联接与重解析点逃逸；`.env`、私钥、云凭据目录、Git 凭据及 `.c2cignore`/`.bridgeignore` 规则命中的文件不可读取、搜索或修改。
- 默认的 `apply_change` 返回等待本机确认；YOLO 授权有效时可直接写入。网页可读取已应用结果，待处理预览可在本机拒绝。
- 生成预览和应用都会检查权限、路径和敏感文件；应用时在项目锁内复核文件哈希。一次最多 20 个文件，每个文本文件最多 2 MiB，只接受严格 UTF-8/UTF-8 BOM。默认模式下，删除需在编辑页单独确认。
- 写入前保存逐文件恢复资料和持久修改记录，并在使用前核对暂存内容与备份的实际哈希。请求 ID 持久绑定操作、目标修改、连接主体和预览内容；同一请求重试返回已有结果，跨目标复用会被拒绝。部分失败保留逐文件状态；程序中断后按磁盘事实标记待恢复或人工检查，不自动重放。安全恢复仅处理本次已写入且仍保持提交后哈希的文件，不覆盖后续人工编辑，也不执行 `git reset --hard`。
- 网页文件工具按项目隔离。Codex 任务默认只读，本机额外授权后使用项目工作区写权限；不能保证只读取所选文件夹。任务固定拒绝提权，命令网络权限设为关闭，禁用继承的 MCP 服务与网页搜索。网页直接编辑不依赖 Codex。
- 可写任务在每次启动前，通过同一个 Codex 沙盒创建、读取并删除随机命名的探测文件。项目授权不代表 Windows ACL 已允许写入；探测失败返回 `workspace_write_unavailable`，不提交模型任务。程序不会自动提权或修改全局沙盒模式。
- 可写委派保存 Codex 会话和工具历史，标题以 `ChatGPT` 开头，状态响应提供 `threadUrl`。网页发来的消息带 `|from_chatgpt|:`，本地协作请求带 `|from_codex|:`。旧版本的临时会话无法从 Codex 历史恢复。
- `completed` 表示模型轮次结束，不代表请求的文件修改成功。可写任务响应中的 `toolFailures` 保留失败命令或文件操作，调用方需同时检查 `result` 和 `error`。启动检查通过也不保证后续每个文件可写。
- 子进程纳入 Windows Job Object，启动器异常退出时由系统回收整个进程树；端口由系统动态分配。
- 发布包自带两个上游后端、C2C 运行依赖、cloudflared 和 tunnel-client。Node.js 作为通用运行环境单独安装。
- 首次设置默认使用免费临时连接。地址变化后，需要按向导在 ChatGPT 中更新共享连接。项目切换不会改变正在运行的地址。
- 断开或退出后终止共享 bridge、cloudflared 或本程序启动的 tunnel-client runtime，并等待本地健康端口关闭。普通断开保留配对；解除配对是单独操作。
- 同一 Windows 会话只允许运行一个启动器实例，避免多个程序同时改写项目登记和连接状态。
- C2C 桥接由程序直接启动和监管。准备失败或取消设置时，会回收桥接及其 Cloudflared 子进程。检测到真实网页授权后自动接管连接；关闭向导前再次检查授权，避免刚配对就撤销。取消未配对的设置、断开或退出时回收。
- OpenAI Secure Tunnel 使用 `tunnel-client runtimes connect --tunnel-id ... --runtime-api-key ...`。日常运行不读取管理员 profile，不依赖 `OPENAI_ADMIN_KEY`。Tunnel 创建、工作区可见性和 ChatGPT 侧选择仍由账号权限控制。
- OpenAI Secure Tunnel 当前把整个 Tunnel 连接视为一个本机授权主体，不能区分同一工作区中的不同网页用户。共享 Tunnel 的可访问者共用本机授予的项目权限，包括有效的 YOLO 和可写任务授权。
- tunnel-client 使用程序专属状态目录和稳定 alias；所有权标记包含连接 ID、PID 与启动时间。停止失败时保留标记并显示“清理未完成”，不会按进程名结束其他程序。
- 程序的 C2C 状态保存在 `%LOCALAPPDATA%\LocalProjectBridge\c2c`，与手动安装的旧插件分开。旧插件的启用配置和消息前缀不由本程序修改。
- 项目登记保存在 `%LOCALAPPDATA%\LocalProjectBridge\projects.json`，不保存密钥。
- 协作记录只保存对话 URL、请求元数据、状态和回复，不保存 ChatGPT 登录凭据。
- 修改记录、暂存内容和恢复资料保存在 `%LOCALAPPDATA%\LocalProjectBridge\changes`。
- 日志自动隐藏 Token、API Key 和配对码。

## 共享入口

复用 C2C 的 OAuth、配对与隧道，经过验证的请求转发到本机项目网关。网关只绑定回环地址，并使用每次启动随机生成的内部凭据。后端适配模块随核心程序集发布，不修改安装的上游源码；上游入口不兼容时会停止并报告错误。

临时隧道的公网健康检查由桌面网络栈执行，以遵循代理设置；失败时保留检查详情。程序跳过 Cloudflared 的启动连通性预检查，仍保留真实公网健康验证；首次启动失败会自动重试一次。临时地址不保证跨程序重启稳定，长期固定地址仍需配置固定入口。

OpenAI Secure Tunnel 重启时复用同一 Connection ID、runtime alias 与 Tunnel ID；本机回环端口可以变化，ChatGPT 侧仍选择同一 Tunnel。若当前账号没有 Tunnel、运行密钥或工作区权限，程序保留项目配置并报告缺失项，不会改用管理员密钥或降低认证要求。

## 项目结构

```text
src/
  LocalProjectBridge.Core/      # 核心库（无 UI 依赖，可单测）
    Sessions/                   # 会话控制器、状态机、能力开关、SessionPolicy、错误分类
    Security/                   # 路径逃逸防护、敏感文件规则
    Processes/                  # Windows Job Object、动态端口
    Adapters/                   # C2C、Transceiver、统一网关适配器
    Gateway/                    # 统一 MCP 网关：JSON-RPC、只读工具、修改预览与恢复、Codex 任务代理
    Persistence/                # projects.json 登记
  LocalProjectBridge/           # WPF 壳：主窗口、托盘、向导、项目权限
  CodexShim/                    # Codex CLI 启动垫片（与启动器共享运行时发现结果）
tests/
  LocalProjectBridge.Core.Tests # xunit 单元测试
```

## 构建与测试

进程和 WPF 生命周期测试需要 Node.js；OAuth 集成测试还需要已安装的 C2C 后端与 cloudflared；任务桥和真实握手检查需要 Transceiver 与 Codex CLI。测试不提交模型任务。可设置 LPB_TEST_LIVE_TUNNEL=1 单独运行 SharedBridgeIntegrationTests，验证真实公网隧道。

```powershell
dotnet build LocalProjectBridge.slnx
dotnet test LocalProjectBridge.slnx
```

真实模型验收需显式启用，会使用已登录 Codex 的额度，并保留测试会话。设置 `LPB_TEST_LIVE_CODEX=1`、`LPB_TEST_PROJECT_ROOT`（可写测试项目）和 `LPB_TEST_DENIED_ROOT`（已确认被沙盒拒写的目录），运行 `LiveNativeTaskTests`。普通测试运行跳过这两项。`scripts/verify-codex-task.mjs` 另提供 App Server 协议验收。

若授权后仍无法写入，先检查目标目录的 Windows 所有者与沙盒设置。`unelevated` 模式在无权设置目录 ACL 时可能仍能读文件，却无法写入。应由本机用户在 Codex 中完成该目录的 Windows 沙盒设置，或选择当前用户拥有的目录；不能用关闭沙盒来代替修复。

运行 `src\LocalProjectBridge\bin\Debug\net10.0-windows\ProjectBridge.exe`。开发版需要本机已有 Node.js、Git、Cloudflared 和 tunnel-client；面向普通用户的发布包携带两个后端和两个连接程序。

临时连接的读取、修改预览和任务委派共用 C2C OAuth 与公网隧道。OpenAI Secure Tunnel 提供同一套多项目工具和任务权限检查。只读任务使用既有 Transceiver 兼容桥，可写任务使用 Codex App Server。任务进程仅在收到首个任务时按项目启动，复用现有组件文件；撤销任务权限或断开共享连接会回收对应进程。

首次启动会打开设置向导。“登录 Windows 时启动”负责自动打开软件，“打开软件后自动连接（只读）”负责连接已保存的通道，两项默认均关闭。两项都勾选可在登录电脑后自动连接。YOLO 默认开启：网页主体验证后，为已授权网页读取且未单独关闭的项目授予两小时写入授权，并在定时刷新中自动续期；断开或退出时全部收回。

## 发布

```powershell
pwsh -NoProfile -File scripts\publish.ps1
```

发布结果位于 `dist\ProjectBridge`（可直接运行 `ProjectBridge.exe`），压缩包位于 `dist\ProjectBridge.zip`，校验值在同名 `.sha256` 文件中。装有 Inno Setup 6 时还会生成安装程序 `dist\ProjectBridge-Setup.exe`（脚本在 `installer\ProjectBridge.iss`）。程序内“立即更新”依赖 Release 中的 `ProjectBridge-Setup.exe` 和 `ProjectBridge-Setup.exe.sha256`：校验通过后以 `/VERYSILENT /AUTOUPDATE=1 /DIR=<当前目录>` 启动安装程序并退出；安装程序最多等待 60 秒让旧版释放单实例锁，安装完成后重新启动程序。`artifacts\` 只存放测试和验收的临时输出。

## 尚未实现

- 托盘程序不能后台唤醒已经结束的 Codex 任务；持续等待由运行中的 Codex 任务负责。
- 网页任意 Shell 命令执行。
- 图形化安装程序和在线更新提示。当前提供免安装压缩包。
- ChatGPT 账号侧操作仍需用户确认。
