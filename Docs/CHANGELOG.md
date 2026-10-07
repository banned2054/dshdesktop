# Changelog

## 📘 Versions

- [v0.1.0-rc.1](#-release-v010-rc1)

## 🚀 Release v0.1.0-rc.1

[English](#v010-rc1-english) | [简体中文](#v010-rc1-zh-cn)

<a id="v010-rc1-english"></a>
### English

This is the first pre-release of DSH Desktop, a native desktop client for DeepSeek Harness. The project is in early development; its interface, configuration, and backend compatibility may continue to change.

#### ✨ Added

- Native desktop conversation UI with session browsing, streaming responses, history, thinking content, and tool activity.
- Workspace session management and new-conversation selection for workspace, Agent mode, model, and permissions.
- Unified edited-files cards for workspace changes and delivered files while preserving their independent data sources and open behavior.
- In-app workspace diffs; delivered files show current disk content, with verified historical edit fragments or an explanation when the file is unavailable.
- Collapsible and incrementally expandable process history for long conversations.
- Model provider settings and discovery, permission selection, and tool approval interactions.

#### ⚠️ Platform and limitations

- The release publishes Native AOT portable ZIP packages for Windows x64, macOS, and Linux; there is no installer, code signing, or automatic update support.
- Only the Windows x64 package has been verified; the macOS and Linux packages have not been verified.
- Delivery declarations do not archive file contents. The current-file view shows the contents on disk when read and may differ from the delivered version.
- File changes and historical fragments depend on local snapshots, loaded history, and backend data availability; they cannot reconstruct every historical file state.
- The GitHub Actions release workflow has not yet been verified, and recent changes have not all been revalidated in a Native AOT release build.

<a id="v010-rc1-zh-cn"></a>
### 简体中文

这是 DSH Desktop 的首个预发布版本，为 DeepSeek Harness 提供原生桌面客户端。项目仍处于早期开发阶段，界面、配置方式和后端兼容性可能继续变化。

#### ✨ 新增与改进

- 提供原生桌面会话界面，支持会话浏览、流式对话、历史记录、思考内容和工具执行过程展示。
- 支持工作区会话管理，以及新对话的工作区、Agent 模式、模型和权限选择。
- 展示工作区改动与交付文件卡片；同轮内容合并为统一的已编辑文件卡，同时保留各自的数据来源和打开行为。
- 工作区改动可在应用内查看文件差异；交付文件可查看当前磁盘文本，文件不可用时按可用记录展示历史编辑片段或原因说明。
- 长对话过程支持折叠和分段展开。
- 提供模型提供商设置、模型发现、权限选择和工具审批交互。

#### ⚠️ 平台与已知限制

- 发布提供 Windows x64、macOS 和 Linux 的 Native AOT 便携 ZIP 包；不提供安装器、签名或自动更新。
- 仅 Windows x64 包经过验证，macOS 和 Linux 包尚未验证。
- 交付文件声明不保存交付时的文件快照；当前文件视图展示读取时的磁盘内容，可能与交付时不同。
- 文件改动和历史片段受本地快照、已加载记录及后端数据可用性限制，不能保证还原所有历史文件状态。
- GitHub Actions 发布流程尚未实际验证；近期代码变更尚未全部重新进行 Native AOT 发布验证。
