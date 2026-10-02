using DshDesktop.Core.Exceptions;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Services.Connection;
using DshDesktop.Harness.Services.Settings;
using System.Text.Json;
using Xunit;

namespace DshDesktop.Tests;

/// <summary>settings/credentials 域的信封形状、视图映射、错误映射与事件解析验证。</summary>
public sealed class SettingsProtocolJsonTests
{
    [Fact]
    public void UpdateRequestFlattensToWireParameterNames()
    {
        var body = RpcEnvelope.BuildArgsRequest("rpc-1", "settings/update",
                                                new SettingsUpdateRequest("ui-theme", Json("""{"fontSize":16}"""), 3),
                                                HarnessJsonContext.Default.SettingsUpdateRequest);

        using var document = JsonDocument.Parse(body);
        var       args     = document.RootElement.GetProperty("payload").GetProperty("args");
        // args 按后端方法形参名组织；键名错误会被 gateway/bad-request 拒绝。
        Assert.Equal(["ns", "patch", "expectedRevision"],
                     args.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("ui-theme", args.GetProperty("ns").GetString());
        Assert.Equal(16, args.GetProperty("patch").GetProperty("fontSize").GetInt32());
        Assert.Equal(3, args.GetProperty("expectedRevision").GetInt64());
    }

    [Fact]
    public void UpdateRequestOmitsUnsetExpectedRevision()
    {
        // expectedRevision 传 undefined 才是无条件写；序列化后必须缺席而非 null 值。
        var body = RpcEnvelope.BuildArgsRequest("rpc-1", "settings/update",
                                                new SettingsUpdateRequest("ui-theme", Json("""{}"""), null),
                                                HarnessJsonContext.Default.SettingsUpdateRequest);

        using var document = JsonDocument.Parse(body);
        var       args     = document.RootElement.GetProperty("payload").GetProperty("args");
        Assert.False(args.TryGetProperty("expectedRevision", out _));
    }

    [Fact]
    public void ReplaceAndMutateRequestsCarryWireParameterNames()
    {
        var replace = RpcEnvelope.BuildArgsRequest("rpc-1", "settings/replace",
                                                   new SettingsReplaceRequest("ui-theme",
                                                                              Json("""{"preference":"system"}""")),
                                                   HarnessJsonContext.Default.SettingsReplaceRequest);
        using var replaceDocument = JsonDocument.Parse(replace);
        var       replaceArgs     = replaceDocument.RootElement.GetProperty("payload").GetProperty("args");
        Assert.Equal(["ns", "section"],
                     replaceArgs.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("system", replaceArgs.GetProperty("section").GetProperty("preference").GetString());

        var mutate = RpcEnvelope.BuildArgsRequest("rpc-2", "settings/mutate",
                                                  new SettingsMutateRequest("subagent",
                                                  [
                                                      new SettingsOpRequest("set",
                                                                            ["maxDepth"], Json("3")),
                                                      new SettingsOpRequest("unset",
                                                                            ["maxActiveSubagents"])
                                                  ]),
                                                  HarnessJsonContext.Default.SettingsMutateRequest);
        using var mutateDocument = JsonDocument.Parse(mutate);
        var       mutateArgs     = mutateDocument.RootElement.GetProperty("payload").GetProperty("args");
        Assert.Equal(["ns", "ops"], mutateArgs.EnumerateObject().Select(property => property.Name).ToArray());
        var set = mutateArgs.GetProperty("ops")[0];
        Assert.Equal("set", set.GetProperty("op").GetString());
        Assert.Equal("maxDepth", set.GetProperty("path")[0].GetString());
        Assert.Equal(3, set.GetProperty("value").GetInt32());
        var unset = mutateArgs.GetProperty("ops")[1];
        Assert.Equal("unset", unset.GetProperty("op").GetString());
        Assert.False(unset.TryGetProperty("value", out _));
    }

    [Fact]
    public void CredentialsRequestsUseRefParameterName()
    {
        var set = RpcEnvelope.BuildArgsRequest("rpc-1", "credentials/set",
                                               new CredentialsSetRequest("DEEPSEEK_API_KEY", "sk-test"),
                                               HarnessJsonContext.Default.CredentialsSetRequest);
        using var setDocument = JsonDocument.Parse(set);
        var       setArgs     = setDocument.RootElement.GetProperty("payload").GetProperty("args");
        Assert.Equal(["ref", "value"], setArgs.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("DEEPSEEK_API_KEY", setArgs.GetProperty("ref").GetString());

        var unset = RpcEnvelope.BuildArgsRequest("rpc-2", "credentials/unset",
                                                 new CredentialsUnsetRequest("DEEPSEEK_API_KEY"),
                                                 HarnessJsonContext.Default.CredentialsUnsetRequest);
        using var unsetDocument = JsonDocument.Parse(unset);
        var       unsetArgs     = unsetDocument.RootElement.GetProperty("payload").GetProperty("args");
        Assert.Equal(["ref"], unsetArgs.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void DescribeWireMapsToCoreModelWithSecrets()
    {
        var wire = JsonSerializer.Deserialize("""
                                              {"writable":true,"hasDocument":true,"namespaces":[
                                                {"ns":"ui-theme","autoGenerate":false,"schema":{"type":"string"},
                                                 "value":{"preference":"dark","fontSize":14},"base":{"preference":"dark"},
                                                 "user":{"fontSize":16},"applies":"live","revision":2,"secrets":[]},
                                                {"ns":"llm-deepseek","autoGenerate":true,"schema":{"type":"object"},"value":{},
                                                 "applies":"live","revision":0,"secrets":[{"path":["apiKeyEnv"],"set":true}]},
                                                {"ns":"locale","autoGenerate":false,"schema":{"type":"string"},
                                                 "value":{"preference":"zh"},"applies":"live","revision":1}
                                              ]}
                                              """,
                                              HarnessJsonContext.Default.SettingsDescribeValueWire);

        var describe = HarnessSettingsService.ToDescribeValue(wire!);
        Assert.True(describe.Writable);
        Assert.Equal(3, describe.Namespaces.Count);

        var theme = describe.Namespaces[0];
        Assert.Equal("dark", theme.Value.GetProperty("preference").GetString());
        Assert.Equal(16, theme.User!.Value.GetProperty("fontSize").GetInt32());
        Assert.Equal("dark", theme.Base!.Value.GetProperty("preference").GetString());

        var llm = describe.Namespaces[1];
        // Secrets 在 Core 模型上可空，先取局部变量并以 Assert.NotNull 让流分析认可非空。
        var llmSecrets = llm.Secrets;
        Assert.NotNull(llmSecrets);
        Assert.Equal(["apiKeyEnv"], llmSecrets[0].Path);
        Assert.True(llmSecrets[0].Set);

        // 条目完全不含 secrets 键时，Core 映射须回退为空集合而非 null。
        var locale = describe.Namespaces[2];
        Assert.NotNull(locale.Secrets);
        Assert.Empty(locale.Secrets);
    }

    [Fact]
    public void ConflictExceptionCarriesDetails()
    {
        var exception = new HarnessRpcException("settings/conflict", "settings/update 失败：stale write",
                                                Json("""{"ns":"ui-theme","expected":3,"actual":7}"""));

        Assert.True(SettingsRpcErrors.TryMap(exception, out var mapped));
        var conflict = Assert.IsType<SettingsConflictException>(mapped);
        Assert.Equal("ui-theme", conflict.Ns);
        Assert.Equal(3, conflict.Expected);
        Assert.Equal(7, conflict.Actual);
    }

    [Fact]
    public void RejectedMapsNamespaceAndRawMessage()
    {
        var exception = new HarnessRpcException("settings/rejected", "settings/mutate 失败：schema 校验失败",
                                                Json("""{"ns":"ui-theme"}"""), "schema 校验失败");

        Assert.True(SettingsRpcErrors.TryMap(exception, out var mapped));
        var rejected = Assert.IsType<SettingsRejectedException>(mapped);
        Assert.Equal("ui-theme", rejected.Ns);
        Assert.Equal("schema 校验失败", rejected.Message);
    }

    [Fact]
    public void CredentialRejectedMapsReferenceAndRawMessage()
    {
        var exception = new HarnessRpcException("credential/rejected", "credentials/set 失败：read-only source",
                                                Json("""{"ref":"GLM_API_KEY"}"""),
                                                "read-only source shadows GLM_API_KEY");

        Assert.True(SettingsRpcErrors.TryMap(exception, out var mapped));
        var rejected = Assert.IsType<CredentialRejectedException>(mapped);
        Assert.Equal("GLM_API_KEY", rejected.Reference);
        Assert.Equal("read-only source shadows GLM_API_KEY", rejected.Message);
    }

    [Fact]
    public void UnknownErrorCodePassesThrough()
    {
        var exception = new HarnessRpcException("gateway/internal", "provider 未挂载。");
        Assert.False(SettingsRpcErrors.TryMap(exception, out _));
    }

    [Fact]
    public void SettingsDocumentUpdatedEmitParsesNotice()
    {
        var emit =
            (RemoteEventFrame.Emit)
            RemoteEventJson.Parse(Json("""{"type":"emit","event":"settings/document-updated","args":["ui-theme",3]}"""))
            !;

        Assert.Equal(RemoteEventJson.SettingsDocumentUpdatedEvent, emit.Event);
        Assert.True(RemoteEventJson.TryGetSettingsDocumentUpdate(emit, out var notice));
        Assert.Equal("ui-theme", notice!.Ns);
        Assert.Equal(3, notice.Revision);
    }

    [Fact]
    public void CredentialReferenceUpdatedEmitParsesReference()
    {
        var emit =
            (RemoteEventFrame.Emit)
            RemoteEventJson
               .Parse(Json("""{"type":"emit","event":"credentials/reference-updated","args":["GLM_API_KEY"]}"""))!;

        Assert.True(RemoteEventJson.TryGetCredentialReference(emit, out var reference));
        Assert.Equal("GLM_API_KEY", reference);
    }

    [Fact]
    public void SettingsEventsWithWrongArityYieldNoNotice()
    {
        var missingRevision =
            (RemoteEventFrame.Emit)
            RemoteEventJson.Parse(Json("""{"type":"emit","event":"settings/document-updated","args":["ui-theme"]}"""))!;
        Assert.False(RemoteEventJson.TryGetSettingsDocumentUpdate(missingRevision, out _));

        var emptyReference =
            (RemoteEventFrame.Emit)
            RemoteEventJson.Parse(Json("""{"type":"emit","event":"credentials/reference-updated","args":[]}"""))!;
        Assert.False(RemoteEventJson.TryGetCredentialReference(emptyReference, out _));
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
