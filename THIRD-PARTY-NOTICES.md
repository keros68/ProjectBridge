# 第三方组件

ProjectBridge 自有代码采用 MIT 许可证，见 [LICENSE](LICENSE)。发布包附带以下独立组件，各组件沿用自身许可证。ProjectBridge 以独立进程方式调用这些组件，未合并其源码。

| 组件 | 包内位置 | 版本 | 来源 | 许可证 | 修改 |
| --- | --- | --- | --- | --- | --- |
| Transceiver | `backends/transceiver` | 0.2.0 之后的 commit `c822936958e7b084e35afaee1035f4f6ec0427a2` | <https://github.com/mark9804/transceiver> | AGPL-3.0 | 无 |
| codex-with-chatgpt | `backends/codex-with-chatgpt` | 0.1.3 之后的 commit `9663b88753e35c76796c5bce000293e0bd22cd9e` | <https://github.com/XiaoDuoYa/codex-with-chatgpt> | MIT | 无 |
| tunnel-client | `runtime/tunnel-client.exe` | v0.0.13，SHA-256 `83f08fb39b1c154747debd31b81b65dd4ee834cacf5a073b6301b2688699bc76` | OpenAI Tunnel 官方安装包 | Apache-2.0 | 无 |
| cloudflared | `runtime/cloudflared.exe` | 2026.8.2，commit `733bfb939963e150dcf5c4faddb1603f744fbc98`，SHA-256 `cd0bc49aec24d0d62a37b6e842dad46000f7b401f6167b4cbace4e170819c9a2` | <https://github.com/cloudflare/cloudflared>，随 tunnel-client v0.0.13 分发 | Apache-2.0 | 无 |

## 许可证文件位置

- Transceiver：`backends/transceiver/LICENSE`；其内嵌依赖见 `plugins/transceiver/THIRD_PARTY_NOTICES.md` 和 `plugins/transceiver/dist/reverse-bridge/licenses.txt`。
- codex-with-chatgpt：`backends/codex-with-chatgpt/LICENSE`；运行依赖的许可证保留在 `node_modules` 各包目录中。
- tunnel-client 与 cloudflared：`runtime/LICENSE`、`runtime/NOTICE`（tunnel-client），依赖许可证清单 `runtime/tunnel-client-v0.0.13-windows-amd64-licenses.txt`，SPDX 清单 `runtime/tunnel-client-v0.0.13-windows-amd64.spdx.json`，cloudflared 构建信息 `runtime/cloudflared-manifest.json`。

## Transceiver 源码

Transceiver 采用 AGPL-3.0。发布包中的 `backends/transceiver` 是上述 commit 的完整源码副本，包含构建脚本、依赖锁定文件和预构建的 `dist/reverse-bridge`，详见该目录下的 `SOURCE-INFO.md`。同一源码也可从上游仓库按 commit 免费获取。
