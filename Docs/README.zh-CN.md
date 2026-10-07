<!-- markdownlint-disable -->

<div align="center">

<img alt="DSH Desktop" src="deepseek_big_fish_512x512.png" width="160" height="160" />

# DSH Desktop

<br>

<div>
    <a href="#-项目状态"><img alt="版本 0.1.0-rc.1" src="https://img.shields.io/badge/version-0.1.0--rc.1-blue"></a>
    <a href="https://dotnet.microsoft.com/"><img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4"></a>
    <a href="https://avaloniaui.net/"><img alt="Avalonia" src="https://img.shields.io/badge/Avalonia-12.1-7B2CBF"></a>
    <a href="../LICENSE"><img alt="License" src="https://img.shields.io/badge/license-Apache_2.0-green"></a>
</div>

<br>

<!-- markdownlint-restore -->

[English](../README.md) | 简体中文

</div>

**DSH Desktop** 是一个面向 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 的独立原生桌面客户端，使用 .NET 10 和 Avalonia 构建。

它将 Harness 对话和 Agent 执行过程带到原生桌面界面中。现有的 Node Harness 后端仍然是会话和 Agent 执行状态的权威来源。

> 本项目是独立项目，与 DeepSeek AI 没有隶属、赞助或官方背书关系。

## 🚧 项目状态

DSH Desktop 仍处于早期开发阶段，界面、配置方式和后端兼容性可能随版本变化。

Windows 是主要开发和验证平台。发布包面向 Windows x64、macOS Apple Silicon 和 Linux x64；macOS 和 Linux 仍待首次真实 GitHub runner 构建验证。

## ✨ 主要功能

- **对话与工作区** — 浏览和整理会话，进行流式对话。
- **Agent 执行过程** — 查看工具执行、任务进度和审批请求。
- **文件与用量** — 检查工作区改动和交付文件，并查看对话用量统计。
- **模型与偏好设置** — 配置可用模型、权限和桌面设置。

## 🗺️ 当前限制

- 暂不支持附件、用户问题交互、集成终端和插件管理。
- 部分提供商与 Agent 预设管理功能，以及浏览或恢复已归档会话的能力尚未支持。草稿仅在应用运行期间保留。
- 文件改动跟踪尽力捕获变化；本地差异为临时数据，会话历史不保存完整文件快照。
- macOS 和 Linux 尚未验证，暂不提供安装器和自动更新。

API 覆盖范围和验证细节见[后端兼容性记录](backend-compatibility.md)。

## 🚀 下载与开发

### 下载

Windows x64、macOS Apple Silicon 和 Linux x64 的便携 ZIP 可从 [GitHub Releases 页面](https://github.com/banned2054/dshdesktop/releases)下载。完整解压后，Windows 双击 `DshDesktop.exe`，macOS 打开 `DshDesktop.app`，Linux 直接运行 `DshDesktop`。请保留程序旁边的 `backend` 资源目录；运行发布包不需要另外安装 .NET 或 Node。打包信息和平台验证状态见[发布构建说明](releasing.md)。

### 从源码构建与启动

需要安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。应用默认使用模拟后端，无需准备本地 Harness Runtime：

```powershell
dotnet build DshDesktop.slnx
dotnet run --project DshDesktop
```

连接单独构建的 Harness Runtime 需要 Node.js。后端要求与验证范围见[后端兼容性记录](backend-compatibility.md)。

## 🖥️ 平台状态

| 平台 | 状态 |
| --- | --- |
| Windows x64 | 已有开发构建、真实后端和 Native AOT 验证记录；具体范围与待验证项见兼容性记录 |
| macOS Apple Silicon | 已配置发布流程，构建与运行验证待完成 |
| Linux x64 | 已配置发布流程，构建与运行验证待完成 |

## ⚖️ 许可证

本项目采用 [Apache License 2.0](../LICENSE) 授权。[NOTICE](../NOTICE) 包含上游致谢和第三方声明。

## 🤝 参与贡献

欢迎提交范围明确的 Issue 和 Pull Request。涉及行为变化时，请尽量补充测试，并区分已实现与未验证的行为。后端依赖或本端特有行为变化时，请同步更新兼容性记录。
