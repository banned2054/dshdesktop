# 后端兼容性记录

核对日期：2026-10-02。

本文件记录 DshDesktop 当前依赖的后端契约、本端补充逻辑，以及升级 DSH 时需要检查的内容。当前能力与缺口见 [中文说明](README.zh-CN.md) 和 [English README](../README.md)。开发由每次明确提出的需求驱动，本文件不规定开发阶段、优先级或交付承诺，也不授权执行升级或后续功能。

## 参考版本与证据边界

- 当前基线为 `dsh-v0.2.0-rc.2`，commit `639ed01539`：2026-10-01 本机 checkout master 自 `0.1.7-rc.2`（`477b4f4205`）快进至该 commit 并重建，junction CLI 实测 `0.2.0-rc.2`，launcher 冒烟出 `ready` 且干净关停（见「历史兼容性记录」2026-10-01 段）。后续升级核对自该基线向前 diff，不再对照 0.1.7。
- 本机参考路径 `C:/Code/JavaScript/deepseek-harness` 只用于分析。应用使用 `DSH_DESKTOP_RUNTIME_DIR` 指定的已构建 runtime，不硬编码该源码路径。
- checkout 和包版本核对不证明当前运行链路已通过验收。更换 runtime 时须分别记录源码版本、构建产物和验证结果。

## Host、传输与数据边界

客户端使用 Avalonia 原生界面和现有 Node Harness 后端。会话、工作区归属、权限和 Agent 执行的权威状态由后端持有；客户端不修改 Harness 内部存储，也不建立第二套权威会话数据库。

- Infrastructure 的 [launcher](../DshDesktop.Infrastructure/Assets/Backend/launcher.mjs) 桥接原 Host 的 Node IPC，向 C# 提供逐行 JSON 控制协议 `v: 1`。stdout 仅用于控制帧，stderr 用于日志；就绪、错误、停止回执和退出清理需要保持一致。
- 当前 Host 入口为 runtime 内 `node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js`。解析模式默认 `runtime`，`link` 可显式选择。升级需核对入口、profile、原生依赖和运行资产，不能仅更新源码而沿用旧构建产物。
- [运行配置](../DshDesktop/Services/Backend/DesktopBackendConfiguration.cs) 中插件 profile 为应用私有；Harness home 默认按 `DSH_HOME`、`~/.dsh` 解析，也可通过 `DSH_DESKTOP_DSH_HOME` 覆盖。用户会话和凭据不能当作测试夹具随意改动。
- [认证](../DshDesktop.Harness/Services/Connection/HarnessAuth.cs) 以本次启动的 token 地址交换 Cookie；HTTP 和 WebSocket 共用 Cookie。敏感地址、Cookie 和凭据值不进入文档或日志。
- [一元 RPC](../DshDesktop.Harness/Services/Connection/HarnessRpcClient.cs) 使用 `POST /api/<method>`，请求类型为 `client-request`；[信封](../DshDesktop.Harness/Services/Connection/RpcEnvelope.cs) 的 `payload.args` 按后端方法形参名组织，错误保留 `code/message/details`。
- [流复用](../DshDesktop.Harness/Services/Connection/HarnessStreamMux.cs) 使用 `/api/remote.mux` WebSocket。重连需恢复事件代、重新读取状态和订阅；不能通过生成新 requestId 自动重发结果不明确的用户操作。

## 已消费的一元 API

以下表格按当前服务调用整理。`args` 指 RPC 信封内的 `payload.args`；DTO 或源生成注册中出现一个类型，不等于该能力已经接入。

| 方法 | 当前 args 与用途 | 升级检查重点 |
| --- | --- | --- |
| `session/list` | `{_request:{cursor?}}`，会话目录 | 目录分页、空白三态、行投影与缓存缺失的语义 |
| `session/create` | `{request:{workspaceId?,sessionId?,agentPreset?}}`，创建或收养会话 | 工作区关联、已有 SessionId 的复用、预设绑定、错误 details；0.2.0 核对：未知 `agentPreset` 报 `agent-preset/not-found`（details 含 available 列表），收养冲突报 `agent-preset/conflict`；请求 DTO 另有 `cwd`，当前服务未赋值 |
| `session/modelCatalog` | `{}`，模型目录 | `groups/default/failures` 与每个模型的 `reasoning.efforts/defaultEffort` |
| `session/selectModel` | `{request:{sessionId,provider,model,reasoningEffort?}}` | 选型回显、能力校验和不支持档位的错误语义 |
| `session/page` | `{request:{address,throughSeq,beforeSeq,maxMessages}}`，向前加载历史 | 包含/排除游标、消息对齐、`records/hasMore`；当前每页预算 50 条 |
| `session/prompt` | `{request:{requestId,sessionId,mode:"queue",content,clientTimeZone?}}` | requestId 幂等、接受与执行的区别、文本分块和 IANA 时区；本端目前仅发送文本 |
| `session/cancel` | `{request:{sessionId}}` | 取消后仍可能 committed，部分回复的 interrupted 标记与流式结算 |
| `session/fork` | `{request:{sessionId}}`，以最近完成 turn 的事件前缀为种子创建子会话 | 返回新 `sessionId`；无已完成 turn 报 `session/fork-unavailable`，子会话经 `api-session/added` 进入列表；本端未消费 `atSeq` 指定分支点 |
| `session/rename` | `{request:{sessionId,title}}`，重命名会话 | 返回规范化标题与提交它的 `seq`；无效标题报 `session/title-invalid`；本端用于分叉子会话的尾部序号递增改名与会话菜单的主动重命名 |
| `session/projections` | `{request:{sessionId}}`，后台空白核实 | `asOfSeq/values`、不存在时的 null、只读且不激活 Agent、不调用模型或持久化缓存的语义 |
| `workspace/create` | `{request:{path}}`，登记已有目录 | 目录规范化、幂等去重和 `workspace/follow` 的归属回流 |
| `workspace/rename` | `{request:{workspaceId,title}}`，重命名工作区显示名 | 返回重命名后的 `workspace` 行；`workspace/name-conflict` 查重为后端权威，本端确认前仅做输入校验 |
| `workspace/delete` | `{request:{workspaceId}}`，从注册表移除工作区 | 返回 `{deleted:true}`；只删注册，不删目录与会话，其下会话按后端记账回到「未分组」 |
| `workspace/archiveSession` | `{request:{sessionId}}`，归档会话（移出列表表面，记录保留） | 返回全量 `archivedSessionIds`；运行中会话报 `workspace/session-active`（本端直接呈现错误，未做「停止并归档」二次确认） |
| `credentials/describe` | `{refs:[引用名]}`，批量查询凭据解析状态 | 返回字典的 `configured/source/writable`，不包含密钥值；批量 ≤64，超限或语法错整包拒 |
| `settings/describe` | `{}`，全量读取设置文档 | 命名空间视图的 `schema/value/base/user/secrets/revision`；value 恒为脱敏生效值，secret 路径只暴露配置状态 |
| `settings/update` | `{ns,patch,expectedRevision?}`，patch 深合并进该 ns 用户段 | args 按形参名扁平组织；`expectedRevision` 缺省=无条件写；`settings/conflict` 语义为重读重放；写后以返回视图落状态 |
| `settings/replace` | `{ns,section,expectedRevision?}`，整段替换该 ns 用户段 | section 为完整替换内容，用户段旧覆盖字段随替换消失；基线层继续在生效值中透出 |
| `settings/mutate` | `{ns,ops,expectedRevision?}`，path 寻址按序编辑用户段 | ops 为 `{op:'set',path,value}` / `{op:'unset',path}`；ops 解析于服务端现存储段（用户段） |
| `settings/openSettingsDocument` | `{}`，物化 patch 文档并调系统编辑器 | 返回 `{opened:true}`；形参 signal 为中止信号，本端映射 CancellationToken |
| `credentials/set` | `{ref,value}`，写入凭据值（不可读取回） | ref 语法 `^[A-Za-z_][A-Za-z0-9_]*$`、value 非空；`credential/rejected` 的 message 为 seam 原文，须原样展示 |
| `credentials/unset` | `{ref}`，移除凭据值 | 同上 |
| `$events/result` | `{request:{clientId,eventId,outcome}}`，交互回执 | 事件代、取消与过期回执；审批使用 result，未支持的 waterfall 使用 rejected |
| `permissionPresets/catalog` | `{}`，权限预设目录 | `options[].value/name/description`、`defaultPreset` 及目录变更广播；0.2.0 另返回 `defaultOptions`（本端忽略），AUTO 预设审批策略为 ask（原 never） |
| `commands/execute` | `{agentId,line:"/permission <preset>",submittedAttachments:[]}`，切换权限 | args 为扁平命名参数；无命令时值缺失；RPC 成功不等于预设已生效，须等待投影 |

消费入口：[会话服务](../DshDesktop.Harness/Services/Sessions/HarnessSessionService.cs)、[工作区服务](../DshDesktop.Harness/Services/Workspaces/HarnessWorkspaceService.cs)、[权限服务](../DshDesktop.Harness/Services/Permissions/HarnessPermissionPresetService.cs)、[设置服务](../DshDesktop.Harness/Services/Settings/HarnessSettingsService.cs)、[凭据服务](../DshDesktop.Harness/Services/Settings/HarnessCredentialService.cs)、[连接与回执](../DshDesktop.Harness/Services/Connection/HarnessConnection.cs)。

`agentPresets/list`、用户问题交互、附件与插件管理不能从后端存在相应能力推定为本端已支持；`workspace/unarchiveSession` 与归档筛选/恢复界面当前未接入（本端归档后行隐藏，恢复入口待后续）。

## 已消费的流、事件与投影

| 流端点 | 当前消费内容 | 升级检查重点 |
| --- | --- | --- |
| `session/follow` | 顶层 session 地址、`maxMessages:50`、`assistantStream:true`；快照、持久化事件、助手流式帧 | 快照记录与历史窗口、序号、流结局、模型和权限投影基线 |
| `workspace/follow` | 空 args；baseline、upsert、remove、order、archived（pinned 帧属上游协议，本端不再消费） | 注册表顺序、工作区会话成员、空组和移除后的呈现；baseline 的 value 携带 archived 全量集合（feed.ts baseline()，缺消费会冷启动丢归档隐藏）；增量 archived 为 registry 级全量集合帧，驱动归档隐藏。置顶已迁出后端协议（本端自有方案，见下文），baseline 的 pinnedSessionIds 与 pinned 增量帧被忽略 |
| `session/control` | 空 args；全局 baseline 和 projection 更新 | 投影键、整值替换、seq 水位、快照与实时更新的合并 |
| `$events` | 空 args；ready、emit、waterfall、cancelled | clientId 事件代、审批待决项清理、目录广播与未支持交互的拒绝回执 |

- `$events` 中 `api-session/*` 驱动会话目录刷新及空白核实失效；`permission-presets/catalog-changed` 驱动权限目录重读；`approval/request` 进入工具审批界面；`settings/document-updated`（位置参数 ns/revision）驱动设置文档按命名空间刷新，`credentials/reference-updated`（位置参数 ref）驱动凭据状态重查，`credentials/record-updated` 同属转发事件但本端未消费。其余 waterfall 当前按协议拒绝，用户问题尚无回答界面。
- 已消费的投影包括 `title`、`sessionListMetadata`、`modelSelection`、`tokenUsage`、`sessionStats` 和 `permissions`。字段名、可空性、序号及含义均需核对，接口名相同不能证明兼容。
- `sessionListMetadata.blank` 是空白核实依据，缺失元数据保持 Unknown；不能用标题、Token、缓存缺失或一时没有气泡来推断空白。
- `modelSelection` 读取 `next`，再回退 `lastUsed`；推理档位来自模型目录，模型不支持的档位应回退其默认档位，无元数据时省略显式档位。
- `tokenUsage/sessionStats` 支撑 Token、缓存命中和生成速度展示；`permissions.currentValue` 是已创建会话的当前权限依据，点选请求不乐观改写它。
- 持久化事件中的 `user/message`、`assistant/message`、`tool/call`、`tool/result`、`turn/end` 及消息块、`source.kind`、中断标记影响时间线。工具身份和结果位置、注入上下文过滤、轮次边界与流式结束语义均需要真实样本核对。

解析入口：[FollowFrames](../DshDesktop.Harness/Models/Events/FollowFrames.cs)、[SessionControlFrames](../DshDesktop.Harness/Models/Events/SessionControlFrames.cs)、[AssistantStreamFrames](../DshDesktop.Harness/Models/Events/AssistantStreamFrames.cs)、[RemoteEventFrames](../DshDesktop.Harness/Models/Events/RemoteEventFrames.cs)。

## 本端行为与补充逻辑

分类描述实现归属，不声称这些功能永远是 DSH 本体所没有的。今后新增本端能力时补充依赖和实现原因；上游出现类似功能时，评估语义、交互与维护成本再决定是否接入。

| 能力 | 当前本端处理与原因 | 后端依赖及后续检查 |
| --- | --- | --- |
| 原生 GUI 与搜索 | 纯客户端布局、侧栏缩放、弹层、标题过滤；适应桌面使用需求 | 标题和工作区数据仍来自后端；不因上游 GUI 改动自动改写本端布局 |
| 新对话草稿 | 本地保留文本、工作区、模式、模型和权限选择；首发送前不创建空白会话，草稿版本与导航代际保护迟到结果 | 首发送为 create → 必要的 selectModel → 预选权限命令 → prompt；上游新增创建参数或草稿能力时核对流程与竞态 |
| 不使用工作区入口 | 本端显式兼容选项，允许 workspaceId 为空 | 核对后端无工作区创建能力，不默认复制上游工作区选择策略 |
| 内置 Agent 模式 | 本端固定 `standard/ptc/minimal/cordis` 四项，以 create 的 agentPreset 绑定 | 目录化、默认值、可用性与开始后的锁定语义；自定义预设尚未接入 |
| 权限选择 | 会话以投影为准；草稿为本地预选，首消息前执行 `/permission`；full access/auto 保留确认步骤 | 核对目录值、命令结果、广播、投影以及未来可能新增的创建期权限参数 |
| 创建失败后的恢复 | 记住已创建会话及目标，关联失败先收养恢复，避免重试额外创建 | `session/workspace-attach-failed` 的 SessionId/details；已有会话的收养与冲突语义 |
| 工作区菜单 | 工作区行悬浮三点菜单（置顶工作区/重命名/删除），重命名与删除交互对齐参考客户端；rename/delete 成功后本地先按 follow 帧语义落投影，帧回流幂等对齐；置顶工作区走本地置顶注册表（见下文），不调后端 | `workspace/rename`、`workspace/delete` 的错误码（name-conflict/not-found）与广播帧；上游菜单形态变化不自动同步 |
| 会话行置顶与归档（置顶为本端自有方案） | 会话行悬停时相对时间与置顶标记让位给三点菜单/归档/置顶三个按钮（对齐参考客户端 sessionRow 的纯 CSS 互换）；归档默认移出列表表面、归档当前会话后回到草稿页，归档成功后本地先按返回的全量集合落投影，帧回流幂等对齐。置顶不消费后端集合：`ISidebarPinService`（Infrastructure `SidebarPinService`）把会话与工作区置顶持久化到本项目配置文件（`%AppData%/DshDesktop/sidebar-pins.json`，多实例并发以磁盘最新状态为基准做读-改-写合并（实例内信号量加跨进程 `.lock` 文件锁互斥），原子替换写入，损坏按空起底）；侧栏重建投影时形成高于工作区一层的「置顶」分类——置顶工作区组在上、置顶会话在下，同类按更新时间降序；置顶会话从原分组提取（不重复出现），置顶工作区内的置顶会话与该工作区并列（不嵌套）；单列表下置顶会话浮顶、置顶工作区不参与投影。**未验证：**GUI 手工冒烟与 AOT 发布 | `workspace/archiveSession` 与 archived 帧的集合语义；`workspace/session-active` 错误目前仅呈现、未做停止并归档确认；取消归档与归档筛选界面未接入；上游 pinned 帧/字段如调整结构需确认本端忽略逻辑不受影响 |
| 会话分叉 | 会话菜单「分叉会话」以最近完成 turn 为界复制出独立新会话，子会话经目录刷新上屏、不切换选中（对齐参考客户端）；源会话有标题时本端做尾部 (N)/（N）序号递增改名（对齐参考客户端 increaseTitle），改名失败不影响已创建的分支 | `session/fork` 的 `fork-unavailable/not-found` 错误码与 `api-session/added` 摘要（含 parentSessionId）；`atSeq` 未消费，上游调整分支点语义时需复查 |
| 会话重命名 | 会话菜单「重命名」（官方 order 200）打开输入弹窗（预填当前标题并全选，Enter 确认、Esc/取消/浅失焦关闭）；确认直调 `session/rename`，成功后就地落服务端规范化标题（本地先落投影），列表刷新同值回流幂等对齐；失败留在弹窗内可重试。对齐官方：未变更标题不阻止确认（确认当前自动标题即「钉住」），本地也无重名冲突检查 | `session/rename` 的错误码（`title-invalid/not-found`）与改名持久化事件回流；上游菜单形态变化不自动同步 |
| 历史 Unknown 空白核实 | 本端调度只读 projections 查询，限并发、合并在途、退避；失败保留可见，已参与会话不被迟到空白结论隐藏 | 上游目录元数据、缓存格式或只读查询语义变化时复查是否仍需补充逻辑 |
| 时间线和过程折叠 | 本端把消息、思考和工具活动组织为轮次过程，保留最终回复；历史未读全时不提前折叠 | 记录格式、source、轮次与子调用表示变化；上游新增聚合投影时评估复用 |

主要消费方：[MainWindowViewModel](../DshDesktop/ViewModels/MainWindowViewModel.cs)、[ComposerViewModel](../DshDesktop/ViewModels/ComposerViewModel.cs)、[PermissionSelectorViewModel](../DshDesktop/ViewModels/PermissionSelectorViewModel.cs)、[SidebarViewModel](../DshDesktop/ViewModels/SidebarViewModel.cs)、[SessionBlankVerifier](../DshDesktop.Harness/Services/Sessions/SessionBlankVerifier.cs)。

## 升级 DSH 时的检查

1. 记录旧版本、新版本及 commit，确认实际 runtime 的包、`lib` 产物、原生依赖和资产。检查 Host 启动与插件加载；API 兼容不代表插件或运行资产兼容。
2. 对照上面的消费清单追踪上游实现差异，检查参数名、信封、字段、枚举、错误 details、读写副作用、事件代、投影和取消/重连语义。新增参数尤其要区分 `request`、`_request`、`refs` 与扁平 args。
3. 检查上游新增能力是否覆盖本端补充逻辑或扩展需求。保留仍有必要的本端行为；变更实现时记录原因，不自动按上游界面或功能列表扩展任务。
4. 按变更风险验证协议、VM、launcher 和真实 Host，必要时补充 GUI、真实模型或 AOT。区分源码核对、测试、截图和真实点击，不能用模拟后端证明真实兼容。
5. 更新本文件的消费清单、参考基线及简洁验证结论。记录未验证项；不累积无关开发日志，也不把候选能力写成执行路线。

现有验证入口：

| 范围 | 入口与使用边界 |
| --- | --- |
| 本地构建与测试 | `dotnet build DshDesktop.slnx`、`dotnet test DshDesktop.Tests/DshDesktop.Tests.csproj`；已恢复依赖时可用 `--no-restore`，须说明未重新恢复 |
| 协议和 JSON | `HarnessProtocolJsonTests`；涉及 DTO 变化还需检查 `HarnessJsonContext` 源生成注册 |
| Host 控制 | `NodeLauncherProtocolTests`；使用夹具验证控制协议，不等于真实 DSH Host 验收 |
| 真正后端 | 设置 `DSH_E2E_RUNTIME_DIR` 后运行 `--filter RealBackendE2ETests`；默认隔离 home，包含创建/收养及数据写入，仍须检查具体用例范围 |
| 共享 home 诊断 | `DSH_E2E_REAL_HOME=1` 启用 `RealHomeListBlankStateReadOnlyDiagnostic` / `RealHomeProjectionsBlankVerificationReadOnly`；这两项为只读，不能将整个真实测试类都视为只读 |
| 真实配置与模型 | `RealModelConfigurationTests` 查询目录/凭据且创建选型会话；`RealModelConversationTests` 还需 `DSH_E2E_REAL_MODEL=1`，会发送消息并调用模型。共享 home 写入或模型消费按当次授权执行 |
| Windows AOT | `dotnet publish DshDesktop/DshDesktop.csproj -c Release -r win-x64 --self-contained true`，并启动产物核对相关交互和退出清理；普通 build 不替代此验收 |

## 历史兼容性记录

以下各段保留历次版本升级与回退的证据：2026-09-29 两段来自原开发计划工作区（其中的操作已经发生于原记录所述任务，不是本次执行指令，版本和包状态也不能视为持续有效），2026-10-01 段为当前基线的建立依据。

后端 runtime 升级至 dsh-v0.2.0-rc.2 的兼容性验证记录（Windows 11 x64，2026-09-29）：

- 背景：应用户要求将 `.backend-runtime` junction 直连的 deepseek-harness checkout 由 dsh-v0.1.7-rc.1 fast-forward 至 origin/master（639ed01539，即 dsh-v0.2.0-rc.2 release，794 commits），`pnpm install && pnpm run build` 重建 lib 产物（产物不随 pull 更新）。客户端代码零修改。
- 协议核对结论（对 46a7f68b09..HEAD 的 diff 与客户端消费面逐项对齐）：客户端消费的 11 个一元 RPC（session/list、create、selectModel、modelCatalog、page、prompt、cancel、projections、workspace/create、credentials/describe、$events/result）与 4 个 mux 流端点（session/follow、workspace/follow、session/control、$events）方法名与 wire 形状零变更；RPC 信封、认证握手、session 存储格式（仍 v4）、投影/projcache 语义均未变。上游破坏性变更均不在客户端消费面：`account/*` 三方法新增首位 client 元数据参数、`workspace/initializeDefault` 删除请求参数（客户端均未调用）；`modelCatalog.routableProviders` 语义收窄为仅含可用模型的 group（客户端仅 DTO 定义，UI 未消费）；投影损坏改抛 `SESSION_QUERY_CORRUPT_SESSION`（SessionBlankVerifier 已有 `catch (Exception)` 退避兜底）；approval `displayReason` 为新增可选字段（手工 TryGetProperty 解析自然忽略）。`session/selectModel` 的模型可用性校验增强沿用既有 `session/model-unavailable` 错误码。
- launcher CLI 冒烟：`launcher.mjs --resolution runtime`（隔离 profile/home）出 `ready` 帧，shutdown 回执 clean（exit 0）；desktop-host 入口仍为 `lib/index.js`，junction 路径有效。
- `dotnet build DshDesktop.slnx`：0 错误；警告仍为既有 6 条 xUnit2013。
- `dotnet test`：133 通过、0 失败、7 按设计跳过（真实后端/模型 E2E）。
- `DSH_E2E_RUNTIME_DIR=<0.2.0 runtime> dotnet test --filter RealBackendE2ETests`：3 通过（workspace 登记、按工作区创建与列表三态、收养链路）、2 跳过（RealHome 诊断另跑）。
- `DSH_E2E_REAL_HOME=1` 真实 home 只读诊断（仅 session/list，不创建不发送）：通过，0.2.0 对真实会话数据解析无回归。
- 未验证：GUI 手工冒烟（沿用 UIA 环境限制）；Native AOT 发布未重跑（无新增客户端依赖与反射路径）；macOS/Linux；0.2.0 新增协议能力（userQuestions/schedule/productAnalytics 命名空间、account/* 方法、quit-inspection 等）尚未接入客户端，属后续功能阶段范围。

勘误：harness runtime 统一回 dsh-v0.1.7-rc.2（Windows 11 x64，2026-09-29）：

- 背景（用户决策）：全局 npm 安装的 `@deepseek-ai/dsh`（WebUI 入口）升级 0.2.0-rc.2 后，用户已装的两个第三方插件（`@yuxianglin/dsh-bridge-browser@0.0.5`、`dsh-git-rollback@0.1.10`）被插件兼容性检查拒载并在每次启动时打印 refusal 警告。经与用户确认，将 npm 全局 dsh 与 git checkout 统一到 npm latest = 0.1.7-rc.2。semver 理论验证（`npx semver`）本判断 0.1.7-rc.2 不满足插件 peer 范围（prerelease 元组规则），但实测 0.1.7-rc.2 的检查逻辑对 prerelease 范围宽松处理，`dsh web` 干净启动、零警告、插件正常加载——以实测为准；拒载行为系 0.2.0 收紧所致。
- 操作：`npm i -g --allow-scripts=... @deepseek-ai/dsh@0.1.7-rc.2`（node-pty/koffi 等 native postinstall 需 allow-scripts 才执行）；git 源码按用户要求 master 直接 `git reset --hard dsh-v0.1.7-rc.2` 停在 477b4f4205、不建分支不 pull（对 origin/master 为纯 behind，随时可 fast-forward 回最新）；`pnpm install && pnpm run build` 重建（lockfile 随版本回退）。
- 客户端侧验证：launcher CLI 冒烟 ready + clean shutdown（exit 0）；`DSH_E2E_RUNTIME_DIR` 真实 Host E2E 3 通过 / 2 跳过。客户端代码继续零修改（0.1.7-rc.2 为客户端既有开发基线 0.1.7-rc.1 的修复版）。
- 未验证：GUI 手工冒烟；Native AOT 未重跑；macOS/Linux。0.2.0-rc.2 升级验证记录保留于上，作为后续接入 0.2.0 的协议核对依据。

升级基线重设为 dsh-v0.2.0-rc.2（Windows 11 x64，2026-10-01）：

- 操作：npm 全局 `@deepseek-ai/dsh` 升至 0.2.0-rc.2（新版 npm 的 `--allow-scripts` 需显式列出 `koffi`、`node-pty` 等原生模块包名，否则其安装脚本不执行）；0.2.0 收紧插件 prerelease peer 检查，两个第三方插件被拒载，经用户决策从 npm web profile 移除 `@yuxianglin/dsh-bridge-browser` 与 `dsh-git-rollback`（`dsh plugin allow-version --accept-risk` 豁免路径未采用）；harness checkout master 自 `477b4f4205` 快进至 `639ed01539`（无新分支），`pnpm install && pnpm run build` 重建，junction 入口实测输出 `0.2.0-rc.2`。客户端代码零修改。桌面侧 default profile 仅含 `dsh-base`，无第三方插件，插件门禁不影响本应用。
- 增量协议核对（2026-09-29 审计之后新增的消费面，逐项对照 0.2.0-rc.2 源码与 `46a7f68b09..639ed01539` diff）：`session/fork`、`session/rename`、`workspace/rename`/`workspace/delete`/`workspace/archiveSession`、`permissionPresets/catalog`、`commands/execute`、`session/create` 的 `agentPreset`，以及 `api-session/added`（含可选 `parentSessionId`）、workspace/follow 的 baseline archived 与增量全量帧、approval 可选 `displayReason`，全部 wire 兼容；差异仅为可忽略的字段增量（`WorkspaceView.createdAt`、catalog `defaultOptions`）。内置 Agent 预设仍为 standard/ptc/minimal/cordis；`workspace/pinSession`/`unpinSession` 仍存在但本端不调用，pinned 帧结构未变，忽略策略不受影响。0.2.0 新挂载 userQuestions/schedule/productAnalytics remote 与 `credentials/record-updated` 等转发事件，均为纯增量。
- 行为级变化：AUTO 权限预设的工具审批策略由 never 改为 ask（形状不变，客户端经目录自动继承）；`commands/execute` 返回值为 `{commandId, result:{kind,text}}` 嵌套（非本次变更），本端仅判空使用，语义不受影响。
- 验证：launcher CLI 冒烟（隔离 profile/home）出 `ready` 并 `shutdown-complete`（exit 0）；冒烟中 `desktop-product-telemetry` 激活失败（config 缺 `serviceVersion`）、`product-analytics` 因依赖随之 pending，非致命、仅影响埋点，正式 GUI 启动如出现该警告属预期。未验证：真实 GUI 冒烟、RealBackendE2E 复跑、Native AOT、macOS/Linux。自本段起参考基线指向 `0.2.0-rc.2`，后续升级 diff 自 `639ed01539` 向前，不再对照 0.1.7。

## 已有功能验证摘要与限制

以下为原开发计划中仍有关联的历史摘要，没有在本次文档整理中重跑。

| 日期 | 历史记录 | 证据范围与限制 |
| --- | --- | --- |
| 2026-09-20 | 真实 GLM 模型流式往返与生成中取消通过 | 当时模型与 home 的验证；不覆盖所有模型或当前升级版本 |
| 2026-09-28 | 历史 Unknown 查询、冷缓存与共享 home 只读核实；Windows x64 AOT 发布和模拟启动通过 | 真实 projections 契约已验证；不覆盖之后新增的模式、权限和推理档位变化，也不覆盖 macOS/Linux |
| 2026-09-29 | 草稿版本与失败恢复测试；工作区登记真实 Host E2E；Agent 模式绑定真实 Host E2E 和模拟 GUI 选择通过 | 原生文件夹选择完整体验、创造模式完整往返和部分弹层仍未验证；自定义模式未接入 |
| 2026-09-30 | 权限选择及草稿预选、推理档位过滤实现；最近记录为 171 通过、0 失败、8 跳过，build 0 错误 | 权限真实目录/命令往返与首发送权限应用未做真实 Host E2E；部分弹层点击、当前 AOT、macOS/Linux 未重验 |
| 2026-09-30 | 会话行悬浮置顶/归档操作条与三点菜单实现；协议与 VM 测试 187 通过、0 失败、8 跳过，build 0 错误 | 置顶/归档未做真实 Host E2E 与 GUI 手工冒烟；AOT 未重验；取消归档入口未实现 |
| 2026-10-01 | 置顶迁出后端协议（不再消费 workspace/pinSession·unpinSession 与 pinned 帧），改为本端自有置顶（本地配置文件持久化）与侧栏置顶分类（工作区在上、会话在下，同类按更新时间排序，工作区与其中置顶会话并列）；协议与 VM 测试 201 通过、0 失败、8 跳过，build 0 错误 | 置顶分类未做 GUI 手工冒烟；AOT 未重验（含新增 Infrastructure JSON 源生成上下文）；归档链路沿用 09-30 结论 |
| 2026-10-01 | 后端基线升至 0.2.0-rc.2：checkout master 快进至 639ed01539 并重建，launcher 冒烟 ready 与干净关停；09-29 审计后新增的消费面（分叉/重命名/归档/权限预设/agentPreset 绑定及相关事件帧）逐项核对 0.2.0 源码全部兼容；客户端代码零修改，详见历史兼容性记录 | 真实 GUI、RealBackendE2E 复跑、Native AOT、macOS/Linux 未验证；AUTO 预设审批 ask 的行为变化待 GUI 观察 |
| 2026-10-02 | settings/credentials RPC 域客户端接入层实现：Core 契约（ISettingsService/ICredentialsService、视图模型、conflict/rejected 异常）、Harness DTO 与错误映射、`settings/document-updated` 与 `credentials/reference-updated` 事件帧、Simulated 内存实现与 App 装配；事件名/形参名对照 0.2.0-rc.2 源码核实（settings-controller、remote-events.ts）；测试 241 通过、0 失败、8 跳过，build 0 错误；Windows x64 AOT 发布通过 | 真实后端 settings/credentials 往返、设置 GUI、AOT 产物启动核对、macOS/Linux 未验证；设置界面尚未接入 ViewModel |

用户问题、附件、设置、偏好持久化和高级原生界面尚未实现。断线专项、长会话性能、输入法/快捷键、Markdown 复制与部分富元素交互仍需验证；这些是当前缺口和证据边界，不表示已安排执行。
