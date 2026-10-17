using System;
using System.Linq;

[TestClass]
internal sealed class ResourceAlertListTests
{
    static ResourceAlert Alert(int pid, long started = 1, ResourceKind kind = ResourceKind.Cpu) =>
        new(new(new(pid, started), $"process{pid}", 90, 1024, null, null), kind, DateTimeOffset.UtcNow, 20);

    [TestMethod]
    public void Arrivals_AppendWithoutMovingExistingRows()
    {
        var first = Alert(1);
        var second = Alert(2);
        var result = ResourceAlertList.Add([first], second, out bool overflow);
        Assert.IsFalse(overflow);
        CollectionAssert.AreEqual(new[] { first, second }, result.ToArray());
    }

    [TestMethod]
    public void Reminder_UpdatesTheSameSlotAndCombinesKinds()
    {
        var first = Alert(1);
        var second = Alert(2);
        var reminder = Alert(1, kind: ResourceKind.Ram);
        var result = ResourceAlertList.Add([first, second], reminder, out bool overflow);
        Assert.IsFalse(overflow);
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(first.Usage.Key, result[0].Usage.Key);
        Assert.AreEqual(ResourceKind.Cpu | ResourceKind.Ram, result[0].Kinds);
        Assert.AreEqual(reminder.At, result[0].At);
        Assert.AreEqual(second, result[1]);
    }

    [TestMethod]
    public void FullList_DoesNotEvictOrMoveAnExistingClickTarget()
    {
        var alerts = Enumerable.Range(1, ResourceAlertList.Limit).Select(pid => Alert(pid)).ToArray();
        var result = ResourceAlertList.Add(alerts, Alert(101), out bool overflow);
        Assert.IsTrue(overflow);
        Assert.AreSame(alerts, result);
        var updated = ResourceAlertList.Add(result, Alert(50, kind: ResourceKind.Ram), out overflow);
        Assert.IsFalse(overflow);
        Assert.AreEqual(50, updated[49].Usage.Key.Pid);
        Assert.AreEqual(ResourceKind.Cpu | ResourceKind.Ram, updated[49].Kinds);
    }

    [TestMethod]
    public void Selection_IsASnapshotAndDoesNotIncludeLaterArrivalsOrReusedPids()
    {
        var first = Alert(1);
        var second = Alert(2);
        var keys = new[] { first.Usage.Key, second.Usage.Key };
        var third = Alert(3);
        CollectionAssert.AreEqual(new[] { first, second },
            ResourceAlertList.Selected([first, second, third], keys));
        CollectionAssert.AreEqual(new[] { second },
            ResourceAlertList.Selected([Alert(1, started: 2), second, third], keys));
    }

    [TestMethod]
    public void WindowHeight_GrowsWithAlertsAndIsBounded()
    {
        Assert.AreEqual(MonitoringLayout.Height, MonitoringLayout.PreferredHeight(0));
        Assert.IsTrue(MonitoringLayout.PreferredHeight(1) > MonitoringLayout.PreferredHeight(0));
        Assert.IsTrue(MonitoringLayout.PreferredHeight(2) > MonitoringLayout.PreferredHeight(1));
        Assert.AreEqual(MonitoringLayout.PreferredHeight(3), MonitoringLayout.PreferredHeight(int.MaxValue));
    }

    [TestMethod]
    public void BulkIgnore_NormalizesNamesAndReplacesOnSightRules()
    {
        var alert = Alert(1);
        alert = alert with { Usage = alert.Usage with { Name = "PROCESS1.EXE" } };
        var options = ResourceAlertList.IgnoreOptions(new(OnSightNames: ["process1", "other"]), [alert, Alert(1)], out string? error);
        Assert.IsNull(error);
        Assert.IsNotNull(options);
        CollectionAssert.AreEqual(new[] { "process1" }, options.IgnoredNames);
        CollectionAssert.AreEqual(new[] { "other" }, options.OnSightNames);
    }

    [TestMethod]
    public void BulkIgnore_RejectsTheWholeBatchWhenRulesWouldOverflow()
    {
        var original = new ResourceOptions(IgnoredNames: [.. Enumerable.Range(1, 499).Select(i => $"ignored{i}")]);
        Assert.IsNull(ResourceAlertList.IgnoreOptions(original, [Alert(1), Alert(2)], out string? error));
        Assert.IsNotNull(error);
        Assert.AreEqual(499, original.IgnoredNames.Length);
    }

    [TestMethod]
    public void Coverage_ReportsOnlyEnabledAndAvailableChecks()
    {
        var options = new ResourceOptions(CpuEnabled: false, RamEnabled: false, DiskEnabled: false,
            NetworkEnabled: false, OnSightNames: ["example"]);
        Assert.AreEqual("Name rules", ResourceText.Coverage(options, false));
        Assert.AreEqual("No alert checks enabled", ResourceText.Coverage(options with { OnSightNames = [] }, false));
        Assert.AreEqual("CPU / RAM · I/O permission needed", ResourceText.Coverage(new(), false));
        Assert.AreEqual("CPU / RAM / Disk / Network", ResourceText.Coverage(new(), true));
    }
}
