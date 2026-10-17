using System;
using System.IO;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Windowing;
using static Microsoft.UI.Reactor.Factories;

sealed class ResourcesWindow
{
    readonly ReactorWindow _window = ReactorApp.OpenWindow(new WindowSpec
    {
        Title = $"Processes - {StatusText.AppName}",
        Width = 760,
        Height = 760,
        MinWidth = 620,
        MinHeight = 500,
        StartPosition = WindowStartPosition.CenterOnCurrent,
        Icon = WindowIcon.FromPath(Path.Combine(AppContext.BaseDirectory, "Assets", "trayicon.ico")),
        Backdrop = BackdropChoice.Of(BackdropKind.Mica),
    }, _ => Component<ResourcesPage>());
    bool _closed;
    public event Action? Closed;

    public ResourcesWindow()
    {
        _window.Closed += (_, _) =>
        {
            _closed = true;
            Closed?.Invoke();
        };
    }

    public void Activate()
    {
        if (_closed)
            return;
        if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        _window.Activate();
    }

    public void Close()
    {
        if (!_closed)
            _window.Close();
    }
}
