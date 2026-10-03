using DshDesktop.Core.Exceptions;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Services.Connection;
using DshDesktop.Harness.Services.Settings;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Text.Json;

namespace DshDesktop.Tests;

/// <summary>settings/credentials 域的信封形状、视图映射、错误映射与事件解析验证。</summary>
public sealed class SettingsProtocolJsonTests
{
    [Test]
    public void UpdateRequestFlattensToWireParameterNames()
    {
        var body = RpcEnvelope.BuildArgsRequest("rpc-1", "settings/update",
                                                new SettingsUpdateRequest("ui-theme", Json("""{"fontSize":16}"""), 3),
                                                HarnessJsonContext.Default.SettingsUpdateRequest);

        using var document = JsonDocument.Parse(body);
        var       args     = document.RootElement.GetProperty("payload").GetProperty("args");
        // args 按后端方法形参名组织；键名错误会被 gateway/bad-request 拒绝。
        ClassicAssert.AreEqual(new[] { "ns", "patch", "expectedRevision" },
                               args.EnumerateObject().Select(property => property.Name).ToArray());
        ClassicAssert.AreEqual("ui-theme", args.GetProperty("ns").GetString());
        ClassicAssert.AreEqual(16, args.GetProperty("patch").GetProperty("fontSize").GetInt32());
        ClassicAssert.AreEqual(3, args.GetProperty("expectedRevision").GetInt64());
    }

    [Test]
    public void UpdateRequestOmitsUnsetExpectedRevision()
    {
        // expectedRevision 传 undefined 才是无条件写；序列化后必须缺席而非 null 值。
        var body = RpcEnvelope.BuildArgsRequest("rpc-1", "settings/update",
                                                new SettingsUpdateRequest("ui-theme", Json("""{}""")),
                                                HarnessJsonContext.Default.SettingsUpdateRequest);

        using var document = JsonDocument.Parse(body);
        var       args     = document.RootElement.GetProperty("payload").GetProperty("args");
        ClassicAssert.IsFalse(args.TryGetProperty("expectedRevision", out _));
    }

    [Test]
    public void ReplaceAndMutateRequestsCarryWireParameterNames()
    {
        var replace = RpcEnvelope.BuildArgsRequest("rpc-1", "settings/replace",
                                                   new SettingsReplaceRequest("ui-theme",
                                                                              Json("""{"preference":"system"}""")),
                                                   HarnessJsonContext.Default.SettingsReplaceRequest);
        using var replaceDocument = JsonDocument.Parse(replace);
        var       replaceArgs     = replaceDocument.RootElement.GetProperty("payload").GetProperty("args");
        ClassicAssert.AreEqual(new[] { "ns", "section" },
                               replaceArgs.EnumerateObject().Select(property => property.Name).ToArray());
        ClassicAssert.AreEqual("system", replaceArgs.GetProperty("section").GetProperty("preference").GetString());

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
        ClassicAssert.AreEqual(new[] { "ns", "ops" },
                               mutateArgs.EnumerateObject().Select(property => property.Name).ToArray());
        var set = mutateArgs.GetProperty("ops")[0];
        ClassicAssert.AreEqual("set", set.GetProperty("op").GetString());
        ClassicAssert.AreEqual("maxDepth", set.GetProperty("path")[0].GetString());
        ClassicAssert.AreEqual(3, set.GetProperty("value").GetInt32());
        var unset = mutateArgs.GetProperty("ops")[1];
        ClassicAssert.AreEqual("unset", unset.GetProperty("op").GetString());
        ClassicAssert.IsFalse(unset.TryGetProperty("value", out _));
    }

    [Test]
    public void CredentialsRequestsUseRefParameterName()
    {
        var set = RpcEnvelope.BuildArgsRequest("rpc-1", "credentials/set",
                                               new CredentialsSetRequest("DEEPSEEK_API_KEY", "sk-test"),
                                               HarnessJsonContext.Default.CredentialsSetRequest);
        using var setDocument = JsonDocument.Parse(set);
        var       setArgs     = setDocument.RootElement.GetProperty("payload").GetProperty("args");
        ClassicAssert.AreEqual(new[] { "ref", "value" },
                               setArgs.EnumerateObject().Select(property => property.Name).ToArray());
        ClassicAssert.AreEqual("DEEPSEEK_API_KEY", setArgs.GetProperty("ref").GetString());

        var unset = RpcEnvelope.BuildArgsRequest("rpc-2", "credentials/unset",
                                                 new CredentialsUnsetRequest("DEEPSEEK_API_KEY"),
                                                 HarnessJsonContext.Default.CredentialsUnsetRequest);
        using var unsetDocument = JsonDocument.Parse(unset);
        var       unsetArgs     = unsetDocument.RootElement.GetProperty("payload").GetProperty("args");
        ClassicAssert.AreEqual(new[] { "ref" },
                               unsetArgs.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Test]
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
        ClassicAssert.IsTrue(describe.Writable);
        ClassicAssert.AreEqual(3, describe.Namespaces.Count);

        var theme = describe.Namespaces[0];
        ClassicAssert.AreEqual("dark", theme.Value.GetProperty("preference").GetString());
        ClassicAssert.AreEqual(16, theme.User!.Value.GetProperty("fontSize").GetInt32());
        ClassicAssert.AreEqual("dark", theme.Base!.Value.GetProperty("preference").GetString());

        var llm = describe.Namespaces[1];
        // Secrets 在 Core 模型上可空，先取局部变量并以 IsNotNull 让流分析认可非空。
        var llmSecrets = llm.Secrets;
        ClassicAssert.IsNotNull(llmSecrets);
        ClassicAssert.AreEqual(new[] { "apiKeyEnv" }, llmSecrets[0].Path);
        ClassicAssert.IsTrue(llmSecrets[0].Set);

        // 条目完全不含 secrets 键时，Core 映射须回退为空集合而非 null。
        var locale = describe.Namespaces[2];
        ClassicAssert.IsNotNull(locale.Secrets);
        ClassicAssert.IsEmpty(locale.Secrets);
    }

    [Test]
    public void ConflictExceptionCarriesDetails()
    {
        var exception = new HarnessRpcException("settings/conflict", "settings/update 失败：stale write",
                                                Json("""{"ns":"ui-theme","expected":3,"actual":7}"""));

        ClassicAssert.IsTrue(SettingsRpcErrors.TryMap(exception, out var mapped));
        Assert.That(mapped, Is.TypeOf<SettingsConflictException>());
        var conflict = (SettingsConflictException)mapped;
        ClassicAssert.AreEqual("ui-theme", conflict.Ns);
        ClassicAssert.AreEqual(3, conflict.Expected);
        ClassicAssert.AreEqual(7, conflict.Actual);
    }

    [Test]
    public void RejectedMapsNamespaceAndRawMessage()
    {
        var exception = new HarnessRpcException("settings/rejected", "settings/mutate 失败：schema 校验失败",
                                                Json("""{"ns":"ui-theme"}"""), "schema 校验失败");

        ClassicAssert.IsTrue(SettingsRpcErrors.TryMap(exception, out var mapped));
        Assert.That(mapped, Is.TypeOf<SettingsRejectedException>());
        var rejected = (SettingsRejectedException)mapped;
        ClassicAssert.AreEqual("ui-theme", rejected.Ns);
        ClassicAssert.AreEqual("schema 校验失败", rejected.Message);
    }

    [Test]
    public void CredentialRejectedMapsReferenceAndRawMessage()
    {
        var exception = new HarnessRpcException("credential/rejected", "credentials/set 失败：read-only source",
                                                Json("""{"ref":"GLM_API_KEY"}"""),
                                                "read-only source shadows GLM_API_KEY");

        ClassicAssert.IsTrue(SettingsRpcErrors.TryMap(exception, out var mapped));
        Assert.That(mapped, Is.TypeOf<CredentialRejectedException>());
        var rejected = (CredentialRejectedException)mapped;
        ClassicAssert.AreEqual("GLM_API_KEY", rejected.Reference);
        ClassicAssert.AreEqual("read-only source shadows GLM_API_KEY", rejected.Message);
    }

    [Test]
    public void UnknownErrorCodePassesThrough()
    {
        var exception = new HarnessRpcException("gateway/internal", "provider 未挂载。");
        ClassicAssert.IsFalse(SettingsRpcErrors.TryMap(exception, out _));
    }

    [Test]
    public void SettingsDocumentUpdatedEmitParsesNotice()
    {
        var emit =
            (RemoteEventFrame.Emit)
            RemoteEventJson.Parse(Json("""{"type":"emit","event":"settings/document-updated","args":["ui-theme",3]}"""))
            !;

        ClassicAssert.AreEqual(RemoteEventJson.SettingsDocumentUpdatedEvent, emit.Event);
        ClassicAssert.IsTrue(RemoteEventJson.TryGetSettingsDocumentUpdate(emit, out var notice));
        ClassicAssert.AreEqual("ui-theme", notice!.Ns);
        ClassicAssert.AreEqual(3, notice.Revision);
    }

    [Test]
    public void CredentialReferenceUpdatedEmitParsesReference()
    {
        var emit =
            (RemoteEventFrame.Emit)
            RemoteEventJson
               .Parse(Json("""{"type":"emit","event":"credentials/reference-updated","args":["GLM_API_KEY"]}"""))!;

        ClassicAssert.IsTrue(RemoteEventJson.TryGetCredentialReference(emit, out var reference));
        ClassicAssert.AreEqual("GLM_API_KEY", reference);
    }

    [Test]
    public void SettingsEventsWithWrongArityYieldNoNotice()
    {
        var missingRevision =
            (RemoteEventFrame.Emit)
            RemoteEventJson.Parse(Json("""{"type":"emit","event":"settings/document-updated","args":["ui-theme"]}"""))!;
        ClassicAssert.IsFalse(RemoteEventJson.TryGetSettingsDocumentUpdate(missingRevision, out _));

        var emptyReference =
            (RemoteEventFrame.Emit)
            RemoteEventJson.Parse(Json("""{"type":"emit","event":"credentials/reference-updated","args":[]}"""))!;
        ClassicAssert.IsFalse(RemoteEventJson.TryGetCredentialReference(emptyReference, out _));
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
