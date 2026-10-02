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
- Session browsing in a single list or grouped by workspace, with title search and native folder selection to register workspaces; in grouped mode, unpinned workspaces are collected under a single "Workspaces" category.
- A hover menu on workspace rows for pinning, renaming, and deleting workspaces (pinned workspaces join the sidebar pinned section; rename validates conflicts in a dialog; delete only removes the registration, keeping the folder and its sessions).
- A hover action strip on session rows with a menu, archive, and pin buttons that replaces the relative time; the menu renames the session (the backend normalizes the title) and forks a session by copying its latest completed turn into a new session with an incremented title; archived sessions leave the list while their records are kept (archiving the current session returns to the draft page).
- Pinning is project-local (the backend pin registry is not consumed) and persisted in the project's own config file: a pinned section above the workspace grouping shows pinned workspaces (groups first) and pinned sessions (below), extracted from their original spots and sorted by update time within each kind; a pinned session of a pinned workspace sits parallel to the workspace, without conflict. The pinned section, the "Workspaces" section, and "Ungrouped" are all plain-text category rows: an expand arrow to the right of the title, no icon, no hover or selection highlight.
- A new-conversation draft with workspace, agent mode, model, and permission choices; the Harness session is created on the first send.
- History loading, streaming replies, cancellation, and connection recovery.
- Markdown responses, reasoning details, interrupted-response states, and paged history.
- Tool-call progress, results, errors, and collapsible per-turn process details.
- Model selection with reasoning levels filtered by model capabilities, plus token usage, cache-hit rate, and generation-speed statistics.
- Session permission presets and local draft preselection, with confirmation for full-access or automatic modes and backend projections confirming the active session value.
- Tool approval prompts with allow-once and reject actions.
- A settings entry at the right of the sidebar status bar: opens the DSH settings document (materialized by the backend, then launched in the system editor).
- Explicit Harness host startup, authentication, shutdown, and child-process cleanup.
- Compiled XAML bindings, source-generated JSON serialization, and previous Windows x64 Native AOT publishing verification. Recent changes have not all been republished with AOT.

## 🗺️ Current Limitations

- User-question prompts, attachments, the settings panel, and preference persistence are not implemented (a settings-document open entry exists; the panel UI and preference persistence are still missing). Drafts are retained only within the running application.
- Workspace support covers registration, selection, session grouping, renaming, and deletion; session pin, archive, fork, and rename plus workspace pinning are wired up, while unarchive entry and archived filtering are not yet implemented; there is no custom agent-preset catalog.
- Terminal, file and diff previews, and plugin-management interfaces are not implemented.
- Reconnect behavior, long-session performance, shortcuts, input methods, and some popup and rich-text interactions still need further GUI or real-backend validation.
- macOS and Linux have not been verified; stable installers and automatic updates are not provided.

Development follows individual feature, GUI, and fix requests. Features may extend beyond DSH's own interface. The [backend compatibility record](Docs/backend-compatibility.md) documents consumed APIs, client-specific behavior, historical validation, and what to check when upgrading DSH; it does not prescribe a feature roadmap.

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
| Windows x64 | Prior development, real-backend, and Native AOT verification recorded; see the compatibility record for scope and remaining checks |
| macOS | Planned; not yet verified |
| Linux | Planned; not yet verified |

## ⚖️ License

Licensed under the [Apache License 2.0](./LICENSE). See [NOTICE](./NOTICE) for upstream acknowledgements and third-party notices.

## 🤝 Contributing

The project is still establishing its core behavior. Focused issues and pull requests are welcome; for behavior changes, please include tests where practical and distinguish implemented behavior from unverified behavior. Update the compatibility record when backend dependencies or client-specific behavior change.
