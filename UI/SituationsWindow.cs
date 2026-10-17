using System;
using System.IO;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;         // BackdropChoice
using Microsoft.UI.Windowing;            // OverlappedPresenter
using static Microsoft.UI.Reactor.Factories;

// The situations editor: an ordinary Mica window rather than a popout page, because situations
// outgrow one popout height and the user wants it to stay put while they try a condition out. Opened
// from the status page or the tray menu, one at a time. Closing it closes it for real; TrayApp then
// gives the memory back. While it's open the engine runs the cheap sensors for live status.
sealed class SituationsWindow
{
    public const int Width = 860;
    public const int Height = 640;

    readonly ReactorWindow _window;
    bool _isClosed;

    public SituationsWindow()
    {
        _window = ReactorApp.OpenWindow(
            new WindowSpec
            {
                Title = $"Situations - {StatusText.AppName}",
                Width = Width,
                Height = Height,
                MinWidth = 640,
                MinHeight = 440,
                StartPosition = WindowStartPosition.CenterOnCurrent,
                Icon = WindowIcon.FromPath(Path.Combine(AppContext.BaseDirectory, "Assets", "trayicon.ico")),
                Backdrop = BackdropChoice.Of(BackdropKind.Mica),
            },
            _ => Component<SituationsEditor>());

        _window.Closed += OnClosed;
        SituationEngine.SetEditorOpen(true);
    }

    // Raised once the window has closed.
    public event Action? Closed;

    // Brings the window forward, restoring it if minimized.
    public void Activate()
    {
        if (_isClosed)
            return;

        if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        _window.Activate();
    }

    public void Close()
    {
        if (!_isClosed)
            _window.Close(); // raises Closed -> OnClosed
    }

    void OnClosed(object? sender, EventArgs e)
    {
        if (_isClosed)
            return;

        _isClosed = true;
        _window.Closed -= OnClosed;
        SituationEngine.SetEditorOpen(false);
        Closed?.Invoke();
    }
}
