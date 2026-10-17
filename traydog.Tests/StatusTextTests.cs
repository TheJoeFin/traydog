using System;

[TestClass]
internal sealed class StatusTextTests
{
    [TestMethod]
    public void MonitoringOff_UsesTheDogIconAndSaysSo()
    {
        Assert.AreEqual("trayicon.ico", StatusText.IconFile(false, NetStatus.Online));
        Assert.AreEqual("traydog\nMonitoring off", StatusText.Tooltip(false, NetStatus.Online));
        Assert.AreEqual("Monitoring paused", StatusText.Headline(false, NetStatus.Online));
    }

    [TestMethod]
    [DataRow(NetStatus.Online, "Check mark button.ico", "Connected")]
    [DataRow(NetStatus.NoInternet, "Warning.ico", "No internet (network up)")]
    [DataRow(NetStatus.Offline, "Globe with meridians.ico", "Offline")]
    [DataRow(NetStatus.Unknown, "trayicon.ico", "Checking…")]
    public void EachStatus_HasItsIconAndTooltip(NetStatus status, string icon, string trayStatus)
    {
        Assert.AreEqual(icon, StatusText.IconFile(true, status));
        Assert.AreEqual($"traydog\n{trayStatus}", StatusText.Tooltip(true, status));
    }

    [TestMethod]
    public void Tooltip_AddsActiveSituationsOnAThirdLine()
    {
        Assert.AreEqual("traydog\nConnected\nAt work, Docked", StatusText.Tooltip(true, NetStatus.Online, ["At work", "Docked"]));
        Assert.AreEqual("traydog\nConnected", StatusText.Tooltip(true, NetStatus.Online, []));
        Assert.AreEqual(127, StatusText.Tooltip(true, NetStatus.Online, [new string('x', 200)]).Length);
    }

    [TestMethod]
    public void Description_ShowsProbeDetailOnlyWhileMonitoring()
    {
        var net = new NetState(NetStatus.Online, "ping 12ms · http ok");

        Assert.AreEqual("ping 12ms · http ok", StatusText.Description(true, net));
        Assert.AreNotEqual(net.Detail, StatusText.Description(false, net));
    }

    [TestMethod]
    public void Cadence_DescribesBothIntervals()
    {
        Assert.AreEqual(
            "Checking every 15 seconds while open, every minute in the tray",
            StatusText.Cadence(true, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1)));
    }

    [TestMethod]
    public void ProgressPercent_IsTheFractionOfTheWaitElapsed()
    {
        var last = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
        var next = last.AddSeconds(60);

        Assert.AreEqual(0, StatusText.ProgressPercent(last, next, last));
        Assert.AreEqual(50, StatusText.ProgressPercent(last, next, last.AddSeconds(30)));
    }

    [TestMethod]
    public void ProgressPercent_IsIndeterminateWhenDueOrUnscheduled()
    {
        var last = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
        var next = last.AddSeconds(60);

        Assert.IsNull(StatusText.ProgressPercent(last, next, next));            // probe running
        Assert.IsNull(StatusText.ProgressPercent(null, next, last));            // nothing probed yet
        Assert.IsNull(StatusText.ProgressPercent(next, next, last));            // zero-length wait
    }
}
