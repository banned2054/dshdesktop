# 发布构建

当前流程生成 **Windows x64 Native AOT 便携 ZIP**，包含客户端、DSH 生产依赖、Node、pnpm、Python 和上游 Office skill 资产。不生成 MSI/安装器，不签名，不提供自动更新；macOS/Linux 不在本流程内。

## 两套版本

- 客户端版本：`DshDesktop/DshDesktop.csproj` 的 `<Version>`。Release 标签必须为 `v<Version>`，例如 `v0.1.0` 或 `v0.1.0-rc.1`。
- DSH 版本：根目录 `backend-version.json`。`version` 是包版本，`ref` 是对应上游标签，`commit` 是该标签指向的完整 SHA。
- About 面板继续读取实际 runtime 内的 DSH `package.json`，不会用声明的版本掩盖运行包的版本。
- Node/Python 版本和下载校验和来自固定 DSH commit 下的 `scripts/primary-runtime/lock.json`；构建用 pnpm 版本来自上游 `packageManager`。

升级 DSH 时一起更新这三个字段，并按 [后端兼容性记录](backend-compatibility.md) 完成相应验证。workflow 检查上游 HEAD、标签 commit、根包/CLI/Host 版本以及最终包版本，任何不一致均失败。

## GitHub Actions

[release.yml](../.github/workflows/release.yml) 有两个入口：

1. **手动运行 `Build release`**：构建所选分支/标签，只上传 Actions artifact，不创建或修改 GitHub Release。可先用这一入口验证构建。
2. **发布 GitHub Release**：`release.published` 触发，包括正式和预发布版本，检出该 Release 标签。成功后附加 ZIP 和 `.sha256`；重跑会覆盖同名资产。不会修改 Release 正文，也不会创建新 Release 或标签。

构建步骤只有 `contents: read`；附加资产的独立 job 有 `contents: write`，使用内置 `GITHUB_TOKEN`。发布期间 ZIP 资产要等构建完成才出现；失败时 Release 本身已发布，需查看日志后重跑。

流程先运行客户端 Release 测试和打包脚本测试，再以固定 commit 的 lockfile 安装/构建上游，导出私有 desktop-host 的生产依赖，使用上游校验锁准备解释器与 Office 资产，然后发布客户端 AOT 产物。pnpm 导出的链接会物化成普通文件，最终包不包含本机 junction。

打包后解压到另一个带空格的路径，使用包内 Node 和 launcher 检查 `ready`、`shutdown-complete` 与退出码 0，成功后生成 ZIP 的 SHA256。后端冒烟使用独立临时 home/profile，不读取个人会话或调用模型，也不输出含认证 token 的 ready URL。

这项冒烟只验证后端启动/关停，不能替代 GUI、真实模型交互或其他平台验收。本流程尚未在 GitHub runner 上实际执行；本地脚本检查和单元测试不代表完整发布成功。

## 使用发布包

完整解压 ZIP 到可写目录后运行 **`Run.cmd`**。启动脚本将后端路径指向包内目录，不需要安装 .NET 或 Node。直接运行 `DshDesktop.exe` 仍按现有环境变量规则选择后端。

会话和凭据沿用 `DSH_HOME` 或 `~/.dsh`，不会放进发布包；客户端私有 profile 仍写入应用数据目录。`BUILD-INFO.json` 记录客户端/DSH/Node 版本与 DSH commit。

## 本地复现

需要 Windows x64、.NET 10 Native AOT 工具链、上游锁定版本的 Node/pnpm，以及独立的 DSH 源码 checkout。先在该 checkout 按锁定 commit 运行 `pnpm install --frozen-lockfile` 与 `pnpm run build`，再回到本仓库执行：

```powershell
node tools/release.mjs source C:\path\to\deepseek-harness
node --test tools/release.test.mjs
./tools/build-release.ps1 -UpstreamDir C:\path\to\deepseek-harness -OutputRoot C:\path\to\new-output-directory
```

输出目录必须不存在，脚本不删除旧目录。准备解释器和依赖导出仍会联网；全量打包可能占用较多磁盘空间。仓库中的开发 runtime、用户 home、环境文件和凭据不参与发布。
