using System;

[TestClass]
internal sealed class MonitoringPolicyTests
{
    [TestMethod]
    [DataRow(true, false, false, NetStatus.Online)]
    [DataRow(true, true, true, NetStatus.Online)]
    [DataRow(false, true, false, NetStatus.NoInternet)]   // ping answers, HTTP doesn't: captive portal, DNS
    [DataRow(false, false, true, NetStatus.NoInternet)]   // adapter up, nothing reachable
    [DataRow(false, false, false, NetStatus.Offline)]
    public void Classify_OnlyAnHttpRoundTripMeansOnline(bool httpOk, bool pingOk, bool networkUp, NetStatus expected)
    {
        Assert.AreEqual(expected, ConnectivityMonitor.Classify(httpOk, pingOk, networkUp));
    }

    [TestMethod]
    public void SanitizeInTray_KeepsOfferedChoices()
    {
        foreach (var choice in CheckInterval.InTrayChoices)
            Assert.AreEqual(choice, CheckInterval.SanitizeInTray((int)choice.TotalSeconds));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(-60)]
    [DataRow(86400)]
    public void SanitizeInTray_SnapsAnythingElseToTheDefault(int seconds)
    {
        Assert.AreEqual(CheckInterval.DefaultInTray, CheckInterval.SanitizeInTray(seconds));
    }

    [TestMethod]
    public void IndexOf_FallsBackToTheDefaultsPosition()
    {
        Assert.AreEqual(0, CheckInterval.IndexOf(TimeSpan.FromSeconds(30)));
        Assert.AreEqual(Array.IndexOf(CheckInterval.InTrayChoices, CheckInterval.DefaultInTray), CheckInterval.IndexOf(TimeSpan.FromSeconds(7)));
    }

    [TestMethod]
    public void Labels_ReadNaturally()
    {
        Assert.AreEqual("Every 30 seconds", CheckInterval.Label(TimeSpan.FromSeconds(30)));
        Assert.AreEqual("Every minute", CheckInterval.Label(TimeSpan.FromMinutes(1)));
        Assert.AreEqual("Every 5 minutes", CheckInterval.Label(TimeSpan.FromMinutes(5)));
        Assert.AreEqual("Every 90 seconds", CheckInterval.Label(TimeSpan.FromSeconds(90)));
    }
}
