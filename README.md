# ProjectBridge

ProjectBridge 是 Windows 托盘启动器，通过一个共享连接让 ChatGPT 读取多个已授权的本地项目、提交文本修改、委派 Codex 开发任务，并为运行中的 Codex 任务提供规划与复核。普通用户只需下载一个程序包，不需要分别下载两个后端项目。

## 使用

1. 安装 [Node.js 长期支持版](https://nodejs.org/)。已安装可跳过。
2. 从 GitHub Releases 下载并解压 `ProjectBridge.zip`，运行 `ProjectBridge.exe`。
3. 点击“建立连接”，选择连接方式。日常使用可选择 OpenAI Secure Tunnel；临时试用可选择 OAuth 临时连接。项目在首页单独添加和授权，可先连接再添加。
4. OpenAI Secure Tunnel 需要已有 Tunnel ID，以及具有 Tunnels Read + Use 权限的运行密钥。向导提供官方创建入口；粘贴的密钥保存到当前 Windows 用户的凭据管理器。也可在高级选项中使用环境变量或密钥文件。
5. 通道就绪后，按软件中的“首次使用：添加插件”引导在 ChatGPT 开启开发人员模式并添加插件。固定入口选择 Tunnel ID 和“无身份验证”；临时连接填写当前 HTTPS 地址并完成 OAuth 授权。
6. 点击“复制验证提示词”，在 ChatGPT 中选中 ProjectBridge 后粘贴发送。提示词只调用项目列表；程序收到成功工具调用后显示“网页已验证”。普通的成功工具调用也会完成验证，无需每次聊天单独测试。

连接设置顶部的“添加 ChatGPT 插件”按钮可打开完整引导，首次步骤直接展开在最上方。主窗口右上角也保留明显按钮，通道就绪但未验证时显示“下一步：添加插件并验证”。同一固定 Tunnel 的插件可复用，切换项目不需要重建；临时地址变化时需更新插件地址或重新添加。关闭向导后，可在首页添加项目和调整权限。插件创建流程参考 [OpenAI 官方说明](https://developers.openai.com/plugins/deploy/connect-chatgpt)。

共享入口提供项目列表、文件读取、目录、搜索、Git 差异，以及受限的文本补丁、新建、重命名和删除预览。每次请求必须带有项目 ID；主界面的项目选择只显示和编辑对应权限，不改变网页请求的目标。默认在本机编辑页确认应用；开启该项目的 YOLO 模式后，网页可直接提交修改。

### 网页委派 Codex

在项目页勾选“允许网页委派 Codex 任务”，默认启动只读任务。另行勾选“允许 Codex 修改项目并运行本地检查”后，网页可通过 `codex_task_start` 提交开发需求，由本机 Codex 修改项目并返回结果；`codex_task_status` 查询进度，`codex_task_stop` 停止任务。两种连接方式均支持，已有插件需要刷新工具列表，无需新建 Tunnel。

可写授权仅用于本次连接，需要每次连接后手动开启；保存的偏好不会自动恢复可写权限。撤权、断开或连接故障会停止对应任务。每个项目同时运行一个可写任务；停止任务保留已发生的文件修改。任务记录仅保留在当前连接中，重连后不恢复任务。

可写任务通过 [Codex App Server](https://learn.chatgpt.com/docs/app-server) 执行，需要本机 Codex CLI 已登录，使用 Codex 的额度。工作区写权限限定为项目根目录，命令网络关闭，禁止提权，禁用继承的 MCP 与网页搜索；这不是仅能读取项目的隔离环境。Codex 直接修改文件，不经过 ProjectBridge 的文本预览、敏感文件规则、文件大小限制或修改恢复记录；授权前应保留项目版本或备份。软件没有提供网页任意 Shell 命令接口。

### YOLO 模式

在“编辑”页为当前项目开启 YOLO，选择 15、30、60 或 120 分钟。有效期内，网页调用 `apply_change` 可直接执行文本修改、新建、重命名和删除，调用 `restore_change` 可安全恢复，免逐次本机确认。`prepare_change` 始终只生成预览。开启后不会自行执行已有预览，网页需要提交。

授权仅用于当前项目、连接和已验证主体；切换项目不会改变其他项目的授权。关闭 YOLO、撤销项目读取、连接故障、断开或程序重启后失效，不自动恢复。路径限制、文件冲突检查和写前备份继续生效。固定 Tunnel 的授权覆盖该 Tunnel 的可访问者。

## 本地与网页协作

1. 在“项目”页开启该项目的“允许本地与网页交换协作请求”。
2. 在“设置”页点击“安装 Codex 协作 Skill”。已有同名 Skill 会保留，不覆盖用户修改。
3. 在 Codex 中明确提出“请 ChatGPT 规划”或“请 ChatGPT 复核”。Skill 优先使用 Codex 的 ChatGPT 对话工具，内置浏览器作为备用入口；项目文件和差异由 ChatGPT 通过 ProjectBridge 读取。只要求发送消息时，发送后即结束，不启动规划或修改代码。
4. Codex 审查网页建议后执行本地工作，再请求同一对话检查真实差异。网页回复不会自行执行命令或扩大原任务权限。最小化或关闭主窗口后进入右下角托盘，连接继续运行；双击托盘图标恢复，托盘菜单“退出”会停止连接。

“协作记录”页用于查看上述过程的进度、回复和失败原因，平时无需操作。托盘图标以蓝色表示已连接、灰色表示未连接、橙色表示正在处理连接、红色表示异常；悬停查看文字状态。窗口图标和任务栏状态标记同步更新，蓝色不等于已完成网页工具调用验证。

每个对话映射同时绑定 Connection ID、project ID 和 Codex source thread。新对话在网页工具返回正确 project ID 后保存，不按标题匹配。发送意图、页面确认、回复和执行断点保存在 `%LOCALAPPDATA%\LocalProjectBridge\collaboration`；中断后先检查原对话，不因超时重复发送。请求有效期为 24 小时，撤销项目读取或协作权限会取消待处理请求。

已有连接若没有 `get_collaboration_request`，在 ChatGPT 的连接设置中刷新现有 ProjectBridge 插件，然后回到原对话继续。正常流程由 Codex 记录当前可见回复；既有 `reply_to_collaboration_request` 仅保留兼容。不要重建 Tunnel 或重发请求。详细流程见 [docs/auto-consult.md](docs/auto-consult.md)。

## 当前边界

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

## 构建

进程和 WPF 生命周期测试需要 Node.js；OAuth 集成测试还需要已安装的 C2C 后端与 cloudflared；任务桥和真实握手检查需要 Transceiver 与 Codex CLI。测试不提交模型任务。可设置 LPB_TEST_LIVE_TUNNEL=1 单独运行 SharedBridgeIntegrationTests，验证真实公网隧道。

```powershell
dotnet build LocalProjectBridge.slnx
dotnet test LocalProjectBridge.slnx
```

运行 `src\LocalProjectBridge\bin\Debug\net10.0-windows\ProjectBridge.exe`。开发版需要本机已有 Node.js、Git、Cloudflared 和 tunnel-client；面向普通用户的发布包携带两个后端和两个连接程序。

临时连接的读取、修改预览和任务委派共用 C2C OAuth 与公网隧道。OpenAI Secure Tunnel 提供同一套多项目工具和任务权限检查。只读任务使用既有 Transceiver 兼容桥，可写任务使用 Codex App Server。任务进程仅在收到首个任务时按项目启动，复用现有组件文件；撤销任务权限或断开共享连接会回收对应进程。

首次启动会打开设置向导。“登录 Windows 时启动”负责自动打开软件，“打开软件后自动连接（只读）”负责连接已保存的通道，两项默认均关闭。两项都勾选可在登录电脑后自动连接；YOLO 始终需要手动开启。

## 共享入口

复用 C2C 的 OAuth、配对与隧道，经过验证的请求转发到本机项目网关。网关只绑定回环地址，并使用每次启动随机生成的内部凭据。后端适配模块随核心程序集发布，不修改安装的上游源码；上游入口不兼容时会停止并报告错误。

临时隧道的公网健康检查由桌面网络栈执行，以遵循代理设置；失败时保留检查详情。程序跳过 Cloudflared 的启动连通性预检查，仍保留真实公网健康验证；首次启动失败会自动重试一次。临时地址不保证跨程序重启稳定，长期固定地址仍需配置固定入口。

OpenAI Secure Tunnel 重启时复用同一 Connection ID、runtime alias 与 Tunnel ID；本机回环端口可以变化，ChatGPT 侧仍选择同一 Tunnel。若当前账号没有 Tunnel、运行密钥或工作区权限，程序保留项目配置并报告缺失项，不会改用管理员密钥或降低认证要求。

## 发布

```powershell
pwsh -NoProfile -File scripts\publish.ps1
```

发布结果位于 `artifacts\ProjectBridge`，压缩包位于 `artifacts\ProjectBridge.zip`。

## 尚未实现

- 托盘程序不能后台唤醒已经结束的 Codex 任务；持续等待由运行中的 Codex 任务负责。
- 网页任意 Shell 命令执行。
- 图形化安装程序和在线更新提示。当前提供免安装压缩包。
- ChatGPT 账号侧操作仍需用户确认。

## 许可证

ProjectBridge 自有代码采用 [MIT 许可证](LICENSE)。发布包附带的 Transceiver（AGPL-3.0）、codex-with-chatgpt（MIT）、tunnel-client 与 cloudflared（Apache-2.0）沿用各自许可证，版本、来源和源码获取方式见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
