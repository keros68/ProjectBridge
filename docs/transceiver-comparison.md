# Transceiver 对照

对照版本：Transceiver `c822936958e7b084e35afaee1035f4f6ec0427a2`，ProjectBridge v0.1.0 → v0.1.1。已检查上游脚本、打包后的 App Server 调用实现，以及 ProjectBridge 的适配器、最终协议检查和测试。

| 层面 | Transceiver | ProjectBridge 与本次处理 |
| --- | --- | --- |
| 项目登记 | 按路径保存 `sync`、`allowCodexTasks`，只把双重授权的根目录交给反向桥 | 使用项目 UUID 和授权版本，任务按项目绑定，撤销时回收该项目进程。保留 |
| Codex 启动 | 复用上游 App Server 后端，固定只读 | 只读仍复用 Transceiver；可写用本地 App Server 客户端，经 Shim 限制最终参数。保留两个适配分支 |
| 工作目录 | 根据登记根目录检查 `cwd`；这一检查不等于 OS 读取隔离 | 子进程工作目录、thread、turn 和 command 都绑定已登记项目。检查未发现漏传 `cwd` |
| 写权限 | 固定只读，不提供可写任务的参考实现 | 原实现只确认授权和参数，遗漏真实可写性。v0.1.1 清除继承的额外可写目录，并在每次模型任务前通过实际沙盒读写探测 |
| 生命周期 | `ephemeral:false`，保存 thread 与项目映射，支持继续原 thread | 原可写分支使用 `ephemeral:true`。现改为持久会话、扩展工具历史、来源标题与 `threadUrl`。跨程序重启恢复网页 task_id 尚未实现 |
| 结果 | 区分 job、turn 与状态，调用方仍需检查返回结果 | 原实现主要返回模型文字与 turn 状态。现增加 `toolFailures`，明确轮次结束不等于修改成功 |

来源标识由两端消息构造器统一添加。标签是传输来源说明，不是额外权限，也不能证明消息已被另一端接收。

## 结论

无需整体替换现有实现。ProjectBridge 已复用 Transceiver 的只读委派，额外提供多项目共享连接、Windows 界面、文件预览与恢复、连接级授权。当前缺陷集中在后来加入的可写分支；把它的会话保存与来源标识补齐，再验证真实权限，改动范围更明确。

本机复现中，CLI 0.154.0 对目标目录返回了正确的 `cwd` 和 `workspaceWrite`，但 Windows 拒绝实际写入；同样调用在用户拥有的目录成功。Windows 沙盒初始化返回成功也不足以保证文件可写。不能据此断言“外层宿主只开放了另一个项目”，也不能通过声明更多 `writableRoots` 解决目录 ACL 限制。

## 代码依据

- [上游项目策略与运行时](https://github.com/mark9804/transceiver/blob/c822936958e7b084e35afaee1035f4f6ec0427a2/plugins/transceiver/scripts/reverse-runtime.mjs)
- [上游来源标识](https://github.com/mark9804/transceiver/blob/c822936958e7b084e35afaee1035f4f6ec0427a2/plugins/transceiver/scripts/reverse-provenance.mjs)与[thread 项目映射](https://github.com/mark9804/transceiver/blob/c822936958e7b084e35afaee1035f4f6ec0427a2/plugins/transceiver/scripts/reverse-router.mjs)
- [上游 App Server 持久会话调用](https://github.com/mark9804/transceiver/blob/c822936958e7b084e35afaee1035f4f6ec0427a2/plugins/transceiver/dist/reverse-bridge/index.mjs)：`startThreadAndTurn` 显式设置 `ephemeral:false`
- 本仓 `NativeCodexTaskBridge.cs`、`ProjectTaskBridgeFactory.cs`、`AppServerProtocolGuard.cs`、`SharedCodexTaskTools.cs`
- [OpenAI App Server 协议](https://developers.openai.com/codex/app-server)与[Windows 沙盒说明](https://developers.openai.com/codex/windows/windows-sandbox)
