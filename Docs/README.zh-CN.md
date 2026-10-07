<!-- markdownlint-disable -->

<div align="center">

<img alt="DSH Desktop" src="deepseek_big_fish_512x512.png" width="160" height="160" />

# DSH Desktop

<br>

<div>
    <a href="#-项目状态"><img alt="开发状态" src="https://img.shields.io/badge/状态-早期开发-orange"></a>
    <a href="https://dotnet.microsoft.com/"><img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4"></a>
    <a href="https://avaloniaui.net/"><img alt="Avalonia" src="https://img.shields.io/badge/Avalonia-12.1-7B2CBF"></a>
    <a href="../LICENSE"><img alt="License" src="https://img.shields.io/badge/license-Apache_2.0-green"></a>
</div>

<br>

<!-- markdownlint-restore -->

[English](../README.md) | 简体中文

</div>

**DSH Desktop** 是一个面向 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 的独立原生桌面客户端，使用 .NET 10、Avalonia 和 MVVM 构建。

它为浏览 Harness 会话、进行流式对话和查看工具执行过程提供桌面界面，不使用浏览器或 WebView 作为应用外壳。现有的 Node Harness 后端仍然是会话和 Agent 执行状态的权威来源。

> 本项目是独立项目，与 DeepSeek AI 没有隶属、赞助或官方背书关系。

## 🚧 项目状态

DSH Desktop 仍处于早期开发阶段。核心对话流程正在逐步成形，但目前没有稳定版本或安装包；在继续开发期间，界面、配置方法和后端兼容性都可能发生变化。

Windows 是当前的主要开发和验证平台。macOS 和 Linux 是计划支持的目标平台，但尚未完成验证。

## ✨ 当前能力

- 基于 Avalonia 的原生界面，不使用浏览器或 WebView 外壳。
- 以单列表或按工作区分组的方式浏览会话，支持标题搜索和原生目录选择器登记工作区；分组模式下未被置顶的工作区统一收进「工作区」分类。
- 工作区行悬浮菜单支持置顶、重命名和删除工作区（置顶进入侧栏置顶分类；重命名弹窗校验重名；删除仅移出列表，目录与会话保留）。
- 会话行悬浮操作条支持置顶和归档：悬停时相对时间让位给三点菜单、归档、置顶三个按钮；菜单里的重命名可修改会话标题（服务端规范化后生效），分叉会话以最近完成轮次为界复制出新会话并递增标题；归档行移出列表（记录保留，归档当前会话后回到草稿页）。
- 置顶是本项目自有方案（不消费后端置顶集合），数据持久化在本项目配置文件：侧栏顶部有高于工作区一层的置顶分类，置顶的工作区（组在上）与置顶的会话（区在下）提取到该分类显示、原位置不再出现，同类按更新时间排序；置顶工作区与其中某条置顶会话互不冲突（会话与工作区并列）。置顶分类、「工作区」分类与「未分组」均为纯文字分类行：文字右侧展开箭头、无图标、无悬停/选中高亮。
- 新对话草稿页可预选工作区、Agent 模式、模型和权限，首发送时才创建 Harness 会话。
- 加载历史、接收流式回复、取消生成和恢复连接。
- 渲染 Markdown 回复、思考内容、中断状态，并分页加载历史记录。
- 展示工具调用进度、结果和错误；每轮过程默认显示已加载的最近 20 条，可每次再展开 20 条更早过程，完整已加载内容与可展开的思考原文仍保留；最终回复独立显示，历史分页仍单独工作。上翻阅读时固定当前可见范围，新事件不会移走正在阅读的步骤；回到底部会恢复最近 20 条，用户手动展开的范围除外。用户主动展开阅读后，轮次结束不会收起其阅读状态。
- composer 上方任务面板展示当前轮任务清单（收起态显进度摘要、可展开查看条目与状态圆点），数据来自 `todo/write` 事件；「更新任务清单」折叠行摘要显示完成比例与相对上次清单的新增/更新/移除。
- 选择模型并按模型能力过滤推理档位，展示 Token 用量、缓存命中率和生成速度。
- 真实会话在草稿首发 prompt 前捕获所选工作区，创建 session 后绑定其首轮；首次 follow 重放该轮起点及匹配轮末时也可结算。普通已选会话也在发送 prompt 前采样，并按发送顺序保留基线；只有同一 session 后续的实时 `turn/start` 可消费，replay 不消费，匹配 `turn/end` 结算。展示改动文件数、相对路径与新增/删除行数；卡片对齐 Codex「已编辑」样式：单文件显示「已编辑 文件名」整卡可点并在右侧垂直居中放「查看变更」，多文件显示「已编辑 N 个文件」与整轮增删合计，仅文件行可点击，超过 3 个文件默认只显示前 3 行并提供「再显示/收起」开关；点击文件复用右侧 Banned.CodeDiff.Avalonia 分栏 diff。本地摘要与对比取自有界缓存，不依赖 Host producer；收到真实 `workspace/changes` 宣告时可用 Host changes 接口作为补偿，同轮本地结果优先。
- 历史 `deliverables/presented` 事件会在对应轮次展示「已编辑」交付卡片，展示形态与改动卡一致：单文件渲染为「已编辑 文件名」整卡可点卡（右侧垂直居中「查看变更」按钮），多文件渲染为「已编辑 N 个文件」容器——头部静态并显示整轮增删合计，仅文件行可点击（左侧相对根目录路径、右侧 +N −N），超过 3 个文件默认只显示前 3 行并提供「再显示/收起」开关。同一路径后续声明更新其内容和描述，但保留首次出现的位置。增删行数来自同轮 workspace/changes 摘要（本端快照或 Host 宣告），匹配不到的文件不显示计数。单击文件在应用内右侧面板查看，数据按条件选择：同会话同轮 workspace/changes 含该文件时显示完整轮次差异；否则显示已加载记录中成功 `edit`/`write` 调用的“历史编辑片段”（按事件顺序分列，优先 `tool/result` 携带的 `meta.diffs`，无 meta 时回退参数 `old_string`/`new_string`，行号为片段内行号、不伪造原文件位置）；否则显示当前文件内容（明确标注非交付时快照）；都不可用时面板显示具体原因并保留卡片。“用默认程序打开”是面板内的显式按钮。会话日志只保存文件声明与编辑现场差异块，不归档当时的完整文件，因此无法还原完整历史 diff 或被覆盖写工具改写前的原文。
- 查看和切换会话权限预设，支持草稿页本地预选；完全权限和自动模式需确认，会话生效值由后端投影确认。
- 处理工具审批，支持“允许一次”和“拒绝”。
- 侧栏底部状态栏提供设置入口，打开原生设置面板（主题偏好经后端设置文档持久化）。模型分区以圆角行卡列出已配置提供商（凭据圆点 + 编辑），点编辑在行内展开编辑卡（一次一卡、同页不切换）：已配置供应商默认只显示 API 密钥，展开「自定义设置」补充显示名称、API 地址、协议与模型目录，「获取可用模型」按路由携带已存凭据探测并勾选合并进草稿；经「+ 添加模型提供商」添加内置目录厂商或自定义模型 API 路由（Provider ID、显示名称、API 地址、协议与模型目录必填）；DeepSeek 第一方编辑器（API 密钥、接口地址、支持 K/M 缩写的模型目录）从其行进入。面板 header 保留打开配置文件入口。
- 明确管理 Harness Host 的启动、认证、关闭和子进程清理。
- 使用 XAML 编译绑定和 JSON 源生成，已有 Windows x64 Native AOT 发布验证记录；近期修改尚未全部重新发布验证。

## 🗺️ 当前限制

- 用户问题交互与附件尚未实现。设置面板覆盖通用偏好、上述模型分区提供商管理与内置插件卡；提供商删除、Agent 预设管理与更多字段级覆盖尚未实现。草稿只在当前进程内保留。
- 工作区支持登记、选择、会话分组、重命名和删除；会话置顶/归档/分叉/重命名与工作区置顶已接入；取消归档入口与已归档筛选尚未实现；自定义 Agent 预设目录尚未接入。
- 终端和插件管理界面尚未实现。
- 文件快照为 best-effort：基线捕获前已发生的写入可能漏记，轮内其他程序的改动也会计入。跳过 attach/replay 历史及缺失 cwd/turn 身份的轮次；本端首发预捕获可关联新建 session 的 turn 1，普通会话预捕获只绑定同一发送之后的实时轮首，历史轮次不补采样。扫描不跟随 reparse/junction，排除 `.git`、`node_modules`、`bin`、`obj`，跳过超过 1 MiB 的文件；不可读文件/子目录在对比两侧都排除，根目录不可读或超过 4,000 文件、8,000 条目、16 MiB、2 秒时不显示摘要。二进制不提供行数和内容预览；行级计算超过预算时以变化区段替换。摘要/diff 在进程内保留，最多 128 轮、64 MiB 内容预算，淘汰或退出应用后不可用。
- 断线恢复、长会话性能、快捷键、输入法及部分弹层和富文本交互仍需补充 GUI 或真实后端验证。
- macOS 和 Linux 尚未验证，尚无稳定安装包和自动更新功能。

开发按每次明确提出的功能、GUI 或修复需求推进，功能可以超出 DSH 本体界面。[后端兼容性记录](backend-compatibility.md) 说明已消费的 API、本端特有行为、历史验证和升级 DSH 时需要检查的内容，不预设功能开发顺序。

## 🚀 开发运行

### 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 当前已验证的开发路径使用 Windows
- 仅在连接真实后端时需要 Node.js 和单独构建的 DeepSeek Harness Runtime

### 构建与启动

```powershell
dotnet build DshDesktop.slnx
dotnet run --project DshDesktop
```

应用默认使用内置模拟后端，因此开发界面时不需要准备本地 Harness Runtime。

如需连接单独构建的 Harness Runtime，请在启动前选择真实后端并指定其目录：

```powershell
$env:DSH_DESKTOP_BACKEND_MODE = "real"
$env:DSH_DESKTOP_RUNTIME_DIR = "C:\path\to\deepseek-harness-runtime"
dotnet run --project DshDesktop
```

真实后端开发环境目前要求可以从 `PATH` 找到 Node.js，并复用 `DSH_HOME` 或 `~/.dsh` 中的 Harness 数据。[发布流程](../.github/workflows/release.yml) 可构建包含固定 DSH runtime 和解释器的 Windows x64 Native AOT 便携 ZIP，完整解压后运行 `Run.cmd`。该流程尚未在 GitHub runner 上验证；暂不提供安装器和自动更新，详见 [发布构建说明](releasing.md)。

## 🖥️ 平台状态

| 平台 | 状态 |
| --- | --- |
| Windows x64 | 已有开发构建、真实后端和 Native AOT 验证记录；具体范围与待验证项见兼容性记录 |
| macOS | 计划支持，尚未验证 |
| Linux | 计划支持，尚未验证 |

## ⚖️ License

本项目采用 [Apache License 2.0](../LICENSE) 授权。[NOTICE](../NOTICE) 包含上游致谢和第三方声明。

## 🤝 Contributing

项目目前仍在建立核心行为。欢迎提交范围明确的 Issue 和 Pull Request；涉及行为变化时，请尽量补充测试，区分已实现与未验证的行为。后端依赖或本端特有行为变化时同步兼容性记录。
