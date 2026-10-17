using System.Drawing;

[TestClass]
internal sealed class PopoutPlacementTests
{
    // 1920x1080 monitor with a 48 px taskbar along the bottom.
    static readonly Rectangle WorkArea = new(0, 0, 1920, 1032);
    static readonly Rectangle BottomTaskbar = Rectangle.FromLTRB(0, 1032, 1920, 1080);
    static readonly Size Popup = new(360, 400);
    const int Margin = 12;

    [TestMethod]
    public void BottomTaskbar_CentersOnIconAndSitsAboveTheTaskbar()
    {
        var position = PopoutPlacement.Calculate(new Point(1000, 1056), Popup, WorkArea, BottomTaskbar, TaskbarEdge.Bottom, Margin);

        Assert.AreEqual(1000 - 180, position.X);
        Assert.AreEqual(1032 - 400 - Margin, position.Y);
    }

    [TestMethod]
    public void IconNearTheRightEdge_IsClampedInsideTheWorkArea()
    {
        var position = PopoutPlacement.Calculate(new Point(1900, 1056), Popup, WorkArea, BottomTaskbar, TaskbarEdge.Bottom, Margin);

        Assert.AreEqual(1920 - 360 - Margin, position.X);
    }

    [TestMethod]
    public void TopTaskbar_SitsBelowTheTaskbar()
    {
        var work = new Rectangle(0, 48, 1920, 1032);
        var taskbar = Rectangle.FromLTRB(0, 0, 1920, 48);

        var position = PopoutPlacement.Calculate(new Point(1000, 24), Popup, work, taskbar, TaskbarEdge.Top, Margin);

        Assert.AreEqual(48 + Margin, position.Y);
    }

    [TestMethod]
    public void LeftTaskbar_SitsBesideTheTaskbar()
    {
        var work = new Rectangle(48, 0, 1872, 1080);
        var taskbar = Rectangle.FromLTRB(0, 0, 48, 1080);

        var position = PopoutPlacement.Calculate(new Point(24, 1000), Popup, work, taskbar, TaskbarEdge.Left, Margin);

        Assert.AreEqual(48 + Margin, position.X);
        Assert.IsTrue(position.Y + Popup.Height <= work.Bottom - Margin);
    }

    [TestMethod]
    public void RightTaskbar_SitsBesideTheTaskbar()
    {
        var work = new Rectangle(0, 0, 1872, 1080);
        var taskbar = Rectangle.FromLTRB(1872, 0, 1920, 1080);

        var position = PopoutPlacement.Calculate(new Point(1896, 1000), Popup, work, taskbar, TaskbarEdge.Right, Margin);

        Assert.AreEqual(1872 - 360 - Margin, position.X);
    }

    [TestMethod]
    public void NoTaskbar_FallsBackToTheBottomOfTheWorkArea()
    {
        var position = PopoutPlacement.Calculate(new Point(1000, 500), Popup, WorkArea, null, TaskbarEdge.Bottom, Margin);

        Assert.AreEqual(1032 - 400 - Margin, position.Y);
    }

    [TestMethod]
    public void ToPhysical_ScalesAndRoundsUp()
    {
        Assert.AreEqual(360, PopoutPlacement.ToPhysical(360, 96));
        Assert.AreEqual(540, PopoutPlacement.ToPhysical(360, 144));
        Assert.AreEqual(16, PopoutPlacement.ToPhysical(13, 115)); // 15.57 -> 16
        Assert.AreEqual(360, PopoutPlacement.ToPhysical(360, 0)); // unknown DPI -> 96
    }
}
