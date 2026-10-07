<!-- markdownlint-disable -->

<div align="center">

<img alt="DSH Desktop" src="Docs/deepseek_big_fish_512x512.png" width="160" height="160" />

# DSH Desktop

<br>

<div>
    <a href="#-project-status"><img alt="Version 0.1.0-rc.1" src="https://img.shields.io/badge/version-0.1.0--rc.1-blue"></a>
    <a href="https://dotnet.microsoft.com/"><img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4"></a>
    <a href="https://avaloniaui.net/"><img alt="Avalonia" src="https://img.shields.io/badge/Avalonia-12.1-7B2CBF"></a>
    <a href="./LICENSE"><img alt="License" src="https://img.shields.io/badge/license-Apache_2.0-green"></a>
</div>

<br>

<!-- markdownlint-restore -->

English | [简体中文](Docs/README.zh-CN.md)

</div>

**DSH Desktop** is an independent native desktop client for [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness), built with .NET 10 and Avalonia.

It brings Harness conversations and agent activity to a native desktop interface. The existing Node-based Harness backend remains the source of truth for sessions and agent execution.

> This project is independent and is not affiliated with, sponsored by, or endorsed by DeepSeek AI.

## 🚧 Project Status

DSH Desktop is in early development. Interfaces, setup steps, and backend compatibility may change between releases.

Windows is the primary development and validation platform. Release packages target Windows x64, macOS Apple Silicon, and Linux x64; macOS and Linux still await their first real GitHub runner builds.

## ✨ Features

- **Conversations and workspaces** — Browse and organize sessions, and follow streaming conversations.
- **Agent activity** — Inspect tool execution, task progress, and approval requests.
- **Files and usage** — Review workspace changes and delivered files, with conversation usage statistics.
- **Models and preferences** — Configure available models, permissions, and desktop settings.

## 🗺️ Limitations

- Attachments, user-question prompts, an integrated terminal, and plugin management are not yet available.
- Some provider and agent-preset management, and browsing or restoring archived sessions, are not yet supported. Drafts last only while the application is running.
- File-change tracking is best effort; local diffs are temporary and session history does not preserve complete file snapshots.
- macOS and Linux remain unverified. Installers and automatic updates are not provided.

See the [backend compatibility record](Docs/backend-compatibility.md) for API coverage and validation details.

## 🚀 Download and Development

### Download

Download portable ZIP packages for Windows x64, macOS Apple Silicon, and Linux x64 from the [GitHub Releases page](https://github.com/banned2054/dshdesktop/releases). Extract the full package and open `DshDesktop.exe` on Windows, `DshDesktop.app` on macOS, or run `DshDesktop` on Linux. Keep the accompanying `backend` resources beside the app; no .NET or Node installation is required. See the [release build instructions](Docs/releasing.md) for package details and platform validation status.

### Build and run from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The default simulated backend lets you run the app without a local Harness runtime:

```powershell
dotnet build DshDesktop.slnx
dotnet run --project DshDesktop
```

Using a separately built Harness runtime requires Node.js. See the [backend compatibility record](Docs/backend-compatibility.md) for backend requirements and validation scope.

## 🖥️ Platform Status

| Platform | Status |
| --- | --- |
| Windows x64 | Development, real-backend, and Native AOT verification recorded; see the compatibility record for scope and remaining checks |
| macOS Apple Silicon | Release workflow configured; build and runtime validation pending |
| Linux x64 | Release workflow configured; build and runtime validation pending |

## ⚖️ License

Licensed under the [Apache License 2.0](./LICENSE). See [NOTICE](./NOTICE) for upstream acknowledgements and third-party notices.

## 🤝 Contributing

Focused issues and pull requests are welcome. For behavior changes, include tests where practical and distinguish implemented behavior from unverified behavior. Update the compatibility record when backend dependencies or client-specific behavior change.
