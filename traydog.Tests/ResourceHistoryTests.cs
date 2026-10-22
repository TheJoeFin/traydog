using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

[TestClass]
internal sealed class ResourceHistoryTests
{
    static readonly ProcessKey First = new(123, 100);
    static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    static ResourceUsage Usage(double? cpu = 90, ProcessKey? key = null) =>
        new(key ?? First, "example", cpu, 1024, null, null);
    static ResourceAlert Alert(ProcessKey? key = null) => new(Usage(key: key), ResourceKind.Cpu, At, 20);

    [TestMethod]
    public void Checks_AppendLiveValuesAndKeepDetectionSnapshot()
    {
        var detected = Alert();
        var alerts = ResourceHistory.Update([detected], new Dictionary<ProcessKey, ResourceUsage> { [First] = Usage() },
            new HashSet<int> { First.Pid }, null, 10, At);
        alerts = ResourceHistory.Update(alerts, new Dictionary<ProcessKey, ResourceUsage> { [First] = Usage(10) },
            new HashSet<int> { First.Pid }, [First], 11, At.AddSeconds(1));
        Assert.AreEqual(2, alerts[0].History.Count);
        Assert.AreEqual(90, alerts[0].Usage.CpuPercent);
        Assert.AreEqual(10, alerts[0].Current!.CpuPercent);
        Assert.AreEqual("Down from 90% at detection", ResourceHistory.Trend(alerts[0], ResourceKind.Cpu));
        Assert.AreEqual(ProcessAvailability.Available, alerts[0].Availability);
    }

    [TestMethod]
    public void RollingHistory_IsBoundedAndDoesNotAddDuplicateTicks()
    {
        var alert = Alert();
        for (int i = 0; i < ResourceHistory.Limit + 10; i++)
            alert = ResourceHistory.Append(alert, new(i, At.AddSeconds(i), Usage(i % 100)));
        Assert.AreEqual(ResourceHistory.Limit, alert.History.Count);
        Assert.AreEqual(10, alert.History[0].AtSeconds);
        Assert.AreEqual(ResourceHistory.Limit + 9, alert.History[^1].AtSeconds);
        Assert.AreSame(alert, ResourceHistory.Append(alert, alert.History[^1]));
        Assert.AreEqual(90, alert.Usage.CpuPercent);
    }

    [TestMethod]
    public void Watch_ChangesOnlySelectedInstanceInPlaceAndRetainsHistory()
    {
        var first = ResourceHistory.Append(Alert(), new(1, At, Usage()));
        var reused = Alert(First with { StartedUtcTicks = 200 });
        var alerts = ResourceAlertList.Watch([first, reused], [First]);
        Assert.IsTrue(alerts[0].Watching);
        Assert.AreSame(first.History, alerts[0].History);
        Assert.AreEqual(first.Usage.Key, alerts[0].Usage.Key);
        Assert.IsFalse(alerts[1].Watching);
        CollectionAssert.AreEqual(new[] { First, reused.Usage.Key }, alerts.Select(a => a.Usage.Key).ToArray());
    }

    [TestMethod]
    public void Targets_IncludePendingAndWatchedAppsEvenAfterRecoveryButNotExitedApps()
    {
        var second = First with { Pid = 124 };
        var exited = First with { Pid = 125 };
        var alerts = new[] { Alert() with { Watching = true }, Alert(second),
            Alert(exited) with { Watching = true, Availability = ProcessAvailability.Exited } };
        CollectionAssert.AreEquivalent(new[] { First, second }, ResourceHistory.Targets([First], alerts));
        var recovered = ResourceHistory.Update(alerts, new Dictionary<ProcessKey, ResourceUsage> { [First] = Usage(1) },
            new HashSet<int> { First.Pid, second.Pid }, [First], 1, At);
        CollectionAssert.AreEquivalent(new[] { First, second }, ResourceHistory.Targets([], recovered));
        Assert.IsTrue(recovered[0].Watching);
    }

    [TestMethod]
    public void Unreadable_IsAGapNotAZeroAndRecoversOnTheNextCheck()
    {
        var alert = ResourceHistory.Append(Alert(), new(1, At, Usage()));
        var missing = ResourceHistory.Update([alert], new Dictionary<ProcessKey, ResourceUsage>(),
            new HashSet<int> { First.Pid }, [First], 2, At.AddSeconds(1));
        Assert.AreEqual(ProcessAvailability.Unavailable, missing[0].Availability);
        Assert.IsNull(missing[0].Current);
        Assert.AreEqual(2, missing[0].History.Count);
        var recovered = ResourceHistory.Update(missing, new Dictionary<ProcessKey, ResourceUsage> { [First] = Usage(5) },
            new HashSet<int> { First.Pid }, [First], 3, At.AddSeconds(2));
        Assert.AreEqual(ProcessAvailability.Available, recovered[0].Availability);
        Assert.AreEqual(5, recovered[0].Current!.CpuPercent);
        Assert.AreEqual(2, ResourceHistory.BarPath(recovered[0].History, ResourceKind.Cpu).Count(c => c == 'M'));
    }

    [TestMethod]
    public void Exited_RecordStaysButIsNotSampledAgainOrTransferredToReusedPid()
    {
        var alert = ResourceHistory.Append(Alert() with { Watching = true }, new(1, At, Usage()));
        var replacement = First with { StartedUtcTicks = 200 };
        var alerts = ResourceHistory.Update([alert],
            new Dictionary<ProcessKey, ResourceUsage> { [replacement] = Usage(2, replacement) },
            new HashSet<int> { First.Pid }, null, 2, At.AddSeconds(1));
        Assert.AreEqual(ProcessAvailability.Exited, alerts[0].Availability);
        Assert.AreEqual(90, alerts[0].Current!.CpuPercent);
        Assert.IsTrue(alerts[0].Watching);
        Assert.IsEmpty(ResourceHistory.Targets([], alerts));
        Assert.AreEqual(1, alerts[0].History.Count);
    }

    [TestMethod]
    public void TargetedCheck_DoesNotMarkAnUnrelatedNewAlertAsExited()
    {
        var other = First with { Pid = 456 };
        var alerts = ResourceHistory.Update([Alert(other)], new Dictionary<ProcessKey, ResourceUsage>(),
            new HashSet<int>(), [First], 2, At);
        Assert.AreEqual(ProcessAvailability.Available, alerts[0].Availability);
        Assert.IsEmpty(alerts[0].History);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ClosedProcess_StopsTrackingAndRetainsLastUsageWithoutResuming(bool watching)
    {
        var alert = ResourceHistory.Append(Alert() with { Watching = watching }, new(1, At, Usage(25)));
        var closed = ResourceHistory.Update([alert], new Dictionary<ProcessKey, ResourceUsage>(),
            new HashSet<int>(), [First], 2, At.AddSeconds(1))[0];
        Assert.IsTrue(closed.IsStopped);
        Assert.IsFalse(closed.CanWatch);
        Assert.AreEqual(watching, closed.Watching);
        Assert.AreSame(alert.History, closed.History);
        Assert.AreEqual(25, closed.Current!.CpuPercent);
        Assert.IsEmpty(ResourceHistory.Targets([], [closed]));
        Assert.AreEqual("Tracking stopped: process closed", ResourceText.Status(closed, true, new()));
        Assert.AreEqual("Tracking stopped: process closed", ResourceText.Status(closed, false, new()));
        Assert.AreSame(closed, ResourceAlertList.Watch([closed], [First])[0]);

        var subsequent = ResourceHistory.Update([closed],
            new Dictionary<ProcessKey, ResourceUsage> { [First] = Usage(90) },
            new HashSet<int> { First.Pid }, null, 3, At.AddSeconds(2));
        Assert.AreSame(closed, subsequent[0]);
        Assert.AreSame(closed, ResourceHistory.Break(subsequent, 4, At.AddSeconds(3))[0]);
    }

    [TestMethod]
    public void TrackingStatus_UnreadableOrPausedDoesNotClaimThatTheProcessClosed()
    {
        var unreadable = Alert() with { Availability = ProcessAvailability.Unavailable };
        Assert.IsFalse(unreadable.IsStopped);
        Assert.IsTrue(unreadable.CanWatch);
        Assert.AreEqual("Process unreadable", ResourceText.Status(unreadable, true, new()));
        Assert.AreEqual("Paused", ResourceText.Status(unreadable, false, new()));
        Assert.AreEqual("Waiting for a sample", ResourceText.Status(Alert(), true, new()));
    }

    [TestMethod]
    public void Pause_LeavesAGapWithoutLosingWatchOrDetectionAndExitEventsRetainLastSample()
    {
        var alert = ResourceHistory.Append(Alert() with { Watching = true }, new(1, At, Usage()));
        var paused = ResourceHistory.Break([alert], 2, At.AddSeconds(1));
        Assert.IsNull(paused[0].Current);
        Assert.IsTrue(paused[0].Watching);
        Assert.AreEqual(alert.Usage, paused[0].Usage);
        var exited = ResourceHistory.Exit([alert], First);
        Assert.AreEqual(ProcessAvailability.Exited, exited[0].Availability);
        Assert.AreEqual(90, exited[0].Current!.CpuPercent);
        var exitedAfterGap = ResourceHistory.Exit(paused, First);
        Assert.AreEqual(90, exitedAfterGap[0].Current!.CpuPercent);
    }

    [TestMethod]
    public void Chart_UsesIndependentEquallySpacedBarsAndFixedCpuScale()
    {
        ResourceReading[] history = [new(0, At, Usage(0)), new(1, At.AddSeconds(1), Usage(50)),
            new(10, At.AddSeconds(10), Usage(100))];
        string path = ResourceHistory.BarPath(history, ResourceKind.Cpu);
        Assert.AreEqual(" M154.75,26 L154.75,25 M156.05,26 L156.05,14 M157.35,26 L157.35,2", path);
    }

    [TestMethod]
    public void Chart_NewBarsStayAtTheRightAndShiftExistingBarsLeftBeforeHistoryIsFull()
    {
        var alert = ResourceHistory.Append(Alert(), new(0, At, Usage(50)));
        StringAssert.Contains(ResourceHistory.BarPath(alert.History, ResourceKind.Cpu), "M157.35,26 L157.35,14");
        alert = ResourceHistory.Append(alert, new(1, At.AddSeconds(1), Usage(100)));
        Assert.AreEqual(" M156.05,26 L156.05,14 M157.35,26 L157.35,2",
            ResourceHistory.BarPath(alert.History, ResourceKind.Cpu));
    }

    [TestMethod]
    public void Chart_FullHistoryDropsOnlyTheOldestBarWithoutChangingSpacing()
    {
        var alert = Alert();
        for (int i = 0; i < ResourceHistory.Limit; i++)
            alert = ResourceHistory.Append(alert, new(i, At.AddSeconds(i), Usage(i == 0 ? 10 : i == ResourceHistory.Limit - 1 ? 30 : 20)));
        alert = ResourceHistory.Append(alert, new(ResourceHistory.Limit, At.AddSeconds(ResourceHistory.Limit), Usage(50)));
        string path = ResourceHistory.BarPath(alert.History, ResourceKind.Cpu);
        Assert.AreEqual(ResourceHistory.Limit, path.Count(c => c == 'M'));
        Assert.IsTrue(path.StartsWith(" M2.65,26 L2.65,21.2", StringComparison.Ordinal));
        StringAssert.Contains(path, "M156.05,26 L156.05,18.8");
        Assert.IsTrue(path.EndsWith("M157.35,26 L157.35,14", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Chart_EmptyHistoryIsEmptyAndAZeroSampleStillHasAVisibleBar()
    {
        Assert.AreEqual("", ResourceHistory.BarPath([], ResourceKind.Cpu));
        ResourceReading[] history = [new(0, At, Usage(0))];
        Assert.AreEqual(" M157.35,26 L157.35,25", ResourceHistory.BarPath(history, ResourceKind.Cpu));
    }

    [TestMethod]
    public void Chart_UnknownIoAndCpuDoNotBecomeZeroAndFormattingIsCultureIndependent()
    {
        ResourceReading[] history = [new(0, At, Usage(null)), new(1, At.AddSeconds(1), Usage(10)),
            new(2, At.AddSeconds(2), null), new(3, At.AddSeconds(3), Usage(20))];
        Assert.AreEqual("", ResourceHistory.BarPath(history, ResourceKind.Disk));
        Assert.AreEqual("", ResourceHistory.BarPath(history, ResourceKind.Network));
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string path = ResourceHistory.BarPath(history, ResourceKind.Cpu);
            Assert.AreEqual(2, path.Count(c => c == 'M'));
            StringAssert.Contains(path, "M154.75,26 L154.75,23.6");
            StringAssert.Contains(path, "M157.35,26 L157.35,21.2");
            Assert.IsFalse(path.Contains("NaN", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [TestMethod]
    public void Reminder_RetainsRollingSamplesAndWatchFlagInItsOriginalSlot()
    {
        var alert = ResourceHistory.Append(Alert() with { Watching = true }, new(1, At, Usage(10)));
        var updated = ResourceAlertList.Add([alert], Alert() with { Kinds = ResourceKind.Ram }, out bool overflow);
        Assert.IsFalse(overflow);
        Assert.AreSame(alert.History, updated[0].History);
        Assert.IsTrue(updated[0].Watching);
        Assert.AreEqual(ResourceKind.Cpu | ResourceKind.Ram, updated[0].Kinds);
    }
}
