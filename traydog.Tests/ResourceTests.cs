using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

[TestClass]
internal sealed class ResourceTests
{
    static readonly ProcessKey First = new(123, 100);
    static readonly ResourceOptions Defaults = new();

    static ResourceUsage Usage(double? cpu = 90, long ram = 0, double? disk = null, double? network = null,
        ProcessKey? key = null, string name = "example") => new(key ?? First, name, cpu, ram, disk, network);

    [TestMethod]
    public void Defaults_MatchResourceMonitoringChoices()
    {
        Assert.AreEqual(30, Defaults.ScanSeconds);
        Assert.AreEqual(1, Defaults.ConfirmSeconds);
        Assert.AreEqual(20, Defaults.SustainedSeconds);
        Assert.AreEqual(80, Defaults.CpuPercent);
        Assert.AreEqual(1024, Defaults.RamMegabytes);
        Assert.AreEqual(50, Defaults.DiskMegabytesPerSecond);
        Assert.AreEqual(10, Defaults.NetworkMegabytesPerSecond);
    }

    [TestMethod]
    public void Options_ClampUnsafeTimingsAndThresholds()
    {
        var options = (Defaults with
        {
            ScanSeconds = 0, ConfirmSeconds = 0, SustainedSeconds = int.MaxValue,
            CpuPercent = double.NaN, RamMegabytes = double.PositiveInfinity,
            DiskMegabytesPerSecond = -1, NetworkMegabytesPerSecond = double.NegativeInfinity
        }).Sanitize();
        Assert.AreEqual(5, options.ScanSeconds);
        Assert.AreEqual(1, options.ConfirmSeconds);
        Assert.AreEqual(300, options.SustainedSeconds);
        Assert.AreEqual(80, options.CpuPercent);
        Assert.AreEqual(1024, options.RamMegabytes);
        Assert.AreEqual(0.1, options.DiskMegabytesPerSecond);
        Assert.AreEqual(10, options.NetworkMegabytesPerSecond);
    }

    [TestMethod]
    public void Rules_NormalizeDeduplicateAndGiveIgnorePrecedence()
    {
        var options = (Defaults with
        {
            IgnoredNames = [" Example.EXE ", "example", @"C:\bad.exe", "*"],
            OnSightNames = ["EXAMPLE", "other.exe", "OTHER", ""]
        }).Sanitize();
        CollectionAssert.AreEqual(new[] { "example" }, options.IgnoredNames);
        CollectionAssert.AreEqual(new[] { "other" }, options.OnSightNames);
    }

    [TestMethod]
    [DataRow(@"C:\Windows\notepad.exe")]
    [DataRow("../notepad.exe")]
    [DataRow("*.exe")]
    [DataRow("")]
    [DataRow(".exe")]
    [DataRow("a\nb")]
    public void Rules_RejectPathsWildcardsAndEmptyNames(string input) => Assert.IsNull(ProcessName.Normalize(input));

    [TestMethod]
    public void Json_RoundTripsOptionsAndRules()
    {
        var options = Defaults with { ScanSeconds = 120, RamMegabytes = 200, IgnoredNames = ["one"], OnSightNames = ["two"] };
        string json = JsonSerializer.Serialize(options, ResourceJson.Default.ResourceOptions);
        var read = JsonSerializer.Deserialize(json, ResourceJson.Default.ResourceOptions)!;
        Assert.AreEqual(120, read.ScanSeconds);
        Assert.AreEqual(200, read.RamMegabytes);
        CollectionAssert.AreEqual(options.IgnoredNames, read.IgnoredNames);
        CollectionAssert.AreEqual(options.OnSightNames, read.OnSightNames);
        Assert.AreEqual(30, JsonSerializer.Deserialize("{}", ResourceJson.Default.ResourceOptions)!.ScanSeconds);
        Assert.IsTrue(JsonSerializer.Deserialize("{}", ResourceJson.Default.ResourceOptions)!.CpuEnabled);
        Assert.AreEqual(0, JsonSerializer.Deserialize("{}", ResourceJson.Default.ResourceOptions)!.IgnoredNames.Length);
    }

    [TestMethod]
    public void Rates_NormalizeCpuToTheWholeMachineAndUseByteDeltas()
    {
        var before = new ProcessSample(First, "example", 0, 50, 100, 200, 10);
        var after = before with { CpuTicks = 8 * TimeSpan.TicksPerSecond, RamBytes = 1000, DiskBytes = 2100, NetworkBytes = 4200, AtSeconds = 12 };
        var rates = ResourceUsage.From(after, before, 8);
        Assert.AreEqual(50, rates.CpuPercent);
        Assert.AreEqual(1000, rates.RamBytes);
        Assert.AreEqual(1000, rates.DiskBytesPerSecond);
        Assert.AreEqual(2000, rates.NetworkBytesPerSecond);
    }

    [TestMethod]
    public void Rates_FirstSampleAndCounterResetsAreUnknownNotZero()
    {
        var before = new ProcessSample(First, "example", 100, 50, 100, null, 10);
        var after = before with { CpuTicks = 10, DiskBytes = 10, NetworkBytes = 500, AtSeconds = 12 };
        Assert.IsNull(ResourceUsage.From(after, null, 8).CpuPercent);
        var rates = ResourceUsage.From(after, before, 8);
        Assert.IsNull(rates.CpuPercent);
        Assert.IsNull(rates.DiskBytesPerSecond);
        Assert.IsNull(rates.NetworkBytesPerSecond);
        Assert.AreEqual(50, rates.RamBytes);
    }

    [TestMethod]
    public void Rates_RejectPidReuseAndNonPositiveTime()
    {
        var sample = new ProcessSample(First, "example", 100, 50, 100, 100, 10);
        Assert.IsNull(ResourceUsage.From(sample, sample, 8).CpuPercent);
        Assert.IsNull(ResourceUsage.From(sample, sample with { AtSeconds = 11 }, 8).DiskBytesPerSecond);
        Assert.IsNull(ResourceUsage.From(sample, sample with { Key = new(123, 99), AtSeconds = 5 }, 8).CpuPercent);
    }

    [TestMethod]
    public void Thresholds_AllFourAreIndependentAndInclusive()
    {
        const long mb = 1024 * 1024;
        var usage = Usage(80, 1024 * mb, 50 * mb, 10 * mb);
        Assert.AreEqual(ResourceKind.Cpu | ResourceKind.Ram | ResourceKind.Disk | ResourceKind.Network, usage.High(Defaults));
        Assert.AreEqual(ResourceKind.None, usage.High(Defaults with
        {
            CpuEnabled = false, RamEnabled = false, DiskEnabled = false, NetworkEnabled = false
        }));
        Assert.AreEqual(ResourceKind.None, Usage(null).High(Defaults));
    }

    [TestMethod]
    public void Policy_ConfirmsTwentySecondsNotASingleSpike()
    {
        var policy = new ResourcePolicy();
        for (int second = 0; second < 20; second++)
            Assert.AreEqual(ResourceKind.None, policy.Observe(Usage(), Defaults, second));
        Assert.AreEqual(1, policy.Candidates.Count);
        Assert.AreEqual(ResourceKind.Cpu, policy.Observe(Usage(), Defaults, 20));
        Assert.AreEqual(0, policy.Candidates.Count); // alerted processes don't keep waking every second
    }

    [TestMethod]
    public void Policy_RecoveryStopsFastRechecksAndResetsTheClock()
    {
        var policy = new ResourcePolicy();
        policy.Observe(Usage(), Defaults, 0);
        policy.Observe(Usage(10), Defaults, 1);
        Assert.AreEqual(0, policy.Candidates.Count);
        for (int second = 2; second < 22; second++)
            Assert.AreEqual(ResourceKind.None, policy.Observe(Usage(), Defaults, second));
        Assert.AreEqual(ResourceKind.Cpu, policy.Observe(Usage(), Defaults, 22));
    }

    [TestMethod]
    public void Policy_AlternatingResourcesDoNotCombineIntoAProblem()
    {
        var policy = new ResourcePolicy();
        for (int second = 0; second < 100; second++)
            Assert.AreEqual(ResourceKind.None, policy.Observe(
                second % 2 == 0 ? Usage() : Usage(10, 2L * 1024 * 1024 * 1024), Defaults, second));
    }

    [TestMethod]
    public void Policy_SleepAndFailedSamplesBreakConfirmation()
    {
        var policy = new ResourcePolicy();
        for (int second = 0; second <= 19; second++)
            policy.Observe(Usage(), Defaults, second);
        Assert.AreEqual(ResourceKind.None, policy.Observe(Usage(), Defaults, 100));
        Assert.AreEqual(ResourceKind.None, policy.Observe(Usage(null), Defaults, 101));
        Assert.AreEqual(0, policy.Candidates.Count);
    }

    [TestMethod]
    public void Policy_DismissRemindsAfterFifteenMinutesWithFreshConfirmation()
    {
        var policy = new ResourcePolicy();
        for (int second = 0; second <= 20; second++)
            policy.Observe(Usage(), Defaults, second);
        policy.Dismiss(First, 100);
        Assert.AreEqual(ResourceKind.None, policy.Observe(Usage(), Defaults, 999));
        Assert.AreEqual(0, policy.Candidates.Count);
        for (int second = 1000; second < 1019; second++)
            Assert.AreEqual(ResourceKind.None, policy.Observe(Usage(), Defaults, second));
        Assert.AreEqual(ResourceKind.Cpu, policy.Observe(Usage(), Defaults, 1019));
    }

    [TestMethod]
    public void Policy_IgnoreOnceDoesNotMuteAReusedPid()
    {
        var policy = new ResourcePolicy();
        policy.IgnoreOnce(First);
        for (int second = 0; second <= 30; second++)
            Assert.AreEqual(ResourceKind.None, policy.Observe(Usage(), Defaults, second));
        var next = First with { StartedUtcTicks = 200 };
        for (int second = 0; second < 20; second++)
            policy.Observe(Usage(key: next), Defaults, second);
        Assert.AreEqual(ResourceKind.Cpu, policy.Observe(Usage(key: next), Defaults, 20));
    }

    [TestMethod]
    public void Policy_IgnoreEveryTimeCoversResourcesAndOnSight()
    {
        var policy = new ResourcePolicy();
        var options = Defaults with { IgnoredNames = ["EXAMPLE"], OnSightNames = ["example"] };
        Assert.IsFalse(policy.OnSight(First, "Example.exe", options));
        for (int second = 0; second <= 30; second++)
            Assert.AreEqual(ResourceKind.None, policy.Observe(Usage(), options, second));
        Assert.AreEqual(0, policy.Candidates.Count);
    }

    [TestMethod]
    public void Policy_OnSightIsOncePerProcessInstanceRegardlessOfUsage()
    {
        var policy = new ResourcePolicy();
        var options = Defaults with { OnSightNames = ["example"] };
        Assert.IsTrue(policy.OnSight(First, "EXAMPLE.exe", options));
        Assert.IsFalse(policy.OnSight(First, "example", options));
        policy.Dismiss(First, 10);
        Assert.IsFalse(policy.OnSight(First, "example", options));
        Assert.IsTrue(policy.OnSight(First with { StartedUtcTicks = 200 }, "example", options));
        Assert.IsFalse(policy.OnSight(new(999, 5), "different", options));
    }

    [TestMethod]
    public void Policy_ForgetMissingReleasesStateAndConfigurationResetPreservesMute()
    {
        var policy = new ResourcePolicy();
        var options = Defaults with { OnSightNames = ["example"] };
        policy.IgnoreOnce(First);
        policy.ResetConfirmation();
        Assert.IsFalse(policy.OnSight(First, "example", options));
        policy.ForgetMissing(new HashSet<ProcessKey>());
        Assert.IsTrue(policy.OnSight(First, "example", options));
    }

    [TestMethod]
    public void Policy_SustainedNetworkAndDiskTriggerWithoutCpu()
    {
        var policy = new ResourcePolicy();
        var usage = Usage(null, disk: 60 * 1024 * 1024, network: 12 * 1024 * 1024);
        for (int second = 0; second < 20; second++)
            policy.Observe(usage, Defaults, second);
        Assert.AreEqual(ResourceKind.Disk | ResourceKind.Network, policy.Observe(usage, Defaults, 20));
    }

    [TestMethod]
    public void Sampler_ReadsOnlyRequestedInstanceAndRejectsPidReuse()
    {
        using var current = Process.GetCurrentProcess();
        var key = new ProcessKey(current.Id, current.StartTime.ToUniversalTime().Ticks);
        var scan = ProcessSampler.Read([key]);
        Assert.AreEqual(1, scan.Samples.Count);
        Assert.AreEqual(key, scan.Samples.Single().Key);
        Assert.IsTrue(scan.Samples.Single().RamBytes > 0);
        Assert.IsTrue(scan.Samples.Single().CpuTicks > 0);
        Assert.AreEqual(0, ProcessSampler.Read([key with { StartedUtcTicks = key.StartedUtcTicks - 1 }]).Samples.Count);
    }

    [TestMethod]
    public void Policy_UnreadableProcessKeepsMuteButNewInstanceDoesNot()
    {
        var policy = new ResourcePolicy();
        var options = Defaults with { OnSightNames = ["example"] };
        policy.IgnoreOnce(First);
        policy.ForgetMissing(new HashSet<ProcessKey>(), new HashSet<int> { First.Pid });
        Assert.IsFalse(policy.OnSight(First, "example", options));
        var replacement = First with { StartedUtcTicks = 500 };
        policy.ForgetMissing(new HashSet<ProcessKey> { replacement });
        Assert.IsTrue(policy.OnSight(replacement, "example", options));
    }

    [TestMethod]
    public void Search_UsesOnlyAnEncodedExecutableNameOnAFixedHttpsHost()
    {
        var uri = ResourceText.SearchUri("Example & Helper.exe");
        Assert.AreEqual("https", uri.Scheme);
        Assert.AreEqual("www.bing.com", uri.Host);
        Assert.AreEqual("?q=example%20%26%20helper.exe", uri.Query);
        Assert.AreEqual("", uri.Fragment);
    }
}
