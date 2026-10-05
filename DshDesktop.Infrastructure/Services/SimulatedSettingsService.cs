using DshDesktop.Core.Exceptions;
using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using System.Buffers;
using System.Text.Json;

namespace DshDesktop.Infrastructure.Services;

/// <summary>
///     模拟设置服务：内存命名空间文档，写语义对齐后端 settings 域
///     （update 深合并用户段、replace 整段替换、mutate 按序路径编辑、revision 按命名空间乐观锁）。
///     种子数据仅用于 simulated profile 演示，不代表真实后端状态。
/// </summary>
public sealed class SimulatedSettingsService : ISettingsService
{
    private readonly List<NamespaceState> _namespaces = BuildSeed();

    private readonly Lock _syncRoot = new();

    public event EventHandler<SettingsDocumentUpdate>? DocumentUpdated;

    public Task<SettingsDescribeValue> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            IReadOnlyList<SettingsNamespaceView> views = _namespaces.Select(ToView).ToArray();
            return Task.FromResult(new SettingsDescribeValue(true, views));
        }
    }

    public Task<SettingsNamespaceView> UpdateAsync(string ns, JsonElement patch, long? expectedRevision = null,
                                                   CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (view, change) = Write(ns, expectedRevision, state => state with { User = DeepMerge(state.User, patch) });
        DocumentUpdated?.Invoke(this, change);
        return Task.FromResult(view);
    }

    public Task<SettingsNamespaceView> ReplaceAsync(string ns, JsonElement section, long? expectedRevision = null,
                                                    CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (view, change) = Write(ns, expectedRevision, state => state with { User = section });
        DocumentUpdated?.Invoke(this, change);
        return Task.FromResult(view);
    }

    public Task<SettingsNamespaceView> MutateAsync(
        string            ns, IReadOnlyList<SettingsMutationOp> ops, long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (view, change) = Write(ns, expectedRevision, state =>
        {
            var user = state.User;
            foreach (var op in ops)
                switch (op.Op)
                {
                    case SettingsMutationOp.SetOp :
                    {
                        if (op.Value is not { } value)
                            throw new InvalidOperationException("set 操作缺少 value。");
                        user = SetPath(user, op.Path, value);
                        break;
                    }
                    case SettingsMutationOp.UnsetOp :
                        user = UnsetPath(user, op.Path);
                        break;
                    default :
                        throw new InvalidOperationException($"未知路径操作：{op.Op}");
                }

            return state with { User = user };
        });
        DocumentUpdated?.Invoke(this, change);
        return Task.FromResult(view);
    }

    public Task OpenSettingsDocumentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private (SettingsNamespaceView View, SettingsDocumentUpdate Change) Write(
        string ns, long? expectedRevision, Func<NamespaceState, NamespaceState> apply)
    {
        lock (_syncRoot)
        {
            var index = _namespaces.FindIndex(candidate => candidate.Ns == ns);
            if (index < 0)
                throw new InvalidOperationException($"设置命名空间不存在：{ns}");

            var state = _namespaces[index];
            if (expectedRevision is { } expected && expected != state.Revision)
                throw new SettingsConflictException(ns, expected, state.Revision);

            state              = apply(state) with { Revision = state.Revision + 1 };
            _namespaces[index] = state;
            return (ToView(state), new SettingsDocumentUpdate(ns, state.Revision));
        }
    }

    private SettingsNamespaceView ToView(NamespaceState state)
    {
        var value = DeepMerge(state.Base, state.User);
        return new SettingsNamespaceView(state.Ns, false, state.Schema, value,
                                         "live", state.Revision, state.Base, state.User,
                                         CollectSecrets(state.Secrets, value));
    }

    /// <summary>
    ///     凭据槽位：种子槽位 ∪ 生效值中的 apiKeyEnv 叶子。真实后端按 schema 的
    ///     credential-ref 路径派生并与凭据域联查；模拟环境以「引用名出现在值中即视为槽位已设置」
    ///     近似（同路径种子槽位优先，保留其显式 set 状态）。
    /// </summary>
    private static IReadOnlyList<SettingsSecretInfo> CollectSecrets(
        IReadOnlyList<SettingsSecretInfo> seeded, JsonElement value)
    {
        List<SettingsSecretInfo> slots = [.. seeded];
        CollectApiKeyEnvPaths(value, [], slots);
        return slots;
    }

    private static void CollectApiKeyEnvPaths(
        JsonElement node, List<string> path, List<SettingsSecretInfo> slots)
    {
        if (node.ValueKind != JsonValueKind.Object) return;

        foreach (var property in node.EnumerateObject())
        {
            path.Add(property.Name);
            if (property is { Name: "apiKeyEnv", Value.ValueKind: JsonValueKind.String })
            {
                var slot = new SettingsSecretInfo([.. path], true);
                if (!slots.Any(existing => existing.Path.SequenceEqual(slot.Path))) slots.Add(slot);
            }
            else
            {
                CollectApiKeyEnvPaths(property.Value, path, slots);
            }

            path.RemoveAt(path.Count - 1);
        }
    }

    private static List<NamespaceState> BuildSeed()
    {
        return
        [
            new NamespaceState("locale", Json("""{"type":"string","choices":["en","zh"],"default":"en"}"""),
                               Json("""{"preference":"en"}"""), EmptyObject(), 0, []),
            new NamespaceState("ui-theme",
                               Json("""{"type":"string","choices":["light","dark","system"],"default":"dark"}"""),
                               Json("""{"preference":"dark","fontSize":14}"""), EmptyObject(), 0, []),
            new NamespaceState("ui-chat", Json("""{"type":"object"}"""),
                               Json("""{"transcriptView":"standard","performanceUsage":"compact","linkOpening":"new-tab"}"""),
                               EmptyObject(), 0, []),
            new NamespaceState("ui-conversation", Json("""{"type":"object"}"""), Json("""{"busyEnter":"queue"}"""),
                               EmptyObject(), 0, []),
            new NamespaceState("ui-settings", Json("""{"type":"object"}"""), Json("""{"enabled":false}"""),
                               EmptyObject(), 0, []),
            new NamespaceState("permission",
                               Json("""{"type":"string","choices":["default","full-access"],"default":"default"}"""),
                               Json("""{"defaultPreset":"default"}"""), EmptyObject(), 0, []),
            new NamespaceState("llm-deepseek", Json("""{"type":"object"}"""),
                               Json("""{"baseURL":"https://api.deepseek.com","models":[{"id":"deepseek-flash","name":"DeepSeek-V41-Flash","contextWindow":1000000,"inputModalities":["text","image"],"systemPromptUpdate":"in-history","toolUpdate":"addition-only"},{"id":"deepseek-v4-pro","name":"DeepSeek-V4-Pro","description":"Stronger agentic coding, knowledge, and difficult reasoning; suited to complex or quality-critical tasks at higher cost.","contextWindow":1000000}]}"""),
                               EmptyObject(), 0, [new SettingsSecretInfo(["apiKeyEnv"], true)]),
            // 账号登录路由：值恒空（凭据由登录态承载），行可见性由 modelCatalog 的
            // deepseek-account 组驱动，无 apiKeyEnv 槽位故不画圆点。
            new NamespaceState("llm-deepseek-account", Json("""{"type":"object"}"""), Json("""{}"""), EmptyObject(), 0,
                               []),
            // llm-pi-ai 插件命名空间：providers 为声明式路由表；glm 对齐真实 profile patch
            // 的 declared 路由形态（自声明 api/baseURL/models，密钥引用走凭据域）。
            new NamespaceState("llm-pi-ai", Json("""{"type":"object"}"""),
                               Json("""{"providers":{"glm":{"displayName":"GLM","api":"openai-completions","baseURL":"https://relay.example.com/v1","apiKeyEnv":"GLM_API_KEY","models":[{"id":"glm-4.7","name":"GLM-4.7","contextWindow":200000,"input":["text","image"]},{"id":"glm-4.7-air","name":"GLM-4.7-Air","contextWindow":128000},{"id":"glm-4.7-flash","name":"GLM-4.7-Flash","contextWindow":128000}]}}}"""),
                               EmptyObject(), 0, [new SettingsSecretInfo(["providers", "glm", "apiKeyEnv"], true)]),
            new NamespaceState("pwsh-sandbox", Json("""{"type":"object"}"""),
                               Json("""{"timeoutMs":120000,"maxOutputBytes":64000}"""), EmptyObject(), 0, []),
            new NamespaceState("agent-loop", Json("""{"type":"object"}"""), Json("""{"maxParallelToolCalls":10}"""),
                               EmptyObject(), 0, []),
            new NamespaceState("subagent", Json("""{"type":"object"}"""),
                               Json("""{"maxDepth":1,"maxActiveSubagents":8}"""), EmptyObject(), 0, []),
            new NamespaceState("web-search-deepseek", Json("""{"type":"object"}"""),
                               Json("""{"apiKeyEnv":"DEEPSEEK_API_KEY","baseURL":"https://api.deepseek.com","maxUses":5}"""),
                               EmptyObject(), 0, [new SettingsSecretInfo(["apiKeyEnv"], false)]),
            new NamespaceState("session-log-deepseek", Json("""{"type":"object"}"""),
                               Json("""{"enabled":true,"maxBytes":8388608}"""), EmptyObject(), 0, [])
        ];
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static JsonElement EmptyObject()
    {
        return Json("{}");
    }

    /// <summary>深合并：patch 同名键递归覆盖，其余键保留；返回独立于原文档的新元素。</summary>
    private static JsonElement DeepMerge(JsonElement current, JsonElement patch)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteMerged(writer, current, patch);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteMerged(Utf8JsonWriter writer, JsonElement current, JsonElement patch)
    {
        if (current.ValueKind != JsonValueKind.Object || patch.ValueKind != JsonValueKind.Object)
        {
            patch.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var property in current.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (patch.TryGetProperty(property.Name, out var patchValue))
                WriteMerged(writer, property.Value, patchValue);
            else
                property.Value.WriteTo(writer);
        }

        foreach (var property in patch.EnumerateObject())
            if (!current.TryGetProperty(property.Name, out _))
            {
                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

        writer.WriteEndObject();
    }

    /// <summary>在用户段按路径赋值；缺失的中间层级按对象补齐。</summary>
    private static JsonElement SetPath(JsonElement segment, IReadOnlyList<string> path, JsonElement value)
    {
        if (path.Count == 0)
            return value;

        var rest   = path.Skip(1).ToArray();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var exists = false;
            if (segment.ValueKind == JsonValueKind.Object)
                foreach (var property in segment.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.NameEquals(path[0]))
                    {
                        exists = true;
                        SetPath(property.Value, rest, value).WriteTo(writer);
                    }
                    else
                    {
                        property.Value.WriteTo(writer);
                    }
                }

            if (!exists)
            {
                writer.WritePropertyName(path[0]);
                SetPath(default, rest, value).WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>按路径移除用户段字段；路径缺失时按原样保留。</summary>
    private static JsonElement UnsetPath(JsonElement segment, IReadOnlyList<string> path)
    {
        if (path.Count == 0 || segment.ValueKind != JsonValueKind.Object)
            return segment;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in segment.EnumerateObject()
                                            .Where(property => !property.NameEquals(path[0]) || path.Count != 1))
            {
                writer.WritePropertyName(property.Name);
                if (property.NameEquals(path[0]))
                    UnsetPath(property.Value, path.Skip(1).ToArray()).WriteTo(writer);
                else
                    property.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private sealed record NamespaceState(
        string                            Ns,
        JsonElement                       Schema,
        JsonElement                       Base,
        JsonElement                       User,
        long                              Revision,
        IReadOnlyList<SettingsSecretInfo> Secrets);
}
