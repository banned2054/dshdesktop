<!-- markdownlint-disable -->

<div align="center">

<img alt="DSH Desktop" src="Docs/deepseek_big_fish_512x512.png" width="160" height="160" />

# DSH Desktop

<br>

<div>
    <a href="#-project-status"><img alt="Development status" src="https://img.shields.io/badge/status-early_development-orange"></a>
    <a href="https://dotnet.microsoft.com/"><img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4"></a>
    <a href="https://avaloniaui.net/"><img alt="Avalonia" src="https://img.shields.io/badge/Avalonia-12.1-7B2CBF"></a>
    <a href="./LICENSE"><img alt="License" src="https://img.shields.io/badge/license-Apache_2.0-green"></a>
</div>

<br>

<!-- markdownlint-restore -->

English | [简体中文](Docs/README.zh-CN.md)

</div>

**DSH Desktop** is an independent native desktop client for [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness), built with .NET 10, Avalonia, and MVVM.

It provides a desktop interface for browsing Harness sessions, holding streaming conversations, and following tool activity without using a browser or WebView as the application shell. The existing Node-based Harness backend remains the source of truth for sessions and agent execution.

> This project is independent and is not affiliated with, sponsored by, or endorsed by DeepSeek AI.

## 🚧 Project Status

DSH Desktop is in early development. Core conversation workflows are taking shape, but there is no stable release or installer yet. Interfaces, setup steps, and backend compatibility may change while development continues.

Windows is the current development and validation platform. macOS and Linux are intended targets but have not yet been verified.

## ✨ Current Capabilities

- Native Avalonia interface with no browser or WebView shell.
- Session browsing in a single list or grouped by workspace.
- Session creation, history loading, streaming replies, cancellation, and connection recovery.
- Markdown responses, reasoning details, interrupted-response states, and paged history.
- Tool-call progress, results, errors, and collapsible per-turn process details.
- Per-session model selection, token usage, cache-hit rate, and generation-speed statistics.
- Tool approval prompts with allow-once and reject actions.
- Explicit Harness host startup, authentication, shutdown, and child-process cleanup.
- Compiled XAML bindings, source-generated JSON serialization, and verified Windows x64 Native AOT publishing.

## 🗺️ Planned

- Complete the remaining conversation interactions, including user-question prompts.
- Finish workspace management, settings, and preference persistence.
- Validate reconnect behavior, long-session performance, keyboard shortcuts, and input methods.
- Build and test native releases independently on macOS and Linux.
- Add packaging, updates, and other distribution features after the core workflow stabilizes.

The detailed implementation stages and validation notes are tracked in the [development plan](plan.md).

## 🚀 Development

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows for the currently verified development path
- Node.js and a separately built DeepSeek Harness runtime only when using the real backend

### Build and run

```powershell
dotnet build DshDesktop.slnx
dotnet run --project DshDesktop
```

The application uses its simulated backend by default, so the interface can be developed without a local Harness runtime.

To use a separately built Harness runtime, select the real backend and provide its directory before launching:

```powershell
$env:DSH_DESKTOP_BACKEND_MODE = "real"
$env:DSH_DESKTOP_RUNTIME_DIR = "C:\path\to\deepseek-harness-runtime"
dotnet run --project DshDesktop
```

Real-backend development currently expects Node.js on `PATH` and reuses the Harness home at `DSH_HOME` or `~/.dsh`. Runtime acquisition and end-user distribution are not automated yet.

## 🖥️ Platform Status

| Platform | Status |
| --- | --- |
| Windows x64 | Development builds, real-backend integration, and Native AOT publishing verified |
| macOS | Planned; not yet verified |
| Linux | Planned; not yet verified |

## ⚖️ License

Licensed under the [Apache License 2.0](./LICENSE). See [NOTICE](./NOTICE) for upstream acknowledgements and third-party notices.

## 🤝 Contributing

The project is still establishing its core behavior. Focused issues and pull requests are welcome; for behavior changes, please include tests where practical and clearly distinguish implemented behavior from planned work.
