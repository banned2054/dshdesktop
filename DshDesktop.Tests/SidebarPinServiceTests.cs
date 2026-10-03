using DshDesktop.Infrastructure.Services;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace DshDesktop.Tests;

/// <summary>
///     本地置顶注册表的落盘状态恢复：置顶集合写入配置文件后由新实例重读恢复；
///     文件缺失与损坏按空集合起底（不阻塞启动）。数据本身持久化在磁盘，
///     本地不维护第二套权威状态。
/// </summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class SidebarPinServiceTests : IDisposable
{
    private readonly string _filePath =
        Path.Combine(Path.GetTempPath(), $"dsh-pin-test-{Guid.NewGuid():N}", "sidebar-pins.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_filePath)!, true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结果。
        }
    }

    [Test]
    public async Task PinsPersistAcrossInstancesAndKeepInsertionOrder()
    {
        var first = new SidebarPinService(_filePath);
        await first.PinSessionAsync("session-b");
        await first.PinSessionAsync("session-a");
        await first.PinWorkspaceAsync("workspace-1");
        await first.UnpinSessionAsync("session-b");

        // 新实例从配置文件恢复：会话集合前插（最近置顶在前），取消置顶已落盘。
        var second = new SidebarPinService(_filePath);
        ClassicAssert.AreEqual(new[] { "session-a" }, second.PinnedSessionIds);
        ClassicAssert.AreEqual(new[] { "workspace-1" }, second.PinnedWorkspaceIds);
    }

    [Test]
    public async Task ConcurrentInstances_MergeChangesInsteadOfOverwriting()
    {
        var first  = new SidebarPinService(_filePath);
        var second = new SidebarPinService(_filePath);

        // 两个实例从同一旧状态分别变更会话／工作区置顶：读-改-写以磁盘最新状态为
        // 合并基准，后写的实例不得整体覆盖先写实例的记录。
        await first.PinSessionAsync("session-a");
        await second.PinWorkspaceAsync("workspace-b");

        var reloaded = new SidebarPinService(_filePath);
        ClassicAssert.AreEqual(new[] { "session-a" }, reloaded.PinnedSessionIds);
        ClassicAssert.AreEqual(new[] { "workspace-b" }, reloaded.PinnedWorkspaceIds);

        // 后续变更仍保留另一实例的记录：实例一取消会话置顶不影响工作区置顶。
        await first.UnpinSessionAsync("session-a");

        reloaded = new SidebarPinService(_filePath);
        ClassicAssert.IsEmpty(reloaded.PinnedSessionIds);
        ClassicAssert.AreEqual(new[] { "workspace-b" }, reloaded.PinnedWorkspaceIds);
    }

    [Test]
    public async Task SameInstanceConcurrentPins_BothPersisted()
    {
        var service = new SidebarPinService(_filePath);

        // 同实例并发写入在进程内串行化：两组变更都生效且都落盘。
        await Task.WhenAll(service.PinSessionAsync("session-a"), service.PinWorkspaceAsync("workspace-b"));

        ClassicAssert.AreEqual(new[] { "session-a" }, service.PinnedSessionIds);
        ClassicAssert.AreEqual(new[] { "workspace-b" }, service.PinnedWorkspaceIds);
        var reloaded = new SidebarPinService(_filePath);
        ClassicAssert.AreEqual(new[] { "session-a" }, reloaded.PinnedSessionIds);
        ClassicAssert.AreEqual(new[] { "workspace-b" }, reloaded.PinnedWorkspaceIds);
    }

    [Test]
    public async Task PersistFailure_LeavesMemoryUnchangedAndThrows()
    {
        // 路径中一段被同名文件占用：目录创建失败使落盘必然失败。
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "occupied"), "not a directory");
        var service = new SidebarPinService(Path.Combine(directory, "occupied", "sidebar-pins.json"));

        Assert.That(() => service.PinSessionAsync("session-a"), Throws.InstanceOf<IOException>());

        // 写入失败不提交内存：集合保持原状，不产生半套状态。
        ClassicAssert.IsEmpty(service.PinnedSessionIds);
        ClassicAssert.IsEmpty(service.PinnedWorkspaceIds);
    }

    [Test]
    public async Task CancelledPin_LeavesMemoryUnchanged()
    {
        var service = new SidebarPinService(_filePath);
        await service.PinSessionAsync("session-a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(() => service.PinSessionAsync("session-b", cts.Token),
                    Throws.InstanceOf<OperationCanceledException>());

        // 取消的变更不改变内存集合；后续变更正常执行。
        ClassicAssert.AreEqual(new[] { "session-a" }, service.PinnedSessionIds);
        ClassicAssert.IsEmpty(service.PinnedWorkspaceIds);
        await service.PinWorkspaceAsync("workspace-c");
        ClassicAssert.AreEqual(new[] { "workspace-c" }, service.PinnedWorkspaceIds);
    }

    [Test]
    public async Task ProcessLockTimeout_ThrowsKeepsMemoryUnchangedAndRecoversAfterRelease()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        // 同进程以 FileShare.None 占住 .lock 文件，模拟另一实例持锁挂死。
        await using (new FileStream(_filePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = new SidebarPinService(_filePath, TimeSpan.FromMilliseconds(300));
            Assert.That(() => blocked.PinSessionAsync("session-a"), Throws.InstanceOf<IOException>());

            // 超时不提交内存：集合保持原状。
            ClassicAssert.IsEmpty(blocked.PinnedSessionIds);
            ClassicAssert.IsEmpty(blocked.PinnedWorkspaceIds);
        }

        // 持锁方释放后恢复正常。
        var recovered = new SidebarPinService(_filePath, TimeSpan.FromMilliseconds(300));
        await recovered.PinSessionAsync("session-a");
        ClassicAssert.AreEqual(new[] { "session-a" }, recovered.PinnedSessionIds);
    }

    [Test]
    public async Task RepeatedPinIsIdempotentAndDoesNotDuplicate()
    {
        var service = new SidebarPinService(_filePath);
        await service.PinWorkspaceAsync("workspace-1");
        await service.PinWorkspaceAsync("workspace-1");

        ClassicAssert.AreEqual(new[] { "workspace-1" }, service.PinnedWorkspaceIds);
        ClassicAssert.AreEqual(new[] { "workspace-1" }, new SidebarPinService(_filePath).PinnedWorkspaceIds);
    }

    [Test]
    public void MissingOrCorruptedFileStartsEmpty()
    {
        ClassicAssert.IsEmpty(new SidebarPinService(_filePath).PinnedSessionIds);

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, "{ not valid json");
        var corrupted = new SidebarPinService(_filePath);
        ClassicAssert.IsEmpty(corrupted.PinnedSessionIds);
        ClassicAssert.IsEmpty(corrupted.PinnedWorkspaceIds);
    }
}
