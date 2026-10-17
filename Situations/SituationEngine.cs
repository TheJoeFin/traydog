using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.UI.Dispatching;          // DispatcherQueue, DispatcherQueueTimer
using Microsoft.Windows.System.Power;    // PowerManager.SystemSuspendStatusChanged

// What the UI renders: the situations in priority order, which are active, and what the sensors see.
sealed record SituationState(
    IReadOnlyList<Situation> Situations,
    IReadOnlySet<string> ActiveIds,
    SituationContext Context,
    LocationAccess LocationAccess)
{
    public Situation? Find(string id) => Situations.FirstOrDefault(s => s.Id == id);

    public bool IsActive(string id) => ActiveIds.Contains(id);
}

// App-lifetime owner of the user's situations: loads and saves them, runs the sensors their
// conditions need, re-evaluates when a sensor reports, and hands each monitor its demand (Monitors).
//
// This is background work while hidden, the same documented exception as Watchdog: situations exist
// to act while nobody's looking. It's opt-in (nothing runs until the user creates a situation), every
// sensor is event-driven (network, power, and device notifications; one clock timer to the next time
// boundary), and only the sensors some enabled situation uses are running. Location is the one that
// polls: a quick fix every 10 minutes and on network changes, only while a place condition exists.
static class SituationEngine
{
    static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    // Cheap sensors the editor runs while it's open, so its pickers and live status have data even
    // for a condition no enabled situation uses yet. Location isn't one: it asks for permission.
    const SensorKind EditorSensors = SensorKind.Network | SensorKind.Power | SensorKind.Devices | SensorKind.Clock;

    static DispatcherQueue? _dispatcher;
    static DispatcherQueueTimer? _clockTimer;
    static DispatcherQueueTimer? _saveTimer;
    static NetworkSensor? _network;
    static PowerSensor? _power;
    static DeviceSensor? _devices;
    static LocationSensor? _location;
    static bool _watchingResume;
    static bool _editorOpen;
    static bool _savePending;
    static int _evaluationQueued;

    public static SituationState State { get; private set; } =
        new([], new HashSet<string>(), SituationContext.Unknown(DateTime.Now), LocationAccess.Unknown);

    // Raised on the UI thread whenever State changes.
    public static event Action? Changed;

    // UseExternalStore's subscribe shape: returns the unsubscribe action.
    public static Action Subscribe(Action onChange)
    {
        Changed += onChange;
        return () => Changed -= onChange;
    }

    // Call once on the UI thread at startup, after the monitors are registered.
    public static void Start()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _clockTimer = _dispatcher.CreateTimer();
        _clockTimer.IsRepeating = false;
        _clockTimer.Tick += (_, _) => Evaluate();
        _saveTimer = _dispatcher.CreateTimer();
        _saveTimer.IsRepeating = false;
        _saveTimer.Interval = SaveDelay;
        _saveTimer.Tick += (_, _) => FlushSave();
        // A hand-switched monitor can come with an expiry that needs the same timer.
        Monitors.Changed += () => ScheduleWake(DateTime.Now);

        State = State with { Situations = SituationStore.Load() };
        UpdateSensors();
        Evaluate();
    }

    // App exit: write any pending change and stop every sensor.
    public static void Stop()
    {
        FlushSave();
        _clockTimer?.Stop();
        SetSensor(ref _network, false, () => new NetworkSensor());
        SetSensor(ref _power, false, () => new PowerSensor());
        SetSensor(ref _devices, false, () => new DeviceSensor());
        SetSensor(ref _location, false, () => new LocationSensor());
        WatchResume(false);
    }

    // The editor window opened or closed.
    public static void SetEditorOpen(bool open)
    {
        _editorOpen = open;
        if (!open)
            FlushSave();
        UpdateSensors();
        Evaluate();
    }

    public static void Add(Situation situation) => Edit([.. State.Situations, situation]);

    public static void Update(Situation situation) =>
        Edit([.. State.Situations.Select(s => s.Id == situation.Id ? situation : s)]);

    public static void Remove(string id) => Edit([.. State.Situations.Where(s => s.Id != id)]);

    // By id, so a debounced rename lands on the latest version of the situation.
    public static void Rename(string id, string name)
    {
        if (State.Find(id) is { } situation && situation.Name != name)
            Update(situation with { Name = name });
    }

    // Moves a situation up (-1) or down (+1); earlier situations win when two want the same monitor.
    public static void Move(string id, int offset)
    {
        var list = State.Situations.ToList();
        int from = list.FindIndex(s => s.Id == id);
        int to = from + offset;
        if (from < 0 || to < 0 || to >= list.Count)
            return;

        (list[from], list[to]) = (list[to], list[from]);
        Edit(list);
    }

    // Edits arrive per keystroke; saves wait for a pause.
    static void Edit(IReadOnlyList<Situation> situations)
    {
        State = State with { Situations = situations };
        _savePending = true;
        _saveTimer?.Stop();
        _saveTimer?.Start();
        UpdateSensors();
        Evaluate();
    }

    static void FlushSave()
    {
        if (!_savePending)
            return;

        _savePending = false;
        _saveTimer?.Stop();
        SituationStore.Save(State.Situations);
    }

    // The situations whose conditions are watched: the enabled ones, or all of them while the editor
    // shows their live status.
    static IEnumerable<Situation> Watched => _editorOpen ? State.Situations : State.Situations.Where(s => s.Enabled);

    // Starts the sensors the situations need and stops the rest.
    static void UpdateSensors()
    {
        var needed = SituationPolicy.SensorsFor(Watched) | (_editorOpen ? EditorSensors : SensorKind.None);

        SetSensor(ref _network, needed.HasFlag(SensorKind.Network) || needed.HasFlag(SensorKind.Location), () =>
        {
            var sensor = new NetworkSensor();
            sensor.Changed += () =>
            {
                _location?.Refresh();
                QueueEvaluate();
            };
            return sensor;
        });
        SetSensor(ref _power, needed.HasFlag(SensorKind.Power), () =>
        {
            var sensor = new PowerSensor();
            sensor.Changed += QueueEvaluate;
            return sensor;
        });
        SetSensor(ref _devices, needed.HasFlag(SensorKind.Devices), () =>
        {
            var sensor = new DeviceSensor();
            sensor.Changed += QueueEvaluate;
            return sensor;
        });
        SetSensor(ref _location, needed.HasFlag(SensorKind.Location), () =>
        {
            var sensor = new LocationSensor();
            sensor.Changed += QueueEvaluate;
            sensor.Start();
            return sensor;
        });

        WatchResume(needed != SensorKind.None);
    }

    static void SetSensor<T>(ref T? sensor, bool on, Func<T> create) where T : class, IDisposable
    {
        if (on && sensor is null)
        {
            sensor = create();
        }
        else if (!on && sensor is not null)
        {
            sensor.Dispose();
            sensor = null;
        }
    }

    // After sleep the clock has jumped and timers run late; re-check everything on resume.
    static void WatchResume(bool watch)
    {
        if (watch == _watchingResume)
            return;

        _watchingResume = watch;
        try
        {
            if (watch)
                PowerManager.SystemSuspendStatusChanged += OnSuspendStatusChanged;
            else
                PowerManager.SystemSuspendStatusChanged -= OnSuspendStatusChanged;
        }
        catch
        {
            // Power notifications are a nicety; the clock timer still catches up.
        }
    }

    static void OnSuspendStatusChanged(object? sender, object e)
    {
        if (PowerManager.SystemSuspendStatus is SystemSuspendStatus.AutoResume or SystemSuspendStatus.ManualResume)
            QueueEvaluate();
    }

    // Sensors report on background threads, often in bursts (a network change raises several
    // events); fold each burst into one evaluation on the UI thread.
    static void QueueEvaluate()
    {
        if (Interlocked.Exchange(ref _evaluationQueued, 1) == 0 &&
            _dispatcher?.TryEnqueue(Evaluate) != true)
        {
            _evaluationQueued = 0;
        }
    }

    static void Evaluate()
    {
        Interlocked.Exchange(ref _evaluationQueued, 0);

        var now = DateTime.Now;
        var context = new SituationContext(now, _network?.Networks, _location?.Location, _power?.Power, _devices?.Devices);
        var situations = State.Situations;

        Monitors.ApplyDemands(SituationPolicy.Demands(situations, context));
        State = State with
        {
            ActiveIds = situations.Where(s => s.IsActive(context)).Select(s => s.Id).ToHashSet(),
            Context = context,
            LocationAccess = _location?.Access ?? State.LocationAccess
        };
        Changed?.Invoke();

        ScheduleWake(now);
    }

    // One timer for both reasons to look again: the next time boundary, and the moment a hand-switched
    // monitor goes back to its situation.
    static void ScheduleWake(DateTime now)
    {
        if (_clockTimer is null)
            return;

        DateTime? wake = Monitors.NextOverrideExpiry();
        if (SituationPolicy.SensorsFor(Watched).HasFlag(SensorKind.Clock))
        {
            var clock = SituationPolicy.NextClockCheck(Watched, now);
            wake = wake is { } expiry && expiry < clock ? expiry : clock;
        }

        _clockTimer.Stop();
        if (wake is not { } at)
            return;

        // A second past the boundary, so the evaluation lands on the far side of it.
        _clockTimer.Interval = at - now + TimeSpan.FromSeconds(1);
        _clockTimer.Start();
    }
}
