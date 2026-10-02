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
- 展示工具调用进度、结果和错误，按轮次折叠执行过程。
- 选择模型并按模型能力过滤推理档位，展示 Token 用量、缓存命中率和生成速度。
- 查看和切换会话权限预设，支持草稿页本地预选；完全权限和自动模式需确认，会话生效值由后端投影确认。
- 处理工具审批，支持“允许一次”和“拒绝”。
- 侧栏底部状态栏提供设置入口：打开 DSH 设置文档（后端物化后调起系统编辑器）。
- 明确管理 Harness Host 的启动、认证、关闭和子进程清理。
- 使用 XAML 编译绑定和 JSON 源生成，已有 Windows x64 Native AOT 发布验证记录；近期修改尚未全部重新发布验证。

## 🗺️ 当前限制

- 用户问题交互、附件、设置面板和偏好持久化尚未实现（设置文档打开入口已接入，设置面板界面与偏好持久化仍缺）；草稿只在当前进程内保留。
- 工作区支持登记、选择、会话分组、重命名和删除；会话置顶/归档/分叉/重命名与工作区置顶已接入；取消归档入口与已归档筛选尚未实现；自定义 Agent 预设目录尚未接入。
- 终端、文件与差异预览、插件管理界面尚未实现。
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

真实后端开发环境目前要求可以从 `PATH` 找到 Node.js，并复用 `DSH_HOME` 或 `~/.dsh` 中的 Harness 数据。Runtime 获取和面向最终用户的分发流程尚未自动化。

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
