# DshDesktop 开发计划

更新日期：2026-09-19。

## 状态与目标

当前只有 .NET 10 / Avalonia 12.1.0 基础模板，没有业务功能、MVVM 服务层或 Native AOT 配置。本次仅建立规划与协作规范，以下开发阶段均未实施。

目标是使用 Avalonia 原生界面复刻 DeepSeek Harness 桌面端的主要工作流程，采用 MVVM，桌面客户端支持 Native AOT。平台优先级为 Windows → macOS → Linux。

本计划是阶段讨论和验收依据，不代表自动授权执行全部阶段、Git 写入或任何远端发布操作。操作边界见 [AGENTS.md](AGENTS.md)。

## 技术路线与范围

- 保留现有 .NET 10 / Avalonia 基础，直接构建原生界面，不以 WebView2 作为首版主界面。
- 初期复用 Node Harness 后端，避免同步重写 Agent、工具执行、插件运行时和会话持久化。
- Native AOT 指桌面客户端，不代表 Node 后端也被编译为 .NET Native AOT。
- 完全移除 Node、重写 Harness 后端不在当前计划内，需要单独评估和确定范围。
- 原项目动态 Web 插件界面无法自动复用为 Avalonia UI；首版采用明确的原生功能支持列表。
- 首版以会话、流式输出、工具状态、审批、取消、工作区和必要设置形成使用闭环。
- 终端、复杂文件预览、插件管理和自动更新等分阶段加入，不承诺首版全量等价。

## 现有源码依据

- 当前客户端入口：`DshDesktop/Program.cs`、`App.axaml.cs`、`MainWindow.axaml`。
- 本机参考源码：`C:/Code/JavaScript/deepseek-harness`，仅作为分析参考，不是本项目运行时硬编码路径。
- `apps/desktop/src/host-process.ts` 使用 Node IPC 启动和管理 Host。
- `apps/desktop-host/src/index.ts` 通过 `runProfile()` 启动后端，使用 `process.send` 报告就绪、失败和关闭。
- `packages/client/connection` 包含客户端连接与 HTTP/WebSocket 相关逻辑；认证、方法和事件恢复协议已在阶段 2 梳理并落地，见 [docs/backend-integration.md](docs/backend-integration.md)。
- 参考实现版本 `0.1.6-alpha.2`；独立 Node 启动、协议往返与进程清理已在阶段 2 于 Windows 实测验证。

## 项目结构

| 项目 | 职责 |
| --- | --- |
| `DshDesktop` | 应用入口、Views、ViewModels、界面模型、服务组装 |
| `DshDesktop.Core` | 应用模型和服务接口，不依赖 UI 或具体平台 |
| `DshDesktop.Harness` | 认证、协议 DTO、请求、事件和应用模型转换 |
| `DshDesktop.Infrastructure` | 后端进程、本地设置、日志与系统能力 |
| `DshDesktop.Tests` | 与阶段实现配套的关键行为测试 |

依赖方向：Desktop 引用 Core、Harness、Infrastructure；Harness 和 Infrastructure 仅引用 Core，彼此不引用。

每个项目按需使用 `Exceptions`、`Utils`、`Services`、`Models`、`ViewModels`、`Views`，类别下再按功能划分。不创建空目录，无 GUI 项目不使用 Views/ViewModels。Desktop 的界面层统一在 `Presentation/` 下组织：`Presentation/Views/`（含 MainWindow 与按区域细分的子目录）、`Presentation/Styles/`（样式与主题资源）。Assets 等框架资源目录正常保留。

示例分类：

```text
DshDesktop/
  Presentation/
    Views/{Sidebar,Conversation,Tooling}/
    Styles/
  ViewModels/
  Services/
  Utils/

DshDesktop.Core/
  Models/{Sessions,Messages,Workspaces}/
  Services/
  Exceptions/

DshDesktop.Harness/
  Models/{Requests,Responses,Events}/
  Services/{Connection,Sessions}/
  Utils/

DshDesktop.Infrastructure/
  Models/
  Services/{Backend,Settings,Logging,Platform}/
```

以上为组织示例，具体类型和目录随真实需求创建，不预先生成全部占位内容。

## 运行与数据设计

1. Desktop 在启动位置组装服务，ViewModel 通过 Core 契约调用业务能力。
2. Infrastructure 启动后端并提供就绪信息，Harness 建立认证连接并处理请求与事件。
3. 协议 DTO 转换为应用模型，再由 ViewModel 转换为界面状态。
4. 会话与任务权威状态由 Harness 后端持有；客户端只持久化必要的桌面偏好。
5. 断线恢复通过读取最新状态和恢复订阅实现；不默认重发用户操作。
6. 流式 UI 更新合并刷新，避免每个 token 触发整段 Markdown 重排。
7. 关闭时先执行正常停止流程，再根据超时和平台能力处理进程清理。

Host 适配层候选方案：独立 Node launcher 通过 Node IPC 管理原 Host，对 C# 暴露带协议版本号的逐行 JSON 控制消息；控制输出与日志分离。该方案已于阶段 2 在 Windows 上验证可行并成为当前实现（`launcher.mjs` 控制协议 v1），详见 [docs/backend-integration.md](docs/backend-integration.md)。

## 阶段 1：工程基础与原生骨架

状态：已实现；Windows x64 Native AOT 发布与启动冒烟通过，窗口级交互尚未完成自动化复验。

任务：

- [x] 建立四个生产项目的职责和引用关系，并加入配套测试项目。
- [x] 建立 MVVM 基础、显式服务组装和 View/ViewModel 映射。
- [x] 启用编译绑定，建立 Native AOT 与裁剪检查基础。
- [x] 实现主窗口布局：工作区/会话侧栏、消息区、输入区、后端状态区域。
- [x] 使用模拟数据实现会话选择与界面状态切换，明确标识模拟模式。
- [x] 建立基础主题资源，处理窗口缩放与基本键盘交互。
- [x] 构建并启动 Windows x64 Native AOT 产物。
- [ ] 如具备 macOS 环境，尽早执行最小窗口的构建与启动验证；否则明确记录未验证。

验收：Windows AOT 程序已成功启动，ViewModel 不依赖具体后端实现；布局和基础交互已实现，但本次环境未能通过桌面自动化连接完成窗口级点击/输入复验。此阶段不接真实 Harness，不实现安装更新器。

## 阶段 2：后端接入最小闭环

状态：主体已实现。参考实现版本、协议语义与开发环境手册见 [docs/backend-integration.md](docs/backend-integration.md)；除实际窗口交互（验收待完成）外，全部链路（含带凭据的真实模型流式往返与生成中取消）对真实 Host 完成验证。

任务：

- [x] 确定并记录参考 Harness 版本及运行时来源。
- [x] 梳理认证、会话列表、发送、流式事件、取消、订阅与恢复语义。
- [x] 验证独立 Node 运行原 Host 的条件，包括 profile、模块、原生依赖与资源（junction 开发布局 + office-skills 资产 + profile 端口 0）。
- [x] 验证 launcher 控制协议，实现启动、就绪、错误与正常关闭。
- [x] 分离控制消息和日志，处理启动超时、端口冲突及异常退出（控制协议走 stdout、日志走 stderr；就绪超时、EADDRINUSE 识别；端口改由 OS 分配规避冲突）。
- [x] 实现最小强类型 DTO 与 JSON 源生成（`HarnessJsonContext`，args 键名区分 `request`/`_request`）。
- [x] 接入真实会话列表、发送消息、流式接收与取消（流式解析经协议测试；真实模型往返与生成中取消已对 GLM 验收，2026-09-20）。
- [x] 验证正常退出和故障情况下的进程清理（优雅关闭握手 + Windows Job Object 强杀回收，实测无孤儿）。
- [x] 为协议转换和关键生命周期行为添加必要测试，并验证 AOT 产物。

验收：真实链路（启动/认证/会话列表/创建/发送/事件流/取消/退出清理）已完成并经自动化测试与手动验证；带模型回复的真实流式对话与生成中取消已对 GLM 凭据完成验收（2026-09-20）。实际窗口内的输入、流式显示与取消交互尚未验收；未经验证的还有 macOS/Linux。审批 waterfall 的 C# 交互闭环在阶段 3 接入，用户问题等未支持的 waterfall 仍按协议回执拒绝。

## 阶段 3：可日常使用的会话界面

状态：进行中。已完成：助手气泡 Markdown 渲染（2026-09-20）、历史消息加载与会话切换、工具调用状态/结果/错误展示（2026-09-20）、按轮次折叠过程组（turn-process 对齐，2026-09-20）、空气泡修复与助手无气泡样式（2026-09-20）、思考（reasoning）展示与「加载更早」按钮分页（2026-09-20）、会话列表按工作区分组与「单列表/按工作区」视图下拉（2026-09-21）、新建会话指定工作区归属（2026-09-24）、工具审批 waterfall 交互闭环（2026-09-21）。未开始：Markdown 复制交互复验、用户问题交互、断线专项验证、流式节流与长会话性能、输入法与快捷键验证。

任务：

- [x] 历史消息加载与会话切换：快照窗口语义（`session/page` 向后翻页、消息对齐裁剪、默认 50 条）+ 时间线顶端「加载更早」按钮手动分页（2026-09-20 由滚动到顶自动触发改为按钮，对齐参考 Web 客户端交互；前插内容滚动锚定补偿保留）+ 切换会话重置窗口。详见 [docs/backend-integration.md](docs/backend-integration.md)。
- [x] 助手气泡接入 Markdown 渲染（LiveMarkdown.Avalonia 2.4.3）：流式增量经 `ObservableStringBuilder` 直连渲染器按块增量更新，避免每个 token 整段重排；用户消息按既定决策暂保持纯文本。
- [x] 代码块样式：覆盖库默认模板为 Codex 风格一体化深底圆角代码框（`</>` + 语言名头部、幽灵操作按钮），DarkPlus（VS）高亮配色保留，已窗口内截图验收。
- [ ] Markdown 复制交互（复制按钮、选区复制）与表格等其余富元素的窗口内复验。
- [x] 工具调用状态、结果和错误展示：`tool/call` + `tool/result` 折叠为时间线卡片（名称/参数/状态/错误原因/结果文本，可展开详情；孤儿 result 独立成卡）。嵌套 `tool/ptc-dispatch-*` 子调用暂不展示。
- [x] 工具执行折叠分组与消息样式调整（2026-09-20）：连续工具调用折叠为「N 次工具调用 · M 条消息」分组行，默认收起、运行中展开、全部落定收起，失败数计入摘要；无正文助手消息（纯工具调用轮的提交）不再产生只有表头的空气泡，计数归入分组摘要；助手回复去除气泡底色融入背景（对齐常见 Agent 客户端），用户气泡保留；翻页边界合并相邻分组与积压的无正文消息计数。
- [x] 按轮次折叠过程组（2026-09-20，对齐参考 Web 客户端 turn-process 投影，取代上一项的连续分组）：`turn/end` 收束一轮后，最终回复（2026-09-20 收紧为轮内最后一条有正文且不含工具调用块的助手消息，思考不算正文，对齐 `latestAnswer` 只看最后一个 step）保持独立气泡，其之前的中间助手消息、工具调用与思考条目折叠为「N 次工具调用 · M 条消息 · K 个 subagent」过程组（全零显示「已思考」，subagent 委派调用单独计数）；生成中的轮次条目逐项实时显示、不折叠；历史未读全（`hasMore`）时不折叠（对齐参考实现 `historyIncomplete`），翻到顶部后按全量条目整体重建并折叠；无最终回复的轮次（以工具收尾/出错/被打断且无正文）不折叠；被打断但已有正文的部分回复按最终回复展示。
- [x] 思考（reasoning）展示（2026-09-20）：assistant 消息的 reasoning 块以消息内可折叠「思考」行展示（默认收起显示首行摘要，展开显示全文），像工具调用一样随过程组折叠；纯思考消息（无正文）不产生气泡、作为轮内过程条目；中断标注改为独立「已中断」角标，不再拼入正文（修复无正文 interrupted 消息被污染成有正文的问题）。
- [x] 工具审批交互：`approval/request` waterfall 映射 `agentId=sessionId`，界面展示待决工具与理由，允许一次/拒绝通过 `$events/result` 回执；连接取消与重连代重置会清理待决项。
- [ ] 用户问题交互：`user-questions/request` 等非审批 waterfall 仍需独立的请求模型、界面和回执闭环。
- [ ] 断线后的状态同步与订阅恢复，不重复执行已发送操作。
- [ ] 流式刷新节流、长会话性能、滚动与选中文本行为。
- [ ] 验证中文输入法、快捷键和取消过程中的界面状态。
- [x] 会话列表按工作区分组与视图模式下拉（2026-09-21）：侧栏提供「单列表 / 按工作区」下拉（默认按工作区，对齐参考 Web 客户端默认视图）；工作区归属接入 `workspace/follow` 状态流（Core `IWorkspaceService` 契约 + Harness 帧解析与投影维护），组序为后端注册表顺序、组内按更新时间降序、空工作区仍显示、未记账会话（含新建空白会话）落入「未分组」；分组行可展开/收起，切换模式与选中实例保持；视图偏好持久化随阶段 4 桌面设置接入。
- [x] 新建会话指定工作区归属（2026-09-24）：每个工作区标题旁增加独立「+」入口，创建并选中新会话；顶部按钮继续创建未分组会话。Core/Harness 创建契约将 `workspaceId` 传入现有 `session/create` 请求；真实归属仍由 `workspace/follow` 状态流更新，模拟服务同步模拟该归属。`dotnet build DshDesktop.slnx`：0 警告、0 错误。未验证：窗口内点击体验及真实 Harness 对 `session/create(workspaceId)` 的端到端关联。

验收：会话、工具、审批和用户问题能够完整往返；恢复连接不导致重复发送；长会话保持可用。

## 阶段 4：工作区与设置

状态：未开始，依赖阶段 3。

任务：

- [ ] 工作区选择、最近工作区及原生目录选择器。
- [ ] 模型配置、必要权限设置和后端能力展示。
- [ ] 附件上传与基础展示。
- [ ] 桌面偏好保存、日志诊断以及与普通设置分离的凭据处理。
- [ ] 对不支持的插件界面或后端能力给出明确状态。

验收：形成可日常使用的 Windows 版本，设置在重启后正确恢复，敏感信息不进入普通日志或普通偏好文件。

## 阶段 5：macOS 完整适配与 Linux 基础适配

状态：未开始；平台最小验证可提前穿插，完整验收依赖主要使用流程稳定。

任务：

- [ ] macOS arm64 原生菜单、快捷键、窗口生命周期、数据目录与进程管理。
- [ ] macOS 凭据处理、字体、输入法和文件选择行为。
- [ ] Linux x64 原生依赖、桌面环境、字体输入和目录选择验证。
- [ ] 在各平台构建、启动 Native AOT 产物并完成真实对话链路。
- [ ] 验证各平台 Node 后端及其原生依赖的交付方式。
- [ ] 记录实际支持的系统版本、架构与 Linux 发行版范围。

验收：各目标平台有独立验证记录；不能用 Windows 验证替代其他平台验证。其他 CPU 架构根据需求另行确定。

## 阶段 6：高级能力与分发

状态：未开始，具体子项需分别确定优先级与范围。

候选事项：

- [ ] 终端、文件浏览、差异与文档预览。
- [ ] 插件管理及原生插件界面的支持策略。
- [ ] 子任务、计划、后台任务等更多 Harness 能力。
- [ ] 安装包、签名、更新检查与安全退出协调方案。
- [ ] 分发前的运行时版本、完整性和依赖清单检查。

验收：每个功能独立确定交付范围和验收标准。准备安装包或更新方案不等于授权签名、上传、发布或部署；相关外部操作单独获得明确授权。

## 关键风险与待验证事项

| 项目 | 应对与验证时点 |
| --- | --- |
| Node Host 依赖 Electron 的启动环境或打包资源 | 阶段 2 首先验证独立 Node 启动，不把原 Host 当作可直接运行的黑盒 |
| 上游协议仍可能变化 | 记录版本，隔离 DTO，对实际通信样本做契约验证 |
| Web 插件 UI 无法直接复用 | 建立原生支持清单，逐项实现 |
| Markdown、编辑器等第三方组件不兼容 AOT | 引入时验证真实 AOT 产物 |
| 高频事件、长会话造成 UI 卡顿 | 合并刷新，按真实数据量验证加载和渲染 |
| 重连导致重复发送或状态丢失 | 核对后端恢复语义，测试中断与重连 |
| 跨平台进程、输入与原生依赖差异 | 保留平台服务边界，尽早进行 macOS 最小验证 |
| 尚无对应平台环境 | 记录未验证，不宣称已支持 |

## 验证记录

阶段 1 已完成的验证记录：

- `dotnet build DshDesktop.slnx`：通过，0 个警告，0 个错误。
- `dotnet test DshDesktop.Tests/DshDesktop.Tests.csproj --no-restore`：通过，3 个测试全部通过。
- `dotnet publish DshDesktop/DshDesktop.csproj -c Release -r win-x64 --self-contained true`：通过，生成 Windows x64 Native AOT 发布目录。
- AOT 可执行文件已启动，进程保持响应，窗口标题为 `Deepseek Harness`；已在验证后关闭。
- macOS/Linux 构建与启动：未验证；真实 Node Harness 协议与后端接入：未验证。
- 窗口级自动化交互：当前桌面自动化连接未暴露 Avalonia 窗口的可操作绑定，因此未将其记录为已验证。

阶段 2 已完成的验证记录（Windows 10 x64，2026-09-20）：

- 参考仓库构建：`pnpm install --frozen-lockfile` 与 `DSH_CLIENT_COMMIT_HASH=0000000 pnpm run build` 通过（无 git 元数据需显式提供 commit 占位）。
- `dotnet build DshDesktop.slnx`：通过，0 警告，0 错误。
- `dotnet test DshDesktop.Tests/DshDesktop.Tests.csproj`：20 个测试全部通过（协议信封/帧解析、launcher 控制协议三场景、ViewModel 流式行为、模拟服务）。
- `DSH_E2E_RUNTIME_DIR=<开发 runtime> dotnet test --filter RealBackendE2eTests`：对真实 Host 通过——独立 Node 启动、就绪握手、令牌认证、`session/list`（`_request` 参数）、`session/create`、`session/prompt`、follow 快照与用户消息事件、取消调用、消息回读、优雅关闭。
- `dotnet publish DshDesktop/DshDesktop.csproj -c Release -r win-x64 --self-contained true`：Native AOT 发布通过，0 警告 0 错误，`launcher.mjs` 随包输出。
- AOT 产物以真实模式启动并成功拉起真实 Host；随后强杀应用进程，Job Object 回收 launcher 与 Host，确认无本应用遗留进程。
- 智谱 GLM 提供方接入（2026-09-20，用户授权）：DSH_HOME 默认改为与正式 dsh 共享（`~/.dsh`），新增 `session/selectModel`/`session/modelCatalog` 与 `DSH_DESKTOP_MODEL` 自动选型；`DSH_E2E_REAL_HOME=1 dotnet test --filter RealModelConversationTests` 对真实 home 通过——模型目录列出 `glm`（默认为 `deepseek-official/deepseek-flash`），选型回显 `glm/glm-5.3-flash`。
- 未验证：带 `GLM_API_KEY` 的完整模型流式往返（本机各环境作用域均未设置该变量，等用户设置后运行同一测试补齐）；macOS/Linux 全部项；窗口级自动化交互仍不可用。

阶段 2 评审修正（2026-09-20，外部 review 后复验）：

- 凭据机制修正：`apiKeyEnv` 是凭据引用名，后端按 继承环境变量 → `DSH_HOME/.credentials.yaml` → 工作目录 `.env` → `DSH_HOME/.env` 解析；删除“必须设置 `GLM_API_KEY` 环境变量”的错误说明。新增 `credentials/describe` 客户端接入（`HarnessCredentialService`，返回 `configured/source/writable`，不含密钥值）；对真实 home 实测 `GLM_API_KEY` 已配置（来源 `file`，即可直接对话，无需环境变量）。
- 流式结局传播：`SessionUpdate.StreamEnded` 携带结局与结算事件类型（`StreamOutcomeKind` + `assistant/message`/`assistant/attempt`）；被放弃的流式气泡就地标注 `[已中断]`，不再无差别“结束”。
- 取消语义核实（对照参考实现 agent.ts）：用户取消总是落盘结算（committed）——有可见内容时为带 `interrupted` 标记的 `assistant/message`，否则为 `assistant/attempt` 空结算；`abandoned` 仅用于无法落盘的错误路径。增量路径的 `MessageAppended` 与快照统一应用 `[已中断]` 标注（此前仅快照有，取消后的部分回复在 UI 不显示中断标记）。
- 会话列表刷新改为按 id 就地更新：选中实例不被替换、不重开订阅，生成中的流式气泡不被新快照清除（新增单元测试 `SessionListRefreshKeepsSelectedInstanceAndStreamingBubble`、`AbandonedStreamMarksBubbleInterruptedInsteadOfHanging` 覆盖）。
- 测试重整：拆分 `RealModelConfigurationTests`（目录/选型/凭据状态，不调用模型）与 `RealModelConversationTests`（`DSH_E2E_REAL_MODEL=1` 显式启用，启用后凭据未识别即失败；断言流式增量非空、最终回复非空、取消的落盘结算）；E2E 改用 `SkippableFact` 显式报告跳过；修复跨线程读写普通 `List` 的竞态，订阅任务异常不再被静默吞掉。取消前置为“尝试已运行”而非“文本增量已出现”——GLM 混合推理模型长文任务可能先进入只产出 `reasoning-delta` 的思考阶段（应用层不透传）。
- 窗口实测修正（2026-09-20，用户人工验收发现）：注入上下文气泡——后端以 user 角色注入的 runtime-context 快照与技能目录（`source.kind` 为 `plugin`/`skill-catalog` 等，非 `'user'`）不再显示为用户气泡，快照与增量路径统一过滤；滚动截断——`ScrollViewer` 的 `Padding` 改由内部 `Border` 承担（Avalonia 12.1.0 中 presenter 的 Padding 不计入滚动 extent，挂在 ScrollViewer 上时手动拉到底仍截掉一段，即“最后一条回信底部不可达”的根因），`HorizontalScrollBarVisibility` 显式 Disabled，消息模板不变；另补挂 `ScrollChanged` 按增高前 extent 维持贴底，覆盖流式文本增高与新气泡布局晚于滚动命令的时机问题。
- 验证（Windows 10 x64）：`dotnet build DshDesktop.slnx` 0 警告 0 错误；`DSH_E2E_RUNTIME_DIR=<开发 runtime> DSH_E2E_REAL_HOME=1 dotnet test`：22 通过、1 明确跳过（真实模型测试，待授权启用；隔离 home 的 `RealBackendE2eTests` 与真实 home 的 `RealModelConfigurationTests` 均通过）；`dotnet publish -c Release -r win-x64 --self-contained true` Native AOT 发布 0 警告 0 错误。
- 真实模型往返验收（2026-09-20，用户授权运行）：`DSH_E2E_REAL_MODEL=1` 下 `RealModelConversationTests` 通过（31s）——GLM glm-5.3-flash 流式文本增量非空、62 字符完整回复、`Committed` 结算；生成中取消后 `Committed/assistant/message` 结算并收到带 `[已中断]` 标注的部分回复（“# 蔚蓝星球”），取消后 `session/list` 仍可用。首轮运行（用户执行）曾因误设“取消 ⇒ Abandoned”断言失败，随即按协议语义修正；同轮还发现并修复 `credentials/describe` 的 refs 参数形状（数组而非请求对象）。

后端冷启动性能修正（2026-09-20，用户与 Codex 讨论定位后实施）：

- 根因：`DSH_DESKTOP_RESOLUTION` 默认 `link`——该模式每次启动都要在 profile 目录维护约 625 个 module fallback junction（`healIsolatedProfileModuleFallback`），冷启动实测约 30.9s、热启动约 15.2s；而 DSH 的 Electron 正式桌面版打包后使用 `runtime`（进程内解析，`main.ts` 中 `development ? 'link' : 'runtime'`），仅开发态用 `link`。`runtime` 模式冷启动约 8.9s。
- 修改：`NodeHostOptions.ResolutionMode` 默认值与 `DesktopBackendConfiguration` 的 `DSH_DESKTOP_RESOLUTION` 回退值均由 `link` 改为 `runtime`；环境变量仍可显式指定回 `link`。launcher 与 DSH 侧无需改动。
- 本机复测（应用真实 profile，launcher 到 ready 计时）：`link` 8.4s/9.2s，`runtime` 6.4s/7.3s，两种模式优雅关闭均 clean；顺带清理了上轮诊断遗留的测量进程树（launcher 依赖父进程关闭 stdin 才退出，独立测量脚本需主动收尾）。
- 验证（Windows 10 x64）：`dotnet build DshDesktop.slnx` 0 警告 0 错误；`DSH_E2E_RUNTIME_DIR=<开发 runtime> DSH_E2E_REAL_HOME=1 dotnet test`：23 通过、1 明确跳过（真实模型测试，随新默认 `runtime` 模式跑通含优雅关闭的全链路）。
- 未验证：应用窗口内从启动到会话列表出现的端到端耗时（Host 之外仍有认证、WebSocket、`$events`、`session/list` 串行段，如需进一步压缩属后续范围）；macOS/Linux。

阶段 3 助手气泡 Markdown 渲染验证记录（Windows 10 x64，2026-09-20）：

- 依赖核验：LiveMarkdown.Avalonia 2.4.3 依赖 Avalonia ≥ 12.0（本项目 12.1.0）、Markdig、TextMateSharp；源码检索无反射动态代码。
- `dotnet build DshDesktop.slnx`：通过，0 警告 0 错误。
- `dotnet test`：21 通过、3 按设计跳过（真实后端/真实模型 E2E 需环境变量启用）；连续 8 次运行无抖动。期间发现并修复既有用例 `StreamingUpdatesAppendAssistantTextAndCommitReplacesBubble` 的竞态（等待条件过早命中流式中间态，约 1/4 概率失败），改为等待正式消息替换流式气泡的终态。
- `dotnet publish -c Release -r win-x64 --self-contained true`：Native AOT 发布通过，强制完整 ILC 重跑，0 警告 0 错误。
- AOT 产物启动冒烟：模拟模式进程稳定后正常退出；随后以真实模式（`.backend-runtime` + 共享 `~/.dsh`，只读加载）启动，窗口截图确认助手气泡 Markdown 生效——真实 GLM 历史回复中 `**GLM**`、`**Z.ai**` 等加粗正确渲染（无裸露星号）、emoji 正常、无空气泡；强杀应用后无遗留 launcher/Host 进程（Job Object 回收）。
- 代码块样式重构（2026-09-20，用户对照 Codex 截图提出）：覆盖库默认 `CodeBlock` 模板为一体化深底（#1E1E1E）圆角（8px）无外描边的代码框，头部为 `</>` 图标 + 语言名 + 幽灵按钮（换行开关/复制），语法高亮仍为 DarkPlus（VS 暗色配色，用户明确保留）。关键点：Fluent 的按钮状态底色画在按钮模板内 `ContentPresenter#PART_ContentPresenter` 上，覆盖按钮自身 Background 无效，需以 `…/template/ ContentPresenter#PART_ContentPresenter` 选择器透两层模板覆盖；换行开关默认选中（`IsCodeWrapped` 默认 true），已压掉强调色底。真实模式窗口截图确认新样式生效、整体布局无损；`dotnet build`/`dotnet test`/Native AOT 发布均通过。
- 未验证：代码块高亮、标题/表格等富元素的窗口内实际显示（可见会话未包含这些元素，且窗口级自动化不可用）；流式增量渲染的窗口内实时观感（VM 层增量同步已由单测覆盖，窗口内待用户发送消息时人工确认）。

阶段 3 历史翻页与工具调用展示验证记录（Windows 10 x64，2026-09-20）：

- 协议梳理（对照参考实现 `packages/api/session-controller/src/history.ts`、`ui-chat/conversation-nodes/tool.ts`、`core/session/src/types.ts`）：快照按消息对齐裁剪（默认最近 50 条消息，工具事件经 `sourceEventSeqs` 与来源消息同组），`hasMore` 表示更早历史；`session/page`（形参名 `request`）以 `throughSeq`（快照游标）+ `beforeSeq`（当前窗口首条 seq，排除性上界）向后翻页，返回同形 records + hasMore；`tool/call` 携带 `callId/name/arguments`（原始 JSON 文本），`tool/result` 的 `message.content[0]` 为 tool-result 块（内层 content 文本块 + `isError`），`data.error` 为错误身份（`name/code/reason`）；孤儿 `tool/call`（崩溃中断）在会话恢复时由后端 repair 合成 error result。参考 Web 客户端翻页页大小同为 50。
- 实现：Core 模型引入 `ConversationEntry` 基类（`ConversationMessage` 继承）与 `ToolActivity`（状态机 Running/Succeeded/Failed）；`ISessionService.LoadOlderAsync` 契约；Harness 层 `session/page` DTO + `MapEntries` 折叠映射（快照与增量同源）+ 快照携带 `WindowStartSeq/HasMore`；ViewModel 维护历史窗口状态、`LoadOlderCommand`（整页被注入上下文过滤时最多连翻 4 页）；工具卡片模板（多类型 DataTemplates 按类型匹配）；窗口 code-behind 滚动到顶自动触发 + 前插内容滚动锚定补偿 + 会话切换重置（Reset）导致的偏移归零不误触发（该问题由窗口截图发现并修复）。
- `dotnet build DshDesktop.slnx`：通过，0 警告 0 错误。
- `dotnet test`：29 通过、3 明确跳过（真实后端/模型 E2E），连续 3 次运行无抖动。新增用例：page 请求线形与响应解析折叠、失败工具错误映射、孤儿 result 独立成卡、翻页前插与耗尽、切换会话重置窗口、工具卡片落定与失败态。
- `DSH_E2E_RUNTIME_DIR=<开发 runtime> dotnet test`：对真实 Host 通过（含新增 `session/page` 真实往返：新会话空页 + `hasMore=false`）。
- `dotnet publish -c Release -r win-x64 --self-contained true`：清理后完整 ILC 重跑，0 警告 0 错误。
- AOT 产物窗口截图验收（PrintWindow 定向截取本应用窗口）：模拟模式首屏（演示会话「长会话翻页」）确认工具卡片渲染（fs.read × 1，状态点与「失败」状态文字、时间、「详情」按钮）与「↑ 向上滚动加载更早消息」提示，无渲染异常；误触发修复后复核为初始窗口（不自动翻页）。真实模式（`.backend-runtime` + 共享 `~/.dsh`，只读加载）确认真实会话列表与 GLM 历史对话正常渲染、「Harness 后端已连接」；强杀应用后 launcher/Host 无遗留（Job Object 回收）。
- 未验证：真实会话内工具卡片的实际渲染——本机 27 个真实会话经解压扫描均无 `tool/call` 事件，需模型实际触发工具（需授权的真实模型运行）或出现含工具调用的会话后补验（2026-09-20 更新：用户以含工具执行的真实 dsh 会话复核窗口，确证卡片渲染正常，暴露的空气泡问题已在下方记录中修复）；滚动到顶自动翻页与详情展开的窗口内人工交互（截图仅覆盖静态渲染）；嵌套 `tool/ptc-dispatch-*` 子调用未实现展示；macOS/Linux。

阶段 3 工具分组折叠与消息样式验证记录（Windows 10 x64，2026-09-20）：

- 背景：真实 dsh 会话复核暴露两类问题——纯工具调用轮的 `assistant/message`（`ExtractText` 仅拼 text 块得到空串）渲染为只有「DeepSeek + 时间」表头的空气泡；工具卡片逐张平铺噪音大。对齐 Web 端「4 次工具调用 · 1 条消息 ›」的折叠交互，并按用户决策：运行中展开、落定收起；助手回复不用气泡底色。
- 实现：新增 `ToolGroupItemViewModel`（工具数/隐藏消息数/失败数摘要、展开态、翻页边界 `Absorb` 合并）；`MainWindowViewModel.TimelineAssembly` 统一组装快照/增量/翻页三条路径（可见消息结束分组；无正文助手消息只计数不显示；页边界相邻分组合并、页尾积压计数并入紧邻分组）；工具卡片模板迁入 `Window.Resources` 供分组内 `ItemsControl` 复用；`assistant-bubble` 背景改透明并删除 `AssistantBubbleBrush`；模拟演示会话改为「用户消息 → 空助手提交 → fs.read + fs.edit → 总结」形态，新增 `BeginToolActivity`/`SettleToolActivity`/`PushCommittedAssistantMessage` 测试辅助。
- `dotnet build DshDesktop.slnx`：通过，0 警告 0 错误。
- `dotnet test`：31 通过、3 明确跳过（真实后端/模型 E2E），连续 3 次运行无抖动。新增用例：无正文助手消息并入分组摘要且不产生气泡、运行中展开/部分落定保持展开/全部落定收起、用户消息后另起分组、翻页边界分组合并与积压计数并入（翻到底 12 项的 seq 序列断言）。
- `dotnet publish -c Release -r win-x64 --self-contained true`：通过，0 警告 0 错误。
- 窗口截图验收（PrintWindow + 模拟点击，模拟模式「长会话翻页」）：收起态确认「2 次工具调用 · 1 条消息 · 1 失败 ›」单行摘要、无空气泡、助手消息无气泡底色、用户气泡与「↑ 向上滚动加载更早消息」提示正常；点击摘要行展开后确认两枚工具卡片（fs.read 失败红点、fs.edit 成功绿点、状态/时间/详情按钮、箭头转向下）。
- 未验证：真实会话内分组折叠的窗口内人工交互（截图为模拟模式演示会话）；展开/收起与「详情」二级展开的连击体验；macOS/Linux。

阶段 3 按轮次折叠过程组验证记录（Windows 10 x64，2026-09-20）：

- 背景与规则来源：桌面端此前把「先重读当前文件，再做定点替换。」这类有正文的中间助手消息显示为独立气泡（并与官方网页端截图对比确认）。规则取自参考实现 `packages/client/ui-chat/src/client/conversation-nodes/turn-process.ts`（`latestAnswer`/`processSpec`）、`ChatNodeSeat.tsx`（`processWindowReady` 含 `historyIncomplete`）与 `ui-chat/src/client/locale.ts`（摘要文案）。协议侧确认：`assistant/message`、`tool/call`、`tool/result` 的 `data.turn/step` 与持久化事件 `turn/end`（`core/session/src/types.ts`）即"哪些是过程、哪些是最终回复"的判定信号；assistant 内容块 `tool-call` 类型字符串经 `event-projection.ts` 核实。
- 实现：`WireEventJson` 新增 `TryGetTurnStep`/`TryGetTurnEnd`/`HasToolCallBlocks`；Core 模型 `ConversationMessage`/`ToolActivity` 增加 `Turn` 与 `HasToolCalls`，新增 `TurnBoundary` 条目与 `SessionUpdate.TurnEnded`；`MapEntries`/`FollowSessionAsync` 透传轮次并产出边界；`TimelineAssembly` 重写为按 turn 组装（轮内逐项渲染，`turn/end` 后结算最终回复并折叠为 `TurnProcessGroupViewModel` 过程组；历史未读全不折叠，翻页改为全量条目重建、删除页边界合并逻辑）；模拟演示数据对齐真实形态（turn 标注 + turn/end，每轮「用户 → 中间说明 → fs.read + fs.edit → 总结 → turn/end」）；`ToolGroupItemViewModel` 删除。
- `dotnet build DshDesktop.slnx`：通过，0 警告 0 错误。
- `dotnet test`：34 通过、3 明确跳过（真实后端/模型 E2E 需环境变量启用）。新增用例：turn/end 映射与轮次透传、tool-call 块识别、turn/end 后折叠过程组并保留最终回复、无最终回复的轮次不折叠、生成中逐项显示与就地落定、翻页到底后整体折叠（12 项 seq 序列断言）。期间修复两处实现缺陷：折叠后重放顺序（过程组应在最终回复之前）与 `Enumerable.Append` 误用（应为 `Add`，导致折叠后条目丢失）。
- 模拟模式启动冒烟：`dotnet run --no-build` 启动 12s 无异常输出。
- 未验证：真实会话内 turn 折叠的窗口内人工交互（模拟模式已覆盖组装逻辑，真实会话待用户复核）；Native AOT 产物未随本轮重跑（无新增反射依赖，上次发布记录仍有效）；macOS/Linux。

阶段 3 思考展示、按钮分页与打断轮次对齐验证记录（Windows 10 x64，2026-09-20）：

- 背景（用户对照 Web 端提出三点）：滚动到顶自动加载不丝滑且偶发漏触发，应改为 Web 端的「加载更早」手动按钮；打断轮次（无可展示正文）不应折叠而其后正常轮次应折叠；工具调用之间的思考（reasoning）被桌面端丢弃（`ExtractText` 仅拼 text 块）。规则核对自参考实现：`ReasoningRow.tsx`（思考行交互）、`assistant-content.ts` 的 `hasAssistantReplyContent`（reasoning 不算回复正文）、`turn-process.ts` 的 `latestAnswer`（只取最后一个 step 的消息，无 reply 内容或含 tool-call 则整轮不折叠）、`ChatView.tsx`（`chat.loadOlder` 按钮分页）。
- 实现：Core `ConversationMessage` 增加 `Step`/`Reasoning`/`IsInterrupted`；`WireEventJson.ExtractReasoning`（reasoning 块拼接）；`ToConversationMessage` 统一映射（快照/增量/`MapMessages` 同源），中断不再拼入正文；`TimelineAssembly`：纯思考消息作为轮内过程条目（随组折叠、展开显示思考行），`FindAnswer` 收紧为轮内最后一条助手消息判定；`TurnProcessGroupViewModel.MessageCount` 只计有正文条目；`MessageItemViewModel` 增加思考行（`ReasoningSummary` 首行摘要 + 展开全文）与独立 `IsInterrupted` 角标；`MainWindow.axaml` 助手气泡渲染思考行与角标，时间线顶端改为「加载更早」按钮（`LoadOlderText` 加载中切换文案），`MainWindow.axaml.cs` 删除滚动到顶自动触发（保留前插锚定与贴底跟随）；模拟数据每轮补两段思考与带思考的总结，`PushCommittedAssistantMessage` 支持 reasoning/isInterrupted。
- `dotnet build DshDesktop.slnx`：通过，0 警告 0 错误。
- `dotnet test`：35 通过、3 明确跳过（真实后端/模型 E2E 需环境变量启用）。更新用例：翻页到底折叠（12 项 seq 序列、组内两段思考、总结带思考行）、流式中断标注改断言 `IsInterrupted`、轮次折叠升级为含思考真实形态；新增用例：打断轮次（reasoning-only + interrupted + turn/end）整轮不折叠且其后正常轮次照常折叠；协议测试补 `ExtractReasoning` 断言。
- 窗口截图验收（PrintWindow + 模拟点击，模拟模式「长会话翻页」）：首屏确认「加载更早」按钮、两条思考行（摘要 + 箭头）与工具卡片渲染正常；连续点击按钮翻到顶后按钮消失、4 轮各折叠为「用户气泡 + 『2 次工具调用 · 1 条消息 ›』过程组 + 带思考行的回答」；点击摘要行展开后组内按时间线显示思考 → 中间说明 → fs.read → 思考 → fs.edit。
- 未验证：真实会话（含 reasoning 的 GLM 历史）的窗口内人工复核；流式生成中思考的实时展示（当前流式仅 text 增量，思考随提交消息事件到达后出现）；Native AOT 产物未随本轮重跑（无新增反射依赖，上次发布记录仍有效）；macOS/Linux。

阶段 3 会话列表工作区分组验证记录（Windows 10 x64，2026-09-21）：

- 背景（用户提出）：会话列表此前仅按时间单列表排列，参考 Web 客户端提供「按工作区 / 按工作区树 / 单列表」视图下拉。规则核对自参考实现 `ui-workspace` 包：`tree.ts` 的 `groupByWorkspace`（工作区记账归属、未分组兜底、空组显示）、`WorkspaceBrowser.tsx` 的视图菜单与 `stores.ts` 默认 `groupBy: 'workspace'`、`orderBy: 'updated'`；线协议为 `workspace/follow` 状态流（无对应一元 list 方法），帧形对照 `workspace-controller/src/types.ts`（baseline/upsert/remove/order/archived，`WorkspaceView` 携带 `sessionIds` 记账）。「按工作区树」嵌套与手动排序（insertSessionBefore）未纳入本轮。
- 实现：Core 新增 `WorkspaceSummary` 与 `IWorkspaceService`（投影消费契约，首次调用可能为空、事件驱动更新）；Harness 新增 `WorkspaceFollowFrame` 手动解析与 `HarnessWorkspaceService`（baseline 整体替换、upsert 新行插头部且旧投影不覆盖新、order 按给出的顺序重排，Baseline 无条件通知以区分「基线未到达」与「确无工作区」；业务终态如后端无该命名空间时保持投影不再重试）；`HarnessConnection` 流泵泛化为 session/workspace 复用同一重连语义；ViewModel `SessionRows` 行投影（组头行 + 会话行混排，默认按工作区、组内 updatedAt 降序、空组显示、未分组仅非空时出现、收起状态按组键保留、`IsCurrent` 驱动行高亮）；侧栏 ComboBox 切换 + 自绘 Button 行模板（hover/选中高亮、分组行不可选）；模拟模式登记两个演示工作区（含一个空组）。
- 外部诊断修复（2026-09-21，用户以 Codex 交叉诊断后授权修复）：① 自定义 Button 模板的 `ContentPresenter` 未绑定 Content/ContentTemplate，行内文字整体不显示（截图只剩固定尺寸元素）——显式补 `TemplateBinding` 后修复；② `RefreshSessionsAsync` 在「选中会话仍存在」分支提前 return，跳过行投影重建，后台新增/移除会话不刷新列表——改为该分支不 return、统一走末尾重建，并补回归测试。
- `dotnet build DshDesktop.slnx`：通过，0 警告 0 错误。
- `dotnet test`：44 通过、3 按设计跳过（真实后端/模型 E2E 需环境变量启用）。新增用例：workspace/follow 帧解析（baseline 字段、upsert/remove/order、archived 帧解析为 null）、投影状态机（原位替换、旧不盖新、新行插头、order 重排未知排尾）、默认分组视图与高亮、模式切换投影形状（组序/组内排序/空组/未分组）、分组收起与跨模式保持、新建会话入未分组且选中保持、工作区事件驱动重组、后台新增会话时选中不变仍重建行投影（回归）。
- `DSH_E2E_RUNTIME_DIR=<开发 runtime> dotnet test --filter RealBackendE2eTests`：对真实 Host 通过（含新增 `workspace/follow` 真实往返：订阅建立、基线到达、解析成功）。
- `dotnet publish -c Release -r win-x64 --self-contained true`：Native AOT 发布通过，0 警告 0 错误。
- 窗口截图验收（PrintWindow 定向截取）：修复前暴露行内容不渲染问题（见上）；修复后模拟模式确认「示例工作区（2 个会话，选中高亮）/ 文档整理（暂无会话）/ 未分组（2 个会话）」分组渲染、下拉框「按工作区」；真实模式（`.backend-runtime` + 共享 `~/.dsh`）确认真实工作区分组（DshDesktop 等多个组）与真实会话归属、选中高亮、「Harness 后端已连接」；应用退出后无本应用遗留 launcher/Host 进程（Job Object 回收；系统中其他 node 进程经命令行核验均属用户自开的正式 dsh web 版与 Codex 环境）。
- 未验证：视图下拉切换与分组展开/收起的窗口内人工点击交互（截图仅覆盖静态渲染，投影与状态逻辑由单测覆盖）；「按工作区树」模式与手动排序；视图偏好持久化（阶段 4）；macOS/Linux。

悬浮输入面板改造验证记录（Windows 10 x64，2026-09-21）：

- 背景（用户对照 WebUI 提出，与 Codex 讨论方案后按第一阶段执行）：底部输入区原为独立 `Grid.Row`，与聊天区割裂；方案确认附件/权限/审批/统计等协议未接入的能力只做视觉占位，不混入纯布局改造。
- 实现：`MainWindow.axaml` 右侧改两行布局，输入面板与消息滚动区同格叠放（`ZIndex` + 底部对齐）悬浮于消息之上；新增圆角面板、左侧工具栏（附件禁用占位）、右侧模型区域（当时为「模型 —」占位）、圆形发送/停止按钮（生成中切换，命令仍为原 `SendMessageCommand`/`CancelCommand`）与底部统计栏（一律 `—`）；`MainWindow.axaml.cs` 随面板实际高度同步滚动内容底部留白，滚到底时最后一条消息不被面板遮挡；`App.axaml` 深/浅主题各新增 `ComposerSendHoverBrush`（发送悬停），窗口内不写死颜色。
- `dotnet build DshDesktop.slnx`：通过，0 警告 0 错误；`dotnet test`：44 通过、3 按设计跳过。
- 窗口截图验收（PrintWindow 定向截取 + SendKeys 交互）：悬浮圆角面板四周透出聊天背景；多行输入后面板增高、无遮挡错位；Enter 发送后草稿清空、用户气泡完整可见；左 + 按钮、模型占位、统计栏渲染正常。
- 未验证：生成中「停止」按钮形态——模拟后端不置 Running（声明式绑定，编译期验证）；真实后端下的实际观感。

模型选择接入验证记录（Windows 10 x64，2026-09-21）：

- 背景：模型协议基础（`session/modelCatalog`、`session/selectModel`、`DSH_DESKTOP_MODEL` 自动选型）已存在但未接入 UI（Codex 交叉分析确认需补 Core 接口、ViewModel 状态与服务调用）。规则核对自参考实现：`session-controller/src/catalog.ts`（目录 `groups/failures` 形态，空目录组剔除）、`model-selection-projection.ts`（`modelSelection` 投影 `{lastUsed, next}`，`next = pending ?? lastUsed` 即下一次请求的选型；选型经 `model/selection` 持久化事件回声）。
- 实现：Core 新增 `ModelSelection`/`ModelCatalog`（`Groups`/`Failures`，reasoning 档位暂无消费方不解释）、`ISessionService.GetModelCatalogAsync`/`SelectModelAsync` 契约、`SessionUpdate.Snapshot.CurrentModel` 与新 `ModelSelected` 更新；Harness 目录 wire 扩展 `groups/failures` 并映射为应用模型（空模型组剔除），follow 快照解析 `modelSelection` 投影（next 优先、回退 lastUsed、未选型为 null），`model/selection` 事件解析为 `ModelSelected`；模拟服务提供双提供方目录、按会话记账选型并经同一回声路径生效；ViewModel 目录于初始化与后端重连时加载（失败不阻塞会话列表），`CurrentModel` 以 follow 流为权威，`SelectedModelOption` 改动即发起选型（与生效值相同/在途/失败时回退显示），当前选型不在目录时补占位项，无会话或断连时下拉禁用；XAML 悬浮面板右列以 ComboBox 替换占位徽标。
- 期间修复：`SelectedSession` setter 原为先启动订阅再清空选型，模拟实现的快照可能内联到达后被清空——改为先清空再订阅（单测稳定复现并回归）。
- `dotnet build DshDesktop.slnx`：通过，0 警告 0 错误。
- `dotnet test`：52 通过、3 按设计跳过（真实后端/模型 E2E 需环境变量启用）。新增用例：目录映射（空组剔除、失败项、默认选型）、快照投影（next 优先/回退 lastUsed/未选型 null）、`model/selection` 事件解析、VM 目录填充与快照选型、下拉选型回声与跨会话各自跟随、选型失败回退并报错、模拟服务选型回声与快照携带、缺失会话选型抛错；`RealModelConfigurationTests`/`RealModelConversationTests` 改走服务公开 API（目录断言改为 groups 含目标提供方）。
- 窗口验收（UI Automation 驱动模拟模式）：下拉展开列出全部选项（Sim Chat/Sim Reasoner · Simulated、Alt Chat · Simulated Alt），选择后回显「Alt Chat · Simulated Alt」；切换到「Native AOT 验证」会话后下拉自动变为该会话的「Sim Reasoner · Simulated」。
- 未验证：真实 Host 下的目录/选型窗口内交互（`RealModelConfigurationTests` 需 `DSH_E2E_*` 环境变量运行，真实目录此前已验收列出 `glm`）；reasoning effort 档位选择（目录含该字段但暂无 UI 消费方）；Native AOT 产物未随本轮重跑（无新增反射依赖）；macOS/Linux。

usage/token/缓存统计接入验证记录（Windows 10 x64，2026-09-21）：

- 背景（用户按既定方案顺序提出）：token 用量、缓存命中率与生成速度此前为「—」占位，流式解析把 usage 等帧归为未解释类型。规则核对自参考实现：`llm/src/types.ts` 的 `TokenUsage`（四桶互斥：inputTokens 仅未命中缓存部分，缓存读/写单独计）、`token-meter/src/usage-projection.ts`（`tokenUsage` 投影，whole-log 累计，view 即四桶）、`session/session-stats/src/projection.ts`（`sessionStats` 投影：turns/steps/llmMs/toolMs/ttft/decodeMs/decodeTokens）、`ui-chat/src/client/chat/StatsPills.tsx` 与 `token-format.ts`（展示口径：总量=计费输入+输出；缓存命中率=缓存读/计费输入，取整、接近 100 时保留一位小数；速度=解码 token/解码时长；紧凑计数 12.2K/1.2M）。传输路径：follow 快照 `projections.values`（冷会话也携带全部投影 wire 视图）+ `session/control` Host 级状态流（每代 Baseline 后按会话 projection 整值更新）。
- 实现：Core 新增 `SessionUsage`/`SessionStats` 与 `SessionUpdate.UsageUpdated`/`StatsUpdated`（整值替换 + 投影 seq 供乱序 gating）；Harness 新增 `SessionControlFrame` 解析（baseline/projection 帧，jobs 帧无消费方解析为 null）与 `HarnessConnection.FollowSessionControlAsync`（复用通用流泵，重连自动重开）；快照解析 `tokenUsage`/`sessionStats` 投影并随 `ProjectionAsOfSeq` 下发；`FollowSessionAsync` 重构为 follow 流与 control 流双泵合并（control 失败静默，不拖垮会话订阅）；模拟服务按会话记账（预置会话从助手条目推演初始计量，推送消息按一步计费累计）并经同一快照/增量路径下发；ViewModel 维护 usage/stats（seq gating、会话切换重置），`TokenFormat`（Utils）承载展示格式化；XAML 统计栏占位替换为绑定值，悬停显示四桶与轮次/步数/耗时明细。
- `dotnet build DshDesktop.slnx`：通过，0 警告 0 错误。
- `dotnet test`：56 通过、3 按设计跳过。新增用例：快照 usage/stats 投影解析（含投影缺失容错）、control baseline/projection 帧解析（未知键与 jobs 帧忽略）、模拟服务快照携带计量与回复累计（含重订阅持久）、VM 统计随会话更新/切换重置/文案带真实数值。
- 真实 Host 验证（`DSH_E2E_RUNTIME_DIR=<开发 runtime> dotnet test --filter RealBackendE2ETests`，2026-09-21）：通过——快照 `tokenUsage` 投影对真实 Host 到达并解析（新会话全零，符合空会话预期）；`session/control` 流 Baseline 到达且含被跟随会话的投影（基线投影会话数 1），端点名与帧形态实证。
- 窗口验收（模拟模式截图，长会话选中）：统计栏显示「Token 用量 1.7K · 缓存命中 84% · 生成速度 21.2 tok/s」，占位「—」已由真实绑定值替换。
- 未验证：真实计费数据下的窗口内实时刷新（真实模型往返需 `DSH_E2E_REAL_MODEL=1` 与凭据；投影链路本身已对真实 Host 实证）；统计悬停明细的窗口内交互；Native AOT 产物未随本轮重跑（无新增反射依赖）；macOS/Linux。

统计条空会话隐藏验证记录（Windows 10 x64，2026-09-21）：

- 背景（用户提出）：新对话尚无往返数据时，底部「Token 用量 / 缓存命中 / 生成速度」三项以「—」占位常驻；参考 WebUI 空会话不渲染统计条。核对 `ui-chat` 编译产物 `StatsPills` 的口径：`stats.steps === 0 && !hasTokens`（hasTokens = 计费输入或输出 > 0）时组件整体返回 null。
- 实现：ViewModel 新增值判断 `HasStatsData`（Stats 有步数或 Usage 四桶合计 > 0）并在 usage/stats setter 通知；XAML 统计条绑定 `IsVisible`，快捷键提示保留；删除无绑定且语义误导的死属性 `HasUsageValues`（非 null 判断会被冷会话全 0 投影骗到，判断必须基于数值——冷会话 follow 快照携带全 0 投影 wire 视图）。
- `dotnet test`：62 通过、3 按设计跳过，连续 3 次无抖动。新增用例 `StatsStripStaysHiddenForBlankSessionUntilUsageArrives`：默认长会话可见 → 新建空会话（全 0 基线已回填）保持隐藏 → 首条回复落地后出现 → 再切到空会话重新隐藏。测试环境注记：沙箱会话需 `/m:1 /nodeReuse:false`（多节点 MSBuild 命名管道被禁）；Avalonia telemetry 写用户目录被拒时以完整文件访问放行（等价本地替代：`-p:UsedAvaloniaProducts=`，见上文 publish 记录）。
- 未验证：窗口内实际视觉验收（本轮为 ViewModel 层验证 + XAML 单绑定，编译期绑定已校验）；Native AOT 产物未随本轮重跑（无新增反射依赖）；macOS/Linux。

轮次折叠逐轮判定 + follow 竞态修复验证记录（Windows 10 x64，2026-09-21）：

- 背景（用户对照 WebUI 提出）：同一会话「清理完成」轮在 WebUI 折叠为「3 次工具调用 · 3 条消息」，桌面端逐项展开。实证根因（对真实 home 只读 follow 本会话 + zstd 解包会话日志核对）：真实 follow 快照只发尾部窗口（约 100+ 条，`HasMore=true`），旧规则「历史读全才折叠」导致中途 attach 长会话全程不折叠；「加载更早」按钮在已加载窗口顶端、远离视口，易被误判为历史已读全。WebUI 折叠是因为其会话从空实时长出、已加载窗口天然全量。
- 附带发现并修复两个真实问题：①真实 Host 会在快照尾部为开放轮合成 `reason=interrupted` 的 turn/end（seq 即 cursor，持久日志不存在，对应 harness `interruptedTurnClosers` 语义）——若不识别，中途 attach/重连会把生成中的轮提前折叠且真实边界到达后二次折叠；②follow 循环的会话守卫是检查-后-动作两段式，被抢占的旧会话循环可把迟到 stats/usage 整值写进新会话基线之后（全量套件偶发复现，现场转储定位：空会话出现 Steps=1 的串台统计）。
- 实现（用户确认选「窗口内完整轮次即折叠」）：`TimelineAssembly` 移除全局 `AllowFolding`，改为逐轮判定——本轮起点（用户消息或上一轮边界）落在已加载窗口内才折叠，被截断的首轮保持逐项；`TurnBoundary`/`SessionUpdate.TurnEnded` 增加 `Reason`，快照应用时丢弃尾部 interrupted 合成边界；`BeginFollow` 引入代际门闩（`_followGate` 锁 + `_followEpoch`），follow 循环的「校验 + 应用」在锁内原子进行。
- `dotnet test`：63 通过、3 按设计跳过；全量套件连跑 6 次无抖动（修复前约 1/3 概率复现串台）。新增用例 `SnapshotTailInterruptedBoundaryKeepsOpenTurnUnfoldedUntilRealEnd`（合成边界不提前结算、真实 turn/end 到达后单次折叠）；`LoadingOlderPrependsEntriesAndFoldsCompleteTurnsAtTheTop` 更新为首次翻页后即折叠完整轮次（`HasMore=true` 时）；统计条用例的零值基线等待收紧为按值判定并放宽到 5s。真实 Host 侧证据经临时诊断测试（boot 真实 runtime + follow 本会话）采集，用后已删除。
- 未验证：窗口内视觉验收（含中途 attach 长会话的折叠形态、生成中轮不被提前折叠）；真实 Host 下重连场景的合成边界行为（仅以快照形态实证 + 模拟桩覆盖）；Native AOT 未随本轮重跑（无新增反射依赖）；macOS/Linux。

阶段 3 工具审批 waterfall 验证记录（Windows 10 x64，2026-09-21）：

- 协议核对：参考 Gateway 的 `RemoteEventInvocationFrame` 与 `parseRemoteEventResult`，确认 waterfall 帧为 `event/eventId/agentId/request`；`$events/result` 的 outcome 为严格的 `next`、`result` 或 `rejected` 三形态。审批请求载荷为 `toolName`、可选 `callId`/`reason`，`agentId` 即会话 id；工具审批结果使用 `result + allowed-once/rejected`，取消通过 `cancel` 帧处理。
- 实现：Core 新增 `IToolApprovalService`/`PendingApproval`；Harness 完整解析 waterfall 字段，审批服务维护跨会话待决列表，按 `agentId` 映射会话，处理 cancel/连接代重置，并经 `$events/result` 回执；回执失败保留待决项以便重试；未知 waterfall 保持可解析并继续走 rejected 回执。Desktop 与模拟服务完成组装，悬浮输入面板展示当前会话审批横幅及「允许一次/拒绝」操作。
- `dotnet build DshDesktop.slnx`：通过，0 警告 0 错误。
- `dotnet test DshDesktop.Tests/DshDesktop.Tests.csproj --no-restore`：60 通过、3 按设计跳过；新增 waterfall 帧/审批载荷、三种 outcome 严格 JSON 形态、未知 request 保持可拒绝的协议测试，以及当前会话审批过滤/允许一次命令回归。
- `dotnet publish DshDesktop/DshDesktop.csproj -c Release -r win-x64 --self-contained true --no-restore -p:UsedAvaloniaProducts=`：通过，Windows x64 Native AOT 产物生成成功。普通 publish 因当前环境无法读取用户级 NuGet/Avalonia telemetry 目录，使用跳过 Avalonia telemetry 统计的等价本地验证参数完成。
- 未验证：真实 Host 触发实际工具审批并在窗口点击后的端到端往返；用户问题 waterfall；macOS/Linux。

UI 层目录重构验证记录（Windows 10 x64，2026-09-22）：

- 背景（用户提出）：`MainWindow.axaml`（899 行）与 `App.axaml`（257 行）承载全部界面，样式、模板与视图混杂。按「UI 拆分、业务不动」范围重构为 Presentation 目录结构。
- 实现：`App.axaml` 精简为 FluentTheme + LiveMarkdown 依赖样式 + `Presentation/Styles` 引入（ThemeResources/Typography/ScrollBars/Markdown/Sidebar/Conversation/Tooling/Composer 共 8 个样式文件，语义资源与选择器原样搬迁，加载顺序保持「FluentTheme → LiveMarkdown → Markdown 覆盖」）；视图拆为 `Presentation/Views`（MainWindow 精简为窗口级组合 + 侧栏列宽 clamp/抽屉折叠协调，SidebarView、ConversationView（含 SessionHeaderView/ComposerView）、Conversation/MessageView、Tooling/ToolActivityView、ApprovalPromptView）；`ToolCardTemplate`/`MessageBubbleTemplate` 大模板改为 DataTemplate 实例化对应 View。界面行为随视图迁移：消息贴底/前插锚定 → `ConversationView.axaml.cs`（改经 `OnDataContextChanged` 订阅），Enter 发送与输入框焦点类切换 → `ComposerView.axaml.cs`，折叠开关视觉 → `SessionHeaderView`（经事件上报 MainWindow 协调列宽）。ViewModel 仅一处微调：`PendingApprovalViewModel` 构造注入允许/拒绝命令（编译绑定无法跨文件回查窗口 DataContext，使 ApprovalPromptView 自包含）；`MainWindowViewModel` 业务流未动。发现并原样保留的遗留：`_anchoringPrepend` 无置真路径（历史滚动锚定方案残枝），后续如启用自动翻页需接回。
- `dotnet build DshDesktop.slnx`：通过，0 警告 0 错误（compiled bindings 全部通过，含 `$parent[SidebarView].DataContext` 行级命令绑定）。
- `dotnet test`：64 通过、3 按设计跳过（真实后端/模型 E2E），0 失败。
- `dotnet publish DshDesktop/DshDesktop.csproj -c Release -r win-x64 --self-contained true`：清理 `obj/Release` 后完整 ILC 重跑，exit 0、0 警告 0 错误，产物 36.9 MB。
- AOT 产物启动冒烟（模拟模式）：进程 8 秒存活后正常终止——全部 ResourceInclude/StyleInclude URI、DataTemplate 与布局在运行时解析无异常。
- 未验证：窗口内交互复验（侧栏拖拽/折叠、Enter 发送、工具卡展开、审批按钮点击、流式贴底）——本环境无窗口级自动化，逻辑为原样搬迁且 VM 层测试通过，待用户日常使用确认；深色主题像素级对照；macOS/Linux。

前插历史滚动锚定修复验证记录（Windows 10 x64，2026-09-22）：

- 背景（用户复查重构提出）：`_anchoringPrepend` 无任何置真路径。git 考古确认仓库初始快照（2026-09-21）即缺失 setter，plan.md 记载 2026-09-20 曾实现并截图验证过锚定补偿，进仓库前丢失。
- 行为判定：真实存在用户可见 bug——点击「加载更早」后视口跳到已加载历史顶端。两条依据：① Avalonia 12.1 `ScrollContentPresenter` 的锚定补偿（`ArrangeWithAnchoring`）只对经 `RegisterAnchorCandidate` 注册的锚点元素生效（通常由虚拟化面板注册），本项目时间线为普通 `ItemsControl` + 非虚拟化 `StackPanel`，无锚点候选，框架不做任何补偿；② 翻页前插由 `RebuildTimeline` 以 Clear + 整体重灌实现，UI 侧只见 Reset + 顺序 Add（无 `Insert(0)` 事件），Clear 与重灌同步发生在同一帧，布局净效果等价前插，原偏移落在新内容顶端。
- 实现（`ConversationView.axaml.cs`，+12 行）：置锚判据为「`IsLoadingOlder=true` 窗口内到达的 `ConversationItems` Reset」——翻页重建专属信号，会话切换等其它 Reset 不置锚（其偏移归零属预期，2026-09-20 截图修复的语义保持不变）；补偿分支同时推进 `_messagesExtent` 基线，避免后续流式增高拿过期 extent 误判贴底。解除时序不变：`IsLoadingOlder=false` 后 Post 到 Background 优先级（低于 Layout），布局落地后才解除。流式贴底与用户上翻不打扰逻辑未动。
- 测试：新增 2 项 VM 层调用顺序契约测试——`LoadOlderRebuildRaisesResetInsideLoadingWindow`（翻页重建的 Reset 必须在 IsLoadingOlder 窗口内，即视图置锚判据成立）与 `SessionSwitchRebuildRaisesResetOutsideLoadingWindow`（会话切换的 Reset 不进入窗口，不误触发锚定）。视图级状态机测试在本环境不可行：testhost 以真实 Win32 平台 `SetupWithoutStarting` 初始化后，任何 `await` 的线程延续切换都会原生崩溃 testhost（构造视图与否无关）；引入 Avalonia.Headless 与既有平台初始化冲突，超出本任务范围，未采用。
- `dotnet build DshDesktop.slnx`：0 警告 0 错误。`dotnet test`：66 通过、3 按设计跳过，连续 3 次无抖动。无反射/新依赖/XAML 变化，不影响 Native AOT 路径，未重跑 AOT 发布。
- 未验证：窗口内「点击加载更早后视口保持不动」的人工复验（逻辑与 2026-09-20 截图验证版一致并补回置真路径）；macOS/Linux。

后续每个阶段记录：实现范围、目标平台、必要验证命令、实际结果、未验证事项。只有验收通过的任务才标记完成。


## 技术参考

- [Avalonia Native AOT](https://docs.avaloniaui.net/docs/deployment/native-aot)
- [.NET Native AOT 跨平台编译限制](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile)
- [System.Text.Json 源生成](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
