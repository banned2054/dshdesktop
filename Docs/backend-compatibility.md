# 后端兼容性记录

核对日期：2026-10-02。

本文件记录 DshDesktop 当前依赖的后端契约、本端补充逻辑，以及升级 DSH 时需要检查的内容。当前能力与缺口见 [中文说明](README.zh-CN.md) 和 [English README](../README.md)。开发由每次明确提出的需求驱动，本文件不规定开发阶段、优先级或交付承诺，也不授权执行升级或后续功能。

## 参考版本与证据边界

- 当前基线为 `dsh-v0.2.0-rc.2`，commit `639ed01539`：2026-10-01 本机 checkout master 自 `0.1.7-rc.2`（`477b4f4205`）快进至该 commit 并重建，junction CLI 实测 `0.2.0-rc.2`，launcher 冒烟出 `ready` 且干净关停（见「历史兼容性记录」2026-10-01 段）。后续升级核对自该基线向前 diff，不再对照 0.1.7。
- 本机参考路径 `C:/Code/JavaScript/deepseek-harness` 只用于分析。应用使用 `DSH_DESKTOP_RUNTIME_DIR` 指定的已构建 runtime，不硬编码该源码路径。
- 发布构建的 DSH 版本、上游标签与完整 commit 固定在根目录 [backend-version.json](../backend-version.json)。[发布流程](releasing.md) 会验证源码和包版本，将生产依赖与上游校验锁准备的解释器/Office 资产打入 Windows x64 ZIP；`Run.cmd` 设置包内 runtime、Node 和 primary-runtime 路径。About 仍显示实际 runtime 包版本。该流程尚未在 GitHub runner 上完成全量打包验证，不能视为新增的真实后端/GUI 验收记录。
- checkout 和包版本核对不证明当前运行链路已通过验收。更换 runtime 时须分别记录源码版本、构建产物和验证结果。

## Host、传输与数据边界

客户端使用 Avalonia 原生界面和现有 Node Harness 后端。会话、工作区归属、权限和 Agent 执行的权威状态由后端持有；客户端不修改 Harness 内部存储，也不建立第二套权威会话数据库。

- Infrastructure 的 [launcher](../DshDesktop.Infrastructure/Assets/Backend/launcher.mjs) 桥接原 Host 的 Node IPC，向 C# 提供逐行 JSON 控制协议 `v: 1`。stdout 仅用于控制帧，stderr 用于日志；就绪、错误、停止回执和退出清理需要保持一致。
- `NodeHostLauncher.Exited` 可由 Host 的 `exited` 控制消息提前完成，不代表 launcher 已实际终止。Unix 上释放或停止超时时先发送本端 `terminate` 控制帧，由 launcher 对独立的 Host 进程组发送 SIGKILL，等待直接子进程的 `close` 后再报告 `exited`，避免父子同时强杀导致 Host 未被回收；控制等待上限 5 秒，超时使用操作系统进程树强杀兜底。Windows 保留进程树强杀与 Job Object 守卫。随后等待 launcher 实际退出（上限 5 秒）再释放进程资源；退出等待超时向调用者报告，不吞异常或把临时运行时清理失败当作成功。`NodeLauncherProtocolTests` 覆盖提前退出通知、优雅关停、停止超时/取消、无响应 launcher 与 Host 子进程回收；夹具验证不等于真实 DSH Host 或 GitHub runner 验收。
- 当前 Host 入口为 runtime 内 `node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js`。锁定的 0.2.0-rc.2 Host 在 runtime、profile 和 primary-runtime 后接收 pnpm 脚本路径与 Node bin 目录，自行进行进程内解析。launcher 保留 `--resolution runtime|link` 的参数兼容性，但不再把该字符串传到 Host 的 pnpm 参数位置；开发环境未提供 pnpm 时省略这两个可选位置参数。升级需核对入口、参数顺序、profile、原生依赖和运行资产，不能仅更新源码而沿用旧构建产物。
- 发布 staging 补齐 legacy production deploy 遗漏的必需 peer（如 `@deepseek-ai/cordis-plugin-group`），仅使用锁定 checkout 中同版本消费者实际解析到的依赖，并保留导出包的共享实例及嵌套版本。解压包 smoke 对 `fatal`、`error` 和异常 `exited` 立即失败，保留脱敏后的有限 stderr 尾部；启动失败不再被隐藏为统一的 120 秒超时。
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
| `llm/listConfigurableProviders` | `{}`，可配置提供方目录（设置面板模型分区行列表与添加下拉） | 返回条目 `{provider,displayName,settingsNs,settingsPath[],declared?,error?}`；DeepSeek 两条路由 settingsPath=[] 整节、pi-ai 路由恒 `['providers',route]`；declared 表示目录外自声明路由（拥有 displayName/api 字段） |
| `llm/discoverModels` | `{settingsNs,request:{provider?,baseURL?,api?,apiKey?}}`，探测提供方可用模型（「获取可用模型」按钮） | 返回 `{id,name?,contextWindow?,maxTokens?,inputModalities?}[]`（host 按 id 去重保端点顺序）；apiKey 仅本次使用不存储；无发现注册/参数均空/端点拒绝报 `llm/model-discovery-rejected`（message 原样展示）；`llm/listProviders`（活跃路由 `{id,name}[]`）后端存在但本端未消费——活跃性由设置文档 providers 投影得出 |
| `credentials/set` | `{ref,value}`，写入凭据值（不可读取回） | ref 语法 `^[A-Za-z_][A-Za-z0-9_]*$`、value 非空；`credential/rejected` 的 message 为 seam 原文，须原样展示 |
| `credentials/unset` | `{ref}`，移除凭据值 | 同上 |
| `GET api/changes.summary`（真实宣告的可选 fallback） | `?sessionId&seq`，读取一轮改动的文件摘要（认证 GET 路由，非 RPC 信封，返回裸 JSON） | 返回 `turn/files/total/added/deleted`；Host 保留 `cwd` 与 snapshot id 不下发；`maxFiles` 截断后 `total` 仍为全量计数；404=内容已不可用（会话销毁或 Host 重启），本端归一为 null 不重试；其余 4xx 为永久性错误按终态异常抛出不重试。负 seq 卡片读本地缓存；真实 Host 宣告的非负 seq 读此路由，不假定默认 desktop-host composition 含 producer |
| `GET api/changes.diff`（真实宣告的可选 fallback） | `?sessionId&seq&index`，读取摘要中第 index 个文件的轮首/轮末对比（同上） | `kind:text`（`before/after/hunks/coarse`，hunk 3 行上下文、行带 +/-/空格前缀）/`binary`/`oversized`；404 语义同上，索引越界亦 404。DiffView 按宣告 seq 来源路由：负 seq 读本地缓存，非负 seq 读此路由 |
| `$events/result` | `{request:{clientId,eventId,outcome}}`，交互回执 | 事件代、取消与过期回执；审批使用 result，未支持的 waterfall 使用 rejected |
| `permissionPresets/catalog` | `{}`，权限预设目录 | `options[].value/name/description`、`defaultPreset` 及目录变更广播；0.2.0 另返回 `defaultOptions`（本端忽略），AUTO 预设审批策略为 ask（原 never） |
| `commands/execute` | `{agentId,line:"/permission <preset>",submittedAttachments:[]}`，切换权限 | args 为扁平命名参数；无命令时值缺失；RPC 成功不等于预设已生效，须等待投影 |

消费入口：[会话服务](../DshDesktop.Harness/Services/Sessions/HarnessSessionService.cs)、[工作区服务](../DshDesktop.Harness/Services/Workspaces/HarnessWorkspaceService.cs)、[权限服务](../DshDesktop.Harness/Services/Permissions/HarnessPermissionPresetService.cs)、[设置服务](../DshDesktop.Harness/Services/Settings/HarnessSettingsService.cs)、[凭据服务](../DshDesktop.Harness/Services/Settings/HarnessCredentialService.cs)、[llm 目录服务](../DshDesktop.Harness/Services/Llm/HarnessLlmCatalogService.cs)、[文件改动服务](../DshDesktop.Harness/Services/Changes/HarnessWorkspaceChangesService.cs)、[连接与回执](../DshDesktop.Harness/Services/Connection/HarnessConnection.cs)。

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
- 持久化事件中的 `user/message`、`assistant/message`、`tool/call`、`tool/result`、`turn/end` 及消息块、`source.kind`、中断标记影响时间线。工具身份和结果位置、注入上下文过滤、轮次边界与流式结束语义均需要真实样本核对。`workspace/changes`（载荷 `{turn}`）仍经 session/follow 透出为会话更新，当前 UI 以其真实 seq 读取 Host 内容作为补偿，同轮本地结果优先。实时与 replay `turn/start` 均透出数值 `data.turn`（与 `turn/end` 同一 long 解析规则）；follow `header.cwd` 与 list `cwd` 落到会话行，缺失值不清空已知 cwd。普通 attach/replay 不捕获；本端首发前基线可绑定全新 session 的 turn 1，普通会话每次 prompt 前基线按发送顺序记录，只由同一 session、cwd、epoch/context 和更高 seq 的实时 turn/start 消费。内容保存在客户端进程有界缓存，Host 重启不直接删除已捕获的内容；重连快照会放弃尚未结束的基线。
- `todo/write`（载荷 `{todos:[{content,status}]}`，status 为 `pending/in_progress/completed`）驱动 composer 上方任务面板；快照窗口内按记录顺序重放（turn/start 清空显示、todo/write 填入，最终态对齐官方投影 checkpoint）。`turn/start` 清空面板显示，实时事件还触发上述本地文件基线捕获。diff 基线取最近一次 `todo/write` 的清单，todo_write 折叠行的「新增/更新/移除」按 content 匹配（status 或位置变化都计更新）；官方前端还有对 `todo_write` 的 `tool/call` 重放推导基线，本端快照重放路径暂不推导（历史窗口内的 todo_write 行只显头段「{done}/{total} 已完成」）。
- `deliverables/presented` 按官方 `isPresentedData` 校验 `data.turn`（正的安全整数）、非空 `data.callId` 和 `data.files` 数组；每项要求非空白 `path`，`description` 可缺省或为字符串。无效项目跳过并保留原数组 `index`，无效事件不产生会话更新。历史 `MapEntries`、follow 初始快照重放、实时事件和 `session/page` 共用同一映射。官方 `presentedForClosing` 只取 `seq < closing assistant/message seq` 的声明，以路径精确去重，最后一次声明更新元数据而路径顺序按首次出现保留；UI 在轮末据此投影卡片。文件单击在应用内右侧面板查看，数据按条件解析：同会话同轮 `workspace/changes`（本端快照或 Host 宣告）摘要含该文件时优先复用其完整 diff；否则从已加载 `tool/call`+`tool/result` 记录取本会话本轮、cwd 解析后路径吻合且已成功落定的 `edit`/`write` 调用展示“历史编辑片段”——优先 `tool/result` 事件 `meta.diffs`（官方 FsDiffMeta：edit/write 的 presentationMeta 随会话日志持久化，Host 在编辑现场计算、每 hunk 3 行上下文且无行号，`oldText: null` 为纯插入），无 meta 的 edit 回退参数 `old_string`/`new_string`（`replace_all` 补「无法恢复匹配次数与位置」限制说明），write 仅在结果文本标注 Created file 时可核实完整新建内容；失败、未落定或参数不完整的调用不产生片段。否则显示当前文件内容（明确标注非交付时快照；1 MB 上限、二进制/编码不支持、读取失败给具体状态）。都没有时面板显示原因并保留卡片。关联只使用 sessionId+turn+cwd 解析后的绝对路径（Windows 不区分大小写），不按文件名或「最近一个 diff」猜测；解析在后台执行，requestId 保证快速连点时迟到结果不覆盖当前面板。`tool/result` 的 `meta.diffs` 为本端新增直读消费，无该字段的历史会话自然走参数回退。“用默认程序打开”是面板内显式按钮，沿用 Infrastructure 的系统默认应用；增量 `tool/result` 到达时本端把状态与 meta.diffs 合并回已加载的发起条目（对齐快照路径的合并语义）。workspace/changes 仍走独立摘要与对比链路，二者共用读取能力但互不覆盖；交付声明记录不包含完整文件内容，无法还原完整历史 diff 或覆盖写前的原文。

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
| 时间线和过程折叠 | 本端按已知 Turn 身份隔离过程，缺失身份时沿用当前唯一开放轮并允许后续事件补全；已知不匹配的迟到边界不收束当前轮。过程组保留完整已加载输入，界面默认显示尾部 20 条，每次向前展开 20 条；上翻时固定当前可见 Seq 范围，追加事件不会移走正在阅读的步骤；回到底部仅自动尾窗恢复最近 20 条，用户手动展开的范围保留。用户主动展开/折叠状态与 Seq 锚点在追加和历史重建时保留。已收束最终消息的 reasoning 投影为组内思考-only 条目（保留 Seq/Id，不复制中断标记），组外正文与流式气泡保持独立；历史分页仍按 session/page 工作，历史未读全时不提前折叠 | 记录格式、source、Turn 可空语义、轮次与子调用表示变化；上游新增聚合投影时评估复用 |
| 交付文件卡片 | `deliverables/presented` 映射为 UI 无关声明；按 closing assistant 的 seq 过滤，同轮多事件按官方规则合并，卡片展示形态与改动卡一致：单文件为整卡可点的「已编辑 文件名」卡（右侧垂直居中「查看变更」），多文件为「已编辑 N 个文件」容器——头部静态、显示整轮增删合计，仅文件行可点（左相对路径、右 +N −N），超过 3 项折叠可展开。增删计数为本端补充推导：按该轮 workspace/changes 宣告 seq 取摘要（本地快照优先、Host GET 兜底），摘要文件按 cwd 解析路径匹配声明，二进制/超大显示形态标注，匹配不到或摘要不可用（Host 404、缓存释放）时该行静默留空，不伪造计数。单击在应用内右侧面板按数据条件展示：同会话同轮 workspace/changes 完整 diff → 已加载成功 edit/write 的历史编辑片段（meta.diffs 优先、参数回退，按事件顺序分列、片段内行号）→ 当前文件内容（标注非快照）→ 不可用原因；“用默认程序打开”为面板内按钮。关联只用 sessionId+turn+解析路径，后台解析并以 requestId 丢弃迟到结果。会话切换由 follow 代际和当前 session 守卫隔离；重复快照/重建按事件 seq 去重。文件缺失仍显示声明（行内红色说明），不恢复归档内容、不伪造完整文件 diff 或行号 | 依赖 `session/follow`、`session/page` 中的持久事件 `{turn,callId,files:[{path,description?}]}`、`tool/result` 的 `meta.diffs`（官方 edit/write presentationMeta）与 workspace/changes 摘要接口（既有消费复用）；需复查上游 meta 形状变化、present-open 的寻址/权限及文件打开行为变化 |
| 设置面板模型目录展示 | 模型分区在 DeepSeek 卡后只读展示当前生效模型目录（`session/modelCatalog`，与对话模型菜单同源），按提供方分组含 DeepSeek 与插件提供方（如 `llm-pi-ai/glm`），不做编辑与写入 | 打开面板时查询一次，失败视为无数据；插件提供方（如 `llm-pi-ai`）在 settings 文档中有自身命名空间（ns=插件 entry id，`providers.<route>` 可经 settings/mutate 写入，持久化目标即 profile 的 cordis.patch.yml），面板 v1 展示层未消费该 ns，取数走 session/modelCatalog |
| 设置面板提供商管理 | 模型分区按官方形态呈现已配置提供方行列表（圆角行卡 + 凭据圆点 + 编辑）、「+ 添加模型提供商」双 tab（第三方目录厂商 / 自定义模型 API）与路由编辑卡。行集合 = llm/listConfigurableProviders 目录 ⊕ 设置文档兜底（目录缺失/失败时 DeepSeek 与 pi-ai 路由仍呈现）；deepseek-account 行仅在 session/modelCatalog 有非空账户组时出现，显示名覆盖「DeepSeek 账号」。凭据圆点取视图 Secrets 中 `[…,apiKeyEnv]` 槽位（无显式命名引用不画点，对齐官方「named apiKeyEnv 才画」规则；官方对未命名路由另有派生 `<ROUTE>_API_KEY` 圆点，本端仅在写入时派生引用名，状态圆点未消费派生引用）。第三方厂商保存 = 最小 profile（apiKeyEnv/baseURL/models 按需，全空时物化 `{}` 收养默认）；自定义路由保存 = 完整 profile（api/baseURL/models≥1 必填）；编辑 = 字段级 diff（未建模字段不动）；密钥一律另走 credentials/set（引用名沿用已命名值否则按路由派生）。模型发现结果仅填草稿行（勾选弹层采纳），不直接写配置 | `llm/listConfigurableProviders`、`llm/discoverModels` 与 `settings/mutate('llm-pi-ai',…)` 的 `['providers',route]` 路径语义；上游 pi-ai 内置厂商目录变化自然经 RPC 透出；路由 id 正则（`^[a-z][a-z0-9]*(-[a-z0-9]+)*$`）与 api 协议枚举（openai-completions/openai-responses/anthropic-messages）为本端镜像，上游收窄时需同步 |

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
| 2026-10-02 | 文件改动域接入层实现：Core 契约（IWorkspaceChangesService、summary/diff 模型、SessionUpdate.WorkspaceChanged）、认证 GET 通道（404 归一 null 不重试）、`workspace/changes` 事件解析与 follow 流映射、Simulated 固定种子实现与 App 装配；路由与载荷对照 0.2.0-rc.2 源码核实（workspace-changes 插件、ui-deliverables present-open.ts：summary 只下发 turn/files/total/added/deleted，diff 按 kind=text/binary/oversized）；测试 247 通过、0 失败、8 跳过，build 0 错误 0 新警告 | 真实后端 changes 往返（404 语义、长 diff、binary/oversized 实样）、改动卡片 GUI、AOT、macOS/Linux 未验证；改动 UI 尚未接入 ViewModel |
| 2026-10-02 | 侧栏状态栏设置入口按钮接入：开始消费 `settings/openSettingsDocument`（此前接入层装配但无消费者），齿轮图标（Icon.SettingsOutlineRegular）path 自 0.2.0-rc.2 ui-primitives IconSettingsOutlineArtwork 原样迁移；模拟实现为空操作、失败写窗口级错误条、测试构造降级 EmptySettingsService；测试 251 通过、0 失败、8 跳过，build 0 错误；模拟模式冒烟截图通过（按钮渲染与布局） | 真实后端打开设置文档往返（系统编辑器调起）、AOT、macOS/Linux 未验证；设置面板 GUI 仍未实现 |
| 2026-10-03 | 模型分区供应商编辑改为行内展开（一次一卡、同点收起、添加位与行编辑互斥），已配置供应商编辑卡默认折叠「自定义设置」（折叠区外仅 API 密钥，密钥占位双态），输入类型空数组视同未声明并移除「至少勾选一项」自设校验；行为逐项对照 0.2.0-rc.2 `ui-settings-models` 源码（ModelsSection 单编辑卡、ProviderEditor details 折叠与 ownsIdentity 字段、ModelInputTypes absent-or-empty fallback）；探测请求 EditDeclared 分支补带路由 id（后端按路由用已存凭据应答，无需重输密钥，对齐官方 probe 构造）；测试 287 通过、0 失败、8 跳过，build 0 错误；真实 GUI 手工验证（智谱行内展开/折叠/模型列表渲染）通过 | 行内展开与折叠仅 Windows 真实后端验证；AOT、macOS/Linux 未验证；添加卡（目录/自定义双 tab）仍为直出布局，折叠化待后续 |
| 2026-10-03 | 修复 `llm/discoverModels` 线上帧：该方法为多参方法（`settingsNs` + `request` 两形参），args 必须扁平展开；此前误用单参 request 包装帧，真实后端网关以 `gateway/arguments-invalid: missing "settingsNs"` 拒绝（自接入以来从未成功过，模拟实现不走 RPC 故测试全绿）。经 launcher 直连真实后端重放验证：包装帧被拒、扁平帧 + 路由 id + 不带密钥成功按已存凭据返回模型列表（glm 11 条）；新增信封形状回归测试 | 仅 Windows 真实后端重放验证；AOT、macOS/Linux 未验证 |
| 2026-10-04 | 工具调用条目改为官方 DSH 行式呈现：折叠行 = 状态点 + 口语化中文标题 + 参数摘要 + 状态 + 时间 + 详情/收起（去蓝色气泡，悬停高亮，展开区不变）。文案为本端客户端推导（协议无描述字段，无新增消费）：新增 `Utils/ToolCallText` 对齐官方 `toolRowModel`/`classifyTool`/`deriveSummary` 规则——已知工具名映射 `tool.title.*` 中文词典（含 present 交付文件、todo_write、subagent 等），摘要按变体取参数字段首行（bash 命令、文件路径、search queries 逗号连接），未知名回退「工具调用」并保留原始名前缀，失败行摘要取错误首行转错误色，present 状态词对齐官方（正在交付/已交付/交付失败）；迟到的参数/名称经 Settle 通知刷新。测试 298 通过、0 失败、8 跳过，build 0 错误；模拟 GUI 冒烟截图（折叠/展开/失败摘要）与 Windows x64 AOT 发布 + 产物启动核对（渲染、展开、优雅退出）通过 | 工具名映射与摘要推导由单测覆盖；真实后端常见工具名（bash/read/edit/present 等）的线上实样未实测（模拟冒烟走未知名 generic 回退路径）；macOS/Linux 未验证 |
| 2026-10-04 | 任务清单域接入与 composer 上方任务面板：开始消费 `todo/write` 事件（载荷 `{todos:[{content,status}]}`）与 `turn/start`（仅清空面板显示）。Core 增 `SessionTodoItem/TodoListUpdated/TurnStarted`；Harness 解析（content 须为字符串、status 未知回退 pending）并经 follow 快照/增量透出；面板（收起头部进度 + 展开条目三态圆点、默认收起不持久化）与 todo_write 折叠行摘要（头段「{done}/{total} 已完成 · 首个进行中」+ diff 段「新增/更新/移除」按 content 匹配、status 或位置变化计更新）逐项对照 0.2.0-rc.2 官方源码（TodoPanel/TodoDock、todo-row/plan-summary/todo-diff-model/todo-history）；本端无 todo/write 的投影通道（`session/control` 的 `todos` 投影键未消费），面板数据走事件流重放。测试 311 通过、0 失败、8 跳过，build 0 错误；模拟 GUI 冒烟（面板收起/展开/再收起、折叠行头段）截图通过 | 真实后端 todo/write 线上实样与 turn/start 频次、快照窗口外基线（旧清单不可用）形态、AOT、macOS/Linux 未验证 |
| 2026-10-06 | 交付文件应用内查看：`deliverables/presented` 卡片单击不再调系统默认应用，改为打开右侧面板并按数据条件展示——同会话同轮 `workspace/changes` 完整 diff（复用现有读取链路）→ 已加载成功 `edit`/`write` 的「历史编辑片段」（新增直读 `tool/result` 事件 `meta.diffs`，即官方 FsDiffMeta 的 Host 现场计算 hunk；无 meta 的 edit 回退参数 `old_string`/`new_string`，`replace_all` 补限制说明；write 仅在结果标注 Created file 时核实新建内容；失败/未落定调用不产生片段；多次编辑按事件顺序分列，行号为片段内行号）→ 当前文件内容（标注非交付快照，1 MB/二进制/编码状态）→ 不可用原因并保留卡片；「用默认程序打开」为面板内按钮（沿用 Infrastructure opener）。新增 Core `ToolFileDiff`/`ToolActivity.MetaDiffs`、Desktop `DeliverableViewResolver`/`WorkspacePathResolver` 与 `DeliverableViewRequest`；增量 `tool/result` 到达时状态与 meta.diffs 合并回已加载发起条目。关联只用 sessionId+turn+cwd 解析绝对路径，requestId 丢弃迟到解析。模拟种子新增「交付文件演示」会话（四形态可冒烟）。相关测试 40 通过；构建 0 错误；模拟 GUI 冒烟（四形态、面板内连点切换、默认程序按钮、关闭）与 Windows x64 AOT 发布通过 | 真实后端交付会话的 meta.diffs 实样、真实 GUI、macOS/Linux 未验证；片段无法还原完整历史 diff 或覆盖写前原文（数据本身不存在） |
| 2026-10-07 | 交付文件卡片展示形态对齐改动卡：标题改「已编辑 N 个文件」（单文件「已编辑 文件名」整卡可点 + 右侧「查看变更」同命令），多文件头部静态、仅文件行可点（左相对路径、右 +N −N），折叠阈值 4→3 并复用改动卡「再显示/收起」开关与 changes-* 样式族；新增消费：卡片构造/组装器补接同轮 `workspace/changes` 宣告 seq，经既有 `api/changes.summary` 读取链路（本地快照优先、Host GET 兜底）异步取摘要，按 cwd 解析路径匹配声明显示增删行数与整轮合计，摘要不可用（Host 404/缓存释放/网络失败）静默降级为无计数，不伪造数据；同轮摘要宣告晚到时组装器回填计数来源重取。声明不再展示行内描述与「单击查看」提示（描述仍随声明保留），缺失文件保留红色说明。测试 372 通过、0 失败、8 跳过（新增计数匹配/单卡标题 2 项），构建 0 错误；模拟 GUI 冒烟（双卡计数、折叠/展开、悬停、错误行）截图通过 | 计数匹配按解析路径不猜文件名；真实后端会话（Host 摘要在线/404 两态）未实测，AOT、macOS/Linux 未验证；改动卡与交付卡同轮并存时头部计数口径不同（整轮合计 vs 声明匹配）属已知差异 |

2026-10-05 真实会话改动捕获修复：源码确认旧首发顺序为 prompt 后才建立 follow，首轮 start 因 replay/cursor 门控被跳过；follow header.cwd 在应用映射时丢失，临时首发会话行又无 cwd；MainWindow 直接忽略真实 workspace/changes；单文件读取失败原本会放弃整轮扫描。现在草稿首发送从已核实的 workspace.Path 启动后台基线任务，创建 session 后绑定 sessionId，发 prompt 前等待采样完成（失败仅放弃摘要）；发送被接受且导航上下文仍匹配才移交首次 follow。只有该预捕获允许关联 turn 1 的首次 replay start（上游 agent-loop/src/index.ts 投影 lastTurn 初始化为 0，agent.ts 开轮 +1，session/src/invariant.ts 初始化 nextTurn: 1 并校验相等），快照已有同 turn 且 seq 较晚的边界则结算，否则等待 live end；通用 replay/cursor 守卫保持，重连、会话切换、取消与 follow 结束清理未结算状态。follow header.cwd 透传，摘要缺失 cwd 不清空已知目录；绑定预捕获时用 GetFullPath/TrimEndingDirectorySeparator 与平台大小写规则比较 cwd。

本地非空差异仍生成负 seq，摘要/diff 共用客户端有界缓存，按 Host 轮末 seq 重建时间线。实际 workspace/changes 的非负 seq 接回原有 Host changes 服务（包括历史分页），同轮本地结果优先且只显示一张卡；默认 desktop-host composition 可能没有 producer，本地捕获不依赖它。扫描不跟随 reparse，排除 .git/node_modules/bin/obj，单文件 1 MiB，单快照 4,000 文件/8,000 条目/16 MiB/2 秒；不可读文件或子目录加入 Skipped 并在两侧排除，根目录不可读或全局超限仍放弃该轮，避免伪新增/删除。行级比较每文件最多 100 万 LCS 单元、每轮 400 万单元，超限按变化区段替换，轮末比较限 2 秒。缓存最多 128 轮/64 MiB 内容预算。后续 live start 到达前写入仍有短竞态，轮内外部改动无法归因。

本次验证：按授权先后执行两次 dotnet build DshDesktop/DshDesktop.csproj --no-restore，最终含 cwd 路径规范化的 Debug 构建通过，0 警告、0 错误；未 restore，未构建整个 solution，未新增/运行测试。代码静态复核覆盖事件身份、cwd、首发 replay 例外、轮末门控、负 seq 缓存、同轮优先、时间线工厂与 XAML。未验证：截图类真实会话实际显示、GUI 点击/长 diff、Host fallback 往返、取消/断线时序、AOT、macOS/Linux。

以下保留开始本次修改前工作区中的 UI 接入记录；其中 Host 读取路径现在仅为真实宣告的 fallback，原验证记录不代表本次验收：

2026-10-05 文件改动 UI 接入：workspace/changes 事件投影为同轮替换卡片，读取既有 changes.summary/changes.diff；文件行点击后在 Session 右侧打开 Banned.CodeDiff.Avalonia 0.1.0 分栏对比，支持加载、不可用、错误、二进制/过大/粗粒度和关闭状态；新增依赖样式并在切换会话时关闭面板。DshDesktop.csproj Release 构建通过；solution 测试因既有 TurnProcessReasoningProjectionTests 符号缺失无法编译，本次未改动或修复。未验证：真实后端 changes 往返、实际 GUI 点击/长 diff、Hunk 展开、AOT 产物、macOS/Linux；模拟会话未推 workspace/changes，未做端到端运行验收。

用户问题、附件、设置、偏好持久化和高级原生界面尚未实现。断线专项、长会话性能、输入法/快捷键、Markdown 复制与部分富元素交互仍需验证；这些是当前缺口和证据边界，不表示已安排执行。
