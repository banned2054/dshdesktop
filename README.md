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
- Tool-call progress, results, errors, and per-turn process details that initially show the latest 20 loaded entries with 20-entry "show earlier" batches; the full loaded process and expandable reasoning remain available, final answers stay separate, and history paging remains independent. Scrolling up pins the current visible range so new events do not remove the steps being read; returning to the bottom restores the latest 20 unless the user manually expanded the process. A user's expanded reading state is preserved when the turn ends.
- Model selection with reasoning levels filtered by model capabilities, plus token usage, cache-hit rate, and generation-speed statistics.
- Real sessions use local workspace snapshots to show changed files and added/deleted line counts. A new draft captures its workspace before the first prompt; that captured baseline can bind to the new session's first turn even when follow initially replays it. Existing-session prompts also capture before sending and retain request-ordered baselines that only a later live `turn/start` for the same session can consume. Replayed starts do not consume them. Matching `turn/end` settles the result. The card follows the Codex "edited" look: a single file renders as one fully clickable "Edited <file>" card with a vertically centered "View changes" action on the right; multiple files render as an "Edited N files" container with turn totals, only file rows are clickable, and more than three files collapse to the first three with a show/collapse toggle. Clicking a file opens its diff in the existing Banned.CodeDiff.Avalonia panel. Actual Host `workspace/changes` announcements provide an optional fallback; local results take priority and do not require that producer.
- Historical `deliverables/presented` events produce per-turn delivered-file cards that follow the same Codex "edited" look as the changes card: a single file renders as one fully clickable "Edited <file>" card with a vertically centered "View changes" action; multiple files render as an "Edited N files" container with turn totals where only file rows are clickable — relative path on the left, +added/−deleted on the right — and more than three files collapse to the first three with a show/collapse toggle. Added/deleted counts come from the same-turn workspace/changes summary (local snapshot or Host announcement); files the summary does not contain simply show no counts. Repeated declarations update the latest entry for a path while keeping its first-seen position. Clicking a file opens an in-app right-side panel with the best available data: a same-session same-turn workspace/changes diff when the file is in that turn's summary; otherwise labeled "historical edit fragments" from successful `edit`/`write` calls in the loaded history (per event order; `tool/result` `meta.diffs` first, argument `old_string`/`new_string` as fallback; line numbers are fragment-relative and never fabricated file positions); otherwise the current file content with a notice that it is not the delivered snapshot; when nothing is available the panel states the reason and the card remains. "Open with default application" is an explicit button inside the panel. Session history stores the declaration plus at-edit diff hunks, not an archived copy of the file, so the full historical diff or pre-overwrite text cannot be reconstructed.
- Session permission presets and local draft preselection, with confirmation for full-access or automatic modes and backend projections confirming the active session value.
- Tool approval prompts with allow-once and reject actions.
- A to-do panel above the composer for the current turn's task list (collapsed header with a progress summary, expandable entries with per-status dots), driven by `todo/write` events; the "update to-do list" tool row summarizes a completed ratio plus add/update/remove changes against the previous list.
- A settings entry at the right of the sidebar status bar opens a native settings panel (theme preference persists to the backend settings document). The Models section lists configured providers as rounded row cards (credential dot + edit) and adds providers via "+ Add model provider": built-in catalog vendors (API key only, optional custom API URL/model catalog) or custom API routes (provider ID, display name, API URL, protocol, and model catalog required); "Fetch available models" probes the backend model-discovery RPC and merges picks into the draft. The DeepSeek first-party editor (API key, API URL, model catalog with K/M token shorthand) opens from its row. The settings-document open entry is also available in the panel header.
- Explicit Harness host startup, authentication, shutdown, and child-process cleanup.
- Compiled XAML bindings, source-generated JSON serialization, and previous Windows x64 Native AOT publishing verification. Recent changes have not all been republished with AOT.

## 🗺️ Current Limitations

- User-question prompts and attachments are not implemented. Settings cover general preferences, the Models provider management described above, and built-in plugin cards; provider deletion, agent-preset management, and per-field overrides beyond those exposed are not implemented. Drafts are retained only within the running application.
- Workspace support covers registration, selection, session grouping, renaming, and deletion; session pin, archive, fork, and rename plus workspace pinning are wired up, while unarchive entry and archived filtering are not yet implemented; there is no custom agent-preset catalog.
- Terminal and plugin-management interfaces are not implemented.
- File-change snapshots are best effort: changes written before a baseline is captured can be missed, and concurrent external edits are included. General attach/replay does not sample historical turns. Only locally captured baselines bind to their own prompt: a first-send baseline may bind to turn 1 through initial replay, while existing-session prompts bind only to later live starts. An already replayed matching turn end also settles a first send. Turns without a known session cwd/turn identity are skipped. Snapshots exclude reparse points, `.git`, `node_modules`, `bin`, and `obj`; files over 1 MiB are skipped. Unreadable files or subdirectories are excluded on both sides of the comparison. An unreadable root or a scan exceeding 4,000 files, 8,000 entries, 16 MiB, or two seconds produces no local card. Binary files have no line counts or content preview; line comparison has a bounded computation budget with a coarse fallback. The process retains at most 128 turn diffs with a 64 MiB content budget; app exit or eviction releases them.
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

Real-backend development currently expects Node.js on `PATH` and reuses the Harness home at `DSH_HOME` or `~/.dsh`. The [release workflow](.github/workflows/release.yml) builds a Windows x64 Native AOT portable ZIP with a pinned DSH runtime and bundled interpreters. Run `Run.cmd` after extracting the full package. This workflow has not yet been verified on GitHub runners; installers and automatic updates are not provided. See [release build instructions](Docs/releasing.md).

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
