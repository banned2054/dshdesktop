# 发布构建

发布流程为三个独立的 Native AOT 便携 ZIP：Windows x64（`win-x64`）、macOS Apple Silicon（`osx-arm64`）和 Linux x64（`linux-x64`）。每包包含客户端、DSH 生产依赖、Node、pnpm、Python 和上游 Office skill 资产。Windows/Linux 直接运行包内客户端可执行文件；macOS 提供可双击的 `DshDesktop.app`。后端资源随包提供，应用自动按自身位置查找，不需要启动脚本。不生成 MSI/安装器，不签名，不提供自动更新。

## 两套版本

- 客户端版本：`DshDesktop/DshDesktop.csproj` 的 `<Version>`。Release 标签必须为 `v<Version>`，例如 `v0.1.0` 或 `v0.1.0-rc.1`。
- DSH 版本：根目录 `backend-version.json`。`version` 是包版本，`ref` 是对应上游标签，`commit` 是该标签指向的完整 SHA。
- About 面板继续读取实际 runtime 内的 DSH `package.json`，不会用声明的版本掩盖运行包的版本。
- Node/Python 版本和下载校验和来自固定 DSH commit 下的 `scripts/primary-runtime/lock.json`；构建用 pnpm 版本来自上游 `packageManager`。

升级 DSH 时一起更新这三个字段，并按 [后端兼容性记录](backend-compatibility.md) 完成相应验证。每个平台 workflow 都检查上游 HEAD、标签 commit、根包/CLI/Host 版本以及最终包版本，任何不一致均失败。

## GitHub Actions

[release-windows-x64.yml](../.github/workflows/release-windows-x64.yml)、[release-macos-arm64.yml](../.github/workflows/release-macos-arm64.yml) 和 [release-linux-x64.yml](../.github/workflows/release-linux-x64.yml) 是三个独立的 workflow。每个 workflow 仅构建自身 RID，不等待其他平台：

1. **手动运行**：`workflow_dispatch` 使用所选分支/标签构建，并只上传 Actions artifact，不上传 Release 资产；手动运行没有明确的 Release 目标标签。
2. **发布 GitHub Release**：正式版和预发布版的 `release.published` 事件分别触发三个 workflow，各自检出对应 Release 标签。平台构建、打包及 smoke 全部成功后，直接将该平台 ZIP 和 `.sha256` 附加到同一个 Release；使用 `gh release upload --clobber`，重跑会覆盖相同 RID 的同名资产。不会修改 Release 正文，也不会创建新 Release 或标签。

三个 workflow 各自声明最低限度的 `contents: write` 权限，以便 Release 事件上传资产；手动触发仍只发布 Actions artifact。各平台互不依赖，因此某个平台完成后即可附加资产，不必等待其他 runner。失败时 Release 本身已发布，需查看对应 workflow 日志并重跑。

每个平台均读取同一客户端与 DSH 锁定版本、检出固定 DSH commit、安装锁定 Node/pnpm、运行发布工具测试和客户端测试、构建上游 DSH、执行 Native AOT 发布及后端打包 smoke。Windows 使用 `windows-2022`；macOS arm64 使用 `macos-14`；Linux x64 使用 `ubuntu-24.04`，并安装 clang 与 zlib Native AOT 依赖。

打包脚本先准备上游运行资产，再执行 `pnpm deploy --legacy --prod` 导出生产依赖。这个顺序必须保留：pnpm 11 的 legacy deploy 会写回生产模式的工作区状态，之后再执行 `pnpm run` 可能触发自动生产安装，导致运行资产准备所需的开发依赖缺失。

打包后会解压 ZIP 到另一个目录，使用包内 Node 和 launcher 检查 `ready`、`shutdown-complete` 与退出码 0，成功后生成 ZIP 的 SHA256。后端 smoke 使用独立临时 home/profile，不读取个人会话或调用模型，也不输出含认证 token 的 ready URL。它只验证后端启动/关停，不能替代 GUI 或真实模型交互；macOS 另检查 `.app` 目录结构和入口文件。

Windows 是已有开发与 Native AOT 验证平台。**macOS arm64 和 Linux x64 尚未经过首次真实 GitHub runner 构建；在对应 workflow 成功运行之前，不能标记为已验证。**静态检查、单元测试或 Windows 构建不能替代这两项平台验收。

## 使用发布包

完整解压 ZIP 到可写目录并保留全部内容。Windows 双击 **`DshDesktop.exe`**，macOS 在 Finder 打开 **`DshDesktop.app`**，Linux 直接运行 **`DshDesktop`**。应用根据自身位置自动发现包内 `backend` 目录、Node 和 launcher，不依赖当前工作目录，也不需要安装 .NET 或 Node。macOS `.app` 的主程序位于 `Contents/MacOS`，后端与相关资源位于 `Contents/Resources`。

会话和凭据沿用 `DSH_HOME` 或 `~/.dsh`，不会放进发布包；客户端私有 profile 仍写入应用数据目录。`BUILD-INFO.json` 记录客户端/DSH/Node 版本与 DSH commit。

## 本地复现

本地构建要求目标平台 runner/工具链、.NET 10 Native AOT 工具链、上游锁定版本的 Node/pnpm，以及独立的 DSH 源码 checkout。先在该 checkout 按锁定 commit 运行 `pnpm install --frozen-lockfile` 与 `pnpm run build`，再回到本仓库执行：

```powershell
node tools/release.mjs source C:\path\to\deepseek-harness
node --test tools/release.test.mjs
./tools/build-release.ps1 -UpstreamDir C:\path\to\deepseek-harness -OutputRoot C:\path\to\new-output-directory -Rid win-x64
```

将 `-Rid` 改为 `osx-arm64` 或 `linux-x64` 可选择其他目标，但交叉编译不会代替目标平台 runner 上的真实构建与 smoke 验证。输出目录必须不存在，脚本不删除旧目录。准备解释器和依赖导出仍会联网；全量打包可能占用较多磁盘空间。仓库中的开发 runtime、用户 home、环境文件和凭据不参与发布。
