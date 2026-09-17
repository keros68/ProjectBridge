# Transceiver 源码说明

- 上游仓库：<https://github.com/mark9804/transceiver>
- 版本：0.2.0 之后的 commit `c822936958e7b084e35afaee1035f4f6ec0427a2`
- 许可证：AGPL-3.0，全文见本目录 `LICENSE`
- ProjectBridge 的修改：无
- 对应源码：本目录即该 commit 的完整源码，不含 `.git` 与 `node_modules`。`plugins/transceiver/dist/reverse-bridge` 由 `plugins/transceiver/scripts/build-reverse.mjs` 构建，依赖版本见 `plugins/transceiver/package-lock.json`。

ProjectBridge 以独立 Node.js 进程启动 Transceiver，未合并其源码。
