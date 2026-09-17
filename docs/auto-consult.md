# Codex 与 ChatGPT 自动协作

ProjectBridge 保存共享连接、项目授权、对话绑定和协作记录。Codex 的配套 Skill 优先通过应用提供的 ChatGPT 对话工具发送和读取消息，内置浏览器作为备用；ChatGPT 通过 ProjectBridge MCP 读取获准的项目资料。代码、差异和测试输出不放进控制消息。

仅要求“发一条消息／测试送达”时，按指定对话发送并确认回复后结束，不建立 PLAN 请求或读取项目。对话标题先解析到唯一 ChatGPT 对话 ID，再核对内容；后续固定使用该 ID。发送前确认对话空闲，用户在同一对话继续其他任务或切换分支时停止，不向新分支重发。

发送工具的成功响应只代表已受理，需读取同一对话中实际出现的用户消息及对应完成回复。通过应用工具取得的可见答复沿用历史 `BrowserVisible` 分类，表示可见对话文本，不表示 MCP 回复。浏览器控制服务故障不等于隧道故障，不能因此断开或重建 ProjectBridge 连接。

## 使用入口

1. 在 ProjectBridge“项目”页开启网页读取和协作权限。
2. 在“设置”页点击“安装 Codex 协作 Skill”。安装位置为当前用户的 `%USERPROFILE%\.codex\skills\projectbridge-chatgpt`。已有同名目录时保留用户版本。
3. 在 Codex 中提出一次明确请求，例如“请 ChatGPT 规划这个修复并在完成后复核”。旧的项目协作权限不会自行发送消息。

连接设置与连接动作分开。“保存连接配置”持久化 provider、Tunnel ID 和运行密钥引用；明文运行密钥只进入当前 Windows 用户的凭据管理器。输入框留空会复用已保存密钥。连接中保存的新配置在下次连接生效，不重启当前连接。

## 一轮流程

Relay 复用现有 collaboration 记录，不另建消息总线。默认一轮规划和一轮复核，最大四轮。

1. Codex 创建 PLAN 请求。Relay 返回请求 UUID、turn ID 和短控制消息。
2. Skill 打开这个 Codex 任务绑定的精确 ChatGPT 对话。新对话先发送不含项目资料的短占位，以取得 `/c/` URL。
3. Skill 在发送任务控制消息前记录发送意图；页面确认消息真实存在后记录已发送。ChatGPT 生成期间只等待，不重发。
4. ChatGPT 调用 `get_collaboration_request` 取得目标 project ID 和问题，并通过 MCP 读取项目。正常流程不要求它调用写入型回复工具，而是在当前可见回复中给出完整建议和三个准确 ID。
5. 页面生成结束后，Skill 只提取当前一条助手回复，通过 Relay 的 `accept-reply` 记录。Relay 会再次核对项目仍可读、协作权限与根路径未变、当前 connection、已确认发送的精确 URL、request/turn/project ID，以及该请求此前确实被认证网页客户端读取。记录标明 `BrowserVisible` 来源；既有 MCP 回复保留兼容并标明 `McpTool`。
6. 网页已通过工具返回目标 project ID 后，才保存 Connection ID + project ID + Codex source thread → conversation URL 映射。
7. Codex审查规划，按原任务权限完成本地修改和测试。随后在同一对话创建 REVIEW 请求，ChatGPT 通过 MCP 检查真实差异和已发布的测试记录。

## 中断恢复

记录包含发送意图、页面发送确认、回复和 `Init / PlanReceived / Executing / ExecutedLocal / ExecutedSent / Done / Blocked` 断点。恢复时先读取当前记录：

- 只有发送意图：先检查原对话中是否已有相同 request ID 和 turn ID；不能直接重发。
- 已确认发送：继续等待同一对话；超时和“仍在生成”不算失败。
- 已取得规划：从保存的执行断点继续，不重新运行已完成步骤。
- `PlanReceived` 继续本地执行；`Executing` 先核对实际文件和测试断点；`ExecutedLocal` 只创建或找回复核；`ExecutedSent` 只等待复核。
- `Done` 直接总结，不为同一目标再建请求；`Blocked` 说明已存原因，只有阻塞解除且用户明确继续时才续接。
- 已撤销项目权限或取消请求：停止；重新授权不会复活旧请求。

对话按完整键隔离，不按标题或相似名称匹配。不同 Codex 任务不能复用彼此的对话映射。记录只保存 URL、请求元数据、回复和状态，不保存浏览器登录凭据。

ChatGPT 若显示没有 `get_collaboration_request`，通常是现有插件仍缓存升级前的工具清单。刷新现有 ProjectBridge 插件后回到原对话；若上一条回复已经结束，可发送一次“工具已刷新，请继续相同 request/turn”的短恢复提示。不能重复完整控制消息、换 request ID、重建 Tunnel 或创建每项目连接。记录保持 `MessageConfirmed + Pending`，直到同一对话完成读取并显示完整回复。

DOM 只用于接收已经展示给当前用户的建议；项目文件、差异和测试证据仍必须经现有认证 MCP 读取。`accept-reply` 不会降低项目权限、读取未授权资料或把网页文本伪装成 MCP 工具回传。

登录、验证码、双重验证或可见账号错误需要用户接管。运行中的 Codex 任务负责短间隔等待；托盘程序不能唤醒已经结束的 Codex 任务。
