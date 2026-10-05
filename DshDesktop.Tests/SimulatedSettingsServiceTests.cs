using DshDesktop.Core.Exceptions;
using DshDesktop.Core.Models;
using DshDesktop.Infrastructure.Services;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Text.Json;

namespace DshDesktop.Tests;

/// <summary>模拟设置/凭据服务的写语义验证（对齐后端 settings/credentials 域）。</summary>
public sealed class SimulatedSettingsServiceTests
{
    [Test]
    public async Task DescribeReturnsSeededNamespacesWithEffectiveValues()
    {
        var service = new SimulatedSettingsService();

        var describe = await service.DescribeAsync();

        ClassicAssert.IsTrue(describe.Writable);
        Assert.That(describe.Namespaces.Any(view => view.Ns    == "ui-theme"), Is.True);
        Assert.That(describe.Namespaces.Any(view => view.Ns    == "llm-deepseek"), Is.True);
        var theme = describe.Namespaces.Single(view => view.Ns == "ui-theme");
        ClassicAssert.AreEqual("dark", theme.Value.GetProperty("preference").GetString());
        ClassicAssert.AreEqual(14, theme.Value.GetProperty("fontSize").GetInt32());
    }

    [Test]
    public async Task UpdateMergesPatchIntoUserSegmentAndBumpsRevision()
    {
        var service = new SimulatedSettingsService();
        var before  = await service.DescribeAsync();
        var theme   = before.Namespaces.Single(view => view.Ns == "ui-theme");

        var after = await service.UpdateAsync("ui-theme", Json("""{"fontSize":16}"""), theme.Revision);

        ClassicAssert.AreEqual(theme.Revision + 1, after.Revision);
        ClassicAssert.AreEqual(16, after.Value.GetProperty("fontSize").GetInt32());
        // 基线字段保留：update 是深合并而不是整段替换。
        ClassicAssert.AreEqual("dark", after.Value.GetProperty("preference").GetString());
        ClassicAssert.AreEqual(16, after.User!.Value.GetProperty("fontSize").GetInt32());
    }

    [Test]
    public async Task UpdateWithStaleRevisionThrowsConflict()
    {
        var service = new SimulatedSettingsService();
        var before  = await service.DescribeAsync();
        var theme   = before.Namespaces.Single(view => view.Ns == "ui-theme");

        var exception =
            await Assert.ThrowsAsync<SettingsConflictException>(() => service.UpdateAsync("ui-theme",
                                                                         Json("""{"fontSize":16}"""),
                                                                         theme.Revision - 1));
        ClassicAssert.AreEqual("ui-theme", exception.Ns);
        ClassicAssert.AreEqual(theme.Revision - 1, exception.Expected);
        ClassicAssert.AreEqual(theme.Revision, exception.Actual);
    }

    [Test]
    public async Task UpdateWithoutExpectedRevisionAppliesUnconditionally()
    {
        var service = new SimulatedSettingsService();

        var after = await service.UpdateAsync("ui-theme", Json("""{"fontSize":16}"""));

        ClassicAssert.AreEqual(16, after.Value.GetProperty("fontSize").GetInt32());
    }

    [Test]
    public async Task ReplaceSwapsWholeUserSegment()
    {
        var service = new SimulatedSettingsService();
        await service.UpdateAsync("ui-theme", Json("""{"fontSize":16}"""));

        var after = await service.ReplaceAsync("ui-theme", Json("""{"preference":"system"}"""));

        ClassicAssert.AreEqual("system", after.Value.GetProperty("preference").GetString());
        // 用户段被整段替换（旧覆盖字段消失），生效值回落基线层。
        ClassicAssert.IsFalse(after.User!.Value.TryGetProperty("fontSize", out _));
        ClassicAssert.AreEqual(14, after.Value.GetProperty("fontSize").GetInt32());
    }

    [Test]
    public async Task MutateAppliesOpsInOrder()
    {
        var service = new SimulatedSettingsService();

        var after = await service.MutateAsync("subagent",
        [
            SettingsMutationOp.Set(["maxDepth"], Json("3")),
            SettingsMutationOp.Unset(["maxActiveSubagents"])
        ]);

        // ops 作用于用户段：覆盖字段写入、移除；基线层的同名字段继续在生效值中透出。
        ClassicAssert.AreEqual(3, after.User!.Value.GetProperty("maxDepth").GetInt32());
        ClassicAssert.IsFalse(after.User.Value.TryGetProperty("maxActiveSubagents", out _));
        ClassicAssert.AreEqual(3, after.Value.GetProperty("maxDepth").GetInt32());
        ClassicAssert.AreEqual(8, after.Value.GetProperty("maxActiveSubagents").GetInt32());
    }

    [Test]
    public async Task MutateSetCreatesNestedPath()
    {
        var service = new SimulatedSettingsService();

        var after = await service.MutateAsync("ui-chat",
                                              [SettingsMutationOp.Set(["composer", "spellcheck"], Json("true"))]);

        ClassicAssert.IsTrue(after.Value.GetProperty("composer").GetProperty("spellcheck").GetBoolean());
    }

    [Test]
    public async Task DocumentUpdatedRaisedOnWrites()
    {
        var service = new SimulatedSettingsService();
        var changes = new List<SettingsDocumentUpdate>();
        service.DocumentUpdated += (_, change) => changes.Add(change);

        await service.UpdateAsync("ui-theme", Json("""{"fontSize":16}"""));

        Assert.That(changes, Has.Count.EqualTo(1));
        ClassicAssert.AreEqual("ui-theme", changes[0].Ns);
    }

    [Test]
    public async Task UnknownNamespaceThrows()
    {
        var service = new SimulatedSettingsService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync("not-a-namespace",
                                                                     Json("""{"a":1}""")));
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}

/// <summary>模拟凭据服务的生命周期与拒绝语义验证。</summary>
public sealed class SimulatedCredentialsServiceTests
{
    [Test]
    public async Task SetThenDescribeReportsConfiguredAndUnsetClears()
    {
        var service = new SimulatedCredentialsService();

        await service.SetAsync("DEEPSEEK_API_KEY", "sk-test");
        var configured = await service.DescribeAsync(["DEEPSEEK_API_KEY"]);
        ClassicAssert.IsTrue(configured["DEEPSEEK_API_KEY"].Configured);
        ClassicAssert.IsTrue(configured["DEEPSEEK_API_KEY"].Writable);

        await service.UnsetAsync("DEEPSEEK_API_KEY");
        var cleared = await service.DescribeAsync(["DEEPSEEK_API_KEY"]);
        ClassicAssert.IsFalse(cleared["DEEPSEEK_API_KEY"].Configured);
    }

    [Test]
    public async Task DescribeReportsUnconfiguredReference()
    {
        var service = new SimulatedCredentialsService();

        var describe = await service.DescribeAsync(["MISSING_KEY"]);

        ClassicAssert.IsFalse(describe["MISSING_KEY"].Configured);
        ClassicAssert.IsTrue(describe["MISSING_KEY"].Writable);
    }

    [Test]
    public async Task SetRejectsEmptyValueAndInvalidReference()
    {
        var service = new SimulatedCredentialsService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetAsync("GLM_API_KEY", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetAsync("1-BAD", "value"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetAsync("HAS SPACE", "value"));
    }

    [Test]
    public async Task DescribeRejectsOversizedBatchAndInvalidSyntax()
    {
        var service = new SimulatedCredentialsService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.DescribeAsync(Enumerable.Range(0, 65)
                                                                                       .Select(_ => "REF").ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DescribeAsync(["BAD-NAME"]));
    }

    [Test]
    public async Task ReferenceUpdatedRaisedOnSetAndUnset()
    {
        var service = new SimulatedCredentialsService();
        var raised  = 0;
        service.ReferenceUpdated += (_, _) => raised++;

        await service.SetAsync("GLM_API_KEY", "value");
        await service.UnsetAsync("GLM_API_KEY");

        ClassicAssert.AreEqual(2, raised);
    }
}
