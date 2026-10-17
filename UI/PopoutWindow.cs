using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;          // DispatcherQueue, DispatcherQueueTimer
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;         // BackdropChoice
using Microsoft.UI.Windowing;            // OverlappedPresenter
using Windows.Graphics;                  // PointInt32
using Windows.UI.ViewManagement;         // UISettings
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using static Microsoft.UI.Reactor.Factories;

// The borderless popout next to the tray icon: a Reactor window hosting the TrayPopout component,
// styled like the shell's own flyouts. It reports visibility to PopoutStore for the content. It slides and fades in and light-dismisses when focus
// leaves. Dismissing only hides it, so a reopen is instant; after CloseDelay hidden it closes for
// real and the next open builds a new window, so the idle app holds no XAML tree.
sealed class PopoutWindow
{
    public const int Width = MonitoringLayout.Width;
    public const int Height = MonitoringLayout.Height;

    // System backdrops ignore UIElement.Opacity, so the fade uses per-window layered alpha
    // (ReactorWindow.SetOpacity) driven by a frame timer, alongside a small slide.
    const int ShowSlideDistance = 24;
    const int HideSlideDistance = 12;
    const double ShowDurationMs = 250;
    const double HideDurationMs = 180;

    // A tray click right after a light-dismiss is the same click that dismissed the popout (focus
    // left to the taskbar); treat it as "close" instead of re-showing.
    static readonly TimeSpan RecentDismissWindow = TimeSpan.FromMilliseconds(300);
    static readonly TimeSpan CloseDelay = TimeSpan.FromMinutes(1);

    readonly ReactorWindow _window;
    readonly HWND _hwnd;
    readonly ShellBackdrop _backdrop = new();
    readonly UISettings _uiSettings = new();
    readonly DispatcherQueue _dispatcher;
    readonly DispatcherQueueTimer _hideTimer;
    readonly DispatcherQueueTimer _closeTimer;
    readonly DispatcherQueueTimer _animationTimer;
    readonly Stopwatch _animationClock = new();

    bool _isShowing;
    bool _isVisible;
    bool _isClosed;
    DateTime _lastDismissedAtUtc = DateTime.MinValue;

    // Animation state: _baseX/_baseY is the resting position; the slide animates an offset below it
    // in physical pixels. _current* is where the last frame left off, so an animation that
    // interrupts another starts from there instead of snapping.
    bool _isHiding;
    int _baseX;
    int _baseY;
    int _fromOffset;
    int _toOffset;
    double _fromOpacity;
    double _toOpacity;
    double _durationMs = ShowDurationMs;
    int _currentOffset;
    double _currentOpacity;

    public PopoutWindow()
    {
        _window = ReactorApp.OpenWindow(
            new WindowSpec
            {
                Title = StatusText.AppName,
                Width = Width,
                Height = Height,
                ResizeMode = WindowResizeMode.NoResize,
                IsMaximizable = false,
                IsMinimizable = false,
                ShowInTaskbar = false,
                ShowInSwitcher = false,
                Level = WindowLevel.AlwaysOnTop,
                CornerStyle = WindowCornerStyle.Rounded,
                StartPosition = WindowStartPosition.Default, // Show() owns tray-anchored placement.
                ActivateOnOpen = false,
                Icon = WindowIcon.FromPath(Path.Combine(AppContext.BaseDirectory, "Assets", "trayicon.ico")),
                // A stable factory, so Reactor materializes it once and Update() reaches the live one.
                Backdrop = BackdropChoice.Of(() => _backdrop),
            },
            _ => Component<TrayPopout>().Provide(TrayPopout.Dismiss, Hide));

        _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(_window.NativeWindow);
        _dispatcher = _window.NativeWindow.DispatcherQueue;

        // Keep the thin border (DWM shadow + rounded corners) but drop the title bar.
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(true, false);

        ApplyShellTheme();
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        _window.Activated += OnActivated;
        _window.Deactivated += OnDeactivated;
        _window.Closing += OnClosing;
        _window.Closed += OnClosed;

        _hideTimer = CreateTimer(TimeSpan.FromMilliseconds(150), HideIfFocusLeftWindow);
        _closeTimer = CreateTimer(CloseDelay, CloseIfStillHidden);
        _animationTimer = CreateTimer(TimeSpan.FromMilliseconds(15), AnimationTick, repeating: true);
    }

    // Raised once the window has closed for real (idle close or app exit).
    public event Action? Closed;

    public bool IsVisible => _isVisible;

    // Tray-icon click: show, or hide if already visible.
    public void Toggle()
    {
        if (_isVisible)
        {
            Hide();
            return;
        }

        if (DateTime.UtcNow - _lastDismissedAtUtc < RecentDismissWindow)
            return;

        Show(fromTrayClick: true);
    }

    public void Show(bool fromTrayClick, bool activate = true)
    {
        if (_isClosed)
            return;

        if (_isVisible)
        {
            if (activate)
            {
                _window.Activate();
                PInvoke.SetForegroundWindow(_hwnd);
            }
            return;
        }

        // Position before showing so the window never flashes at a stale location.
        // Size once per opening; new alerts scroll instead of moving an existing click target.
        int height = PopoutStore.Current.Page == PopoutPage.Status
            ? MonitoringLayout.PreferredHeight(ResourceMonitor.State.Alerts.Count) : Height;
        WindowPlacement.PositionNearTray(_window.AppWindow, Width, height, fromTrayClick);
        _baseX = _window.AppWindow.Position.X;
        _baseY = _window.AppWindow.Position.Y;

        _hideTimer.Stop();
        _closeTimer.Stop();
        _isShowing = activate;
        _isVisible = true;

        // Re-read here too: not every taskbar setting change raises ColorValuesChanged.
        ApplyShellTheme();
        PlayShowAnimation();

        if (activate)
        {
            _window.Activate();
            PInvoke.SetForegroundWindow(_hwnd);
        }
        else
        {
            _window.AppWindow.Show(activateWindow: false);
        }
        PopoutStore.SetVisible(true, activate);
    }

    public void Hide()
    {
        _hideTimer.Stop();
        _isShowing = false;
        if (!_isVisible)
            return;

        _isVisible = false;
        _lastDismissedAtUtc = DateTime.UtcNow;
        PopoutStore.SetVisible(false);
        PlayHideAnimation();

        _closeTimer.Stop();
        _closeTimer.Start();
    }

    // Closes the window for real: stops its timers and unsubscribes from app-lifetime events so
    // nothing keeps the window alive.
    public void Close()
    {
        if (_isClosed)
            return;

        _window.Close(); // raises Closed -> OnClosed
    }

    void OnClosed(object? sender, EventArgs e)
    {
        if (_isClosed)
            return;

        _isClosed = true;
        _hideTimer.Stop();
        _closeTimer.Stop();
        _animationTimer.Stop();
        if (_isVisible)
            PopoutStore.SetVisible(false);
        _isVisible = false;

        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        _window.Activated -= OnActivated;
        _window.Deactivated -= OnDeactivated;
        _window.Closing -= OnClosing;
        _window.Closed -= OnClosed;
        Closed?.Invoke();
    }

    void CloseIfStillHidden()
    {
        if (!_isVisible)
            Close();
    }

    // Alt+F4 hides like any other dismissal; only the app (idle close, Exit) really closes it.
    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.Reason == WindowCloseReason.UserClosed)
        {
            e.Cancel = true;
            Hide();
        }
    }

    void OnActivated(object? sender, EventArgs e)
    {
        _isShowing = false;
        _hideTimer.Stop();
    }

    void OnDeactivated(object? sender, EventArgs e)
    {
        if (_isShowing || !_isVisible)
            return;

        // Debounce: focus bounces briefly while XAML popups (tooltips, menus, dropdowns) open.
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    void HideIfFocusLeftWindow()
    {
        if (_isVisible && !ShouldRemainVisible(PInvoke.GetForegroundWindow()))
            Hide();
    }

    bool ShouldRemainVisible(HWND foreground)
    {
        if (foreground == HWND.Null)
            return false;

        if (foreground == _hwnd || PInvoke.GetAncestor(foreground, GET_ANCESTOR_FLAGS.GA_ROOT) == _hwnd)
            return true;

        // WinUI flyouts and ComboBox dropdowns open in their own popup HWNDs; classic menus use #32768.
        Span<char> buffer = stackalloc char[64];
        int length = PInvoke.GetClassName(foreground, buffer);
        if (length <= 0)
            return false;

        ReadOnlySpan<char> className = buffer[..length];
        return className.SequenceEqual("Xaml_WindowedPopupClass") || className.SequenceEqual("#32768");
    }

    // Raised off the UI thread when the accent color or light/dark mode changes.
    void OnColorValuesChanged(UISettings sender, object args) => _dispatcher.TryEnqueue(ApplyShellTheme);

    // Matches the taskbar's mode and accent tint, like the shell's own flyouts. (The content's
    // RequestedTheme follows the same source inside TrayPopout.)
    void ApplyShellTheme()
    {
        bool isLight = SystemThemeService.IsLight;
        _backdrop.Update(isLight, SystemThemeService.ShowsAccentColor ? _uiSettings.GetColorValue(UIColorType.AccentDark2) : null);
    }

    void PlayShowAnimation()
    {
        _isHiding = false;
        _durationMs = ShowDurationMs;
        _fromOffset = ScaleForDpi(ShowSlideDistance);
        _toOffset = 0;
        _fromOpacity = 0;
        _toOpacity = 1;
        StartAnimation();
    }

    void PlayHideAnimation()
    {
        _isHiding = true;
        _durationMs = HideDurationMs;
        _fromOffset = _currentOffset;
        _toOffset = ScaleForDpi(HideSlideDistance);
        _fromOpacity = _currentOpacity;
        _toOpacity = 0;
        StartAnimation();
    }

    void StartAnimation()
    {
        // "Animation effects" off in Windows settings: jump straight to the end state.
        if (!_uiSettings.AnimationsEnabled)
            _durationMs = 0;

        ApplyFrame(_fromOffset, _fromOpacity);
        _animationClock.Restart();
        _animationTimer.Start();
        AnimationTick();
    }

    void AnimationTick()
    {
        double progress = _durationMs <= 0 ? 1 : Math.Min(1, _animationClock.Elapsed.TotalMilliseconds / _durationMs);

        // Cubic ease-out on the way in, ease-in on the way out; linear fade.
        double eased = _isHiding ? progress * progress * progress : 1 - Math.Pow(1 - progress, 3);
        int offset = _fromOffset + (int)Math.Round((_toOffset - _fromOffset) * eased);
        double opacity = _fromOpacity + (_toOpacity - _fromOpacity) * progress;
        ApplyFrame(offset, opacity);

        if (progress < 1)
            return;

        _animationTimer.Stop();
        _animationClock.Reset();
        if (_isHiding)
            _window.Hide();
    }

    void ApplyFrame(int offset, double opacity)
    {
        _currentOffset = offset;
        _currentOpacity = Math.Clamp(opacity, 0, 1);
        _window.SetOpacity(_currentOpacity);
        _window.AppWindow.Move(new PointInt32(_baseX, _baseY + offset));
    }

    int ScaleForDpi(int logical) => PopoutPlacement.ToPhysical(logical, PInvoke.GetDpiForWindow(_hwnd));

    DispatcherQueueTimer CreateTimer(TimeSpan interval, Action tick, bool repeating = false)
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = repeating;
        timer.Tick += (_, _) => tick();
        return timer;
    }
}
