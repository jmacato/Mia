// SPDX-License-Identifier: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Concurrent;

namespace Mia.App.Presentation;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    const int AudioSampleRate = 48_000;
    const int MaximumPendingAudioSamples = AudioSampleRate / 5;
    readonly IMainViewSession _session;
    readonly IMainViewHost _host;
    readonly MainViewCapabilities _capabilities;
    readonly MainViewAsyncGate _operationGate = new();
    IMainViewAudioOutput? _audioOutput;
    string _status = "Ready";
    bool _isBootEnabled = true;
    bool _isStopEnabled;
    bool _isInputEnabled;
    bool _isBluetoothStatusActive;
    bool _isNetworkStatusActive;
    bool _isControlOverlayOpen;
    bool _bootAttempted;
    int _disposeStarted;
    MainViewFrameEventArgs? _pendingFrame;
    int _frameDispatchPending;
    readonly ConcurrentQueue<ReadOnlyMemory<short>> _pendingAudio = new();
    int _pendingAudioSamples;
    int _audioDispatchPending;

    public MainViewModel(
        IMainViewSession session,
        IMainViewHost host,
        MainViewOptions options)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentNullException.ThrowIfNull(options);

        Details = options.Details ?? string.Empty;
        AutoStart = options.AutoStart;
        PaceToRealTime = options.PaceToRealTime;
        _capabilities = options.Capabilities;

        Gsm = new GsmViewModel(session, host);
        Bluetooth = new BluetoothViewModel(session, host, _operationGate);
        Infrared = new InfraredViewModel(session, host, _operationGate);
        Diagnostics = new DiagnosticsViewModel(session, host, options);
        CommuniCam = new CommuniCamViewModel(
            session, host, _operationGate, options.Capabilities);

        BootCommand = CreateLifecycleCommand(BootAsync, () => IsBootEnabled);
        StopCommand = CreateLifecycleCommand(StopAsync, () => IsStopEnabled);
        ToggleControlOverlayCommand = new RelayCommand(ToggleControlOverlay);
        _operationGate.AvailabilityChanged += NotifyLifecycleCommandState;
        SubscribeToSession();
    }

    public event EventHandler<MainViewFrameEventArgs>? FrameReady;
    public event EventHandler<MainViewPhoneLedEventArgs>? PhoneLedsChanged;
    public event EventHandler? FocusRequested;

    public GsmViewModel Gsm { get; }
    public BluetoothViewModel Bluetooth { get; }
    public InfraredViewModel Infrared { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public CommuniCamViewModel CommuniCam { get; }
    public string Details { get; }
    public bool AutoStart { get; }
    public bool PaceToRealTime { get; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBootEnabled
    {
        get => _isBootEnabled;
        private set
        {
            if (SetProperty(ref _isBootEnabled, value))
            {
                BootCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsStopEnabled
    {
        get => _isStopEnabled;
        private set
        {
            if (SetProperty(ref _isStopEnabled, value))
            {
                StopCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsInputEnabled
    {
        get => _isInputEnabled;
        private set => SetProperty(ref _isInputEnabled, value);
    }

    public bool IsBluetoothStatusActive
    {
        get => _isBluetoothStatusActive;
        private set => SetProperty(ref _isBluetoothStatusActive, value);
    }

    public bool IsNetworkStatusActive
    {
        get => _isNetworkStatusActive;
        private set => SetProperty(ref _isNetworkStatusActive, value);
    }

    public bool IsControlOverlayOpen
    {
        get => _isControlOverlayOpen;
        private set
        {
            if (SetProperty(ref _isControlOverlayOpen, value))
            {
                OnPropertyChanged(nameof(ControlOverlayToolTip));
            }
        }
    }

    public string ControlOverlayToolTip => IsControlOverlayOpen
        ? "Close emulator controls"
        : "Open emulator controls";

    public IAsyncRelayCommand BootCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IRelayCommand ToggleControlOverlayCommand { get; }

    public void SetKey(
        byte scanMask,
        byte rowMask,
        byte? secondaryScanMask,
        bool pressed) =>
        _session.SetKey(scanMask, rowMask, secondaryScanMask, pressed);

    public void SetPowerKey(bool pressed) => _session.SetPowerKey(pressed);

    public void BeginClose()
    {
        _operationGate.Close();
        DisableSessionControls();
        Status = "Saving emulator state…";
    }

    public void ReportHostFailure(string operation, Exception error) =>
        ReportFailure(operation, error);

    AsyncRelayCommand CreateLifecycleCommand(
        Func<Task> operation,
        Func<bool> canExecute) =>
        new AsyncRelayCommand(
            () => RunLifecycleAsync(operation),
            () => _operationGate.IsAvailable && canExecute(),
            AsyncRelayCommandOptions.None);

    async Task RunLifecycleAsync(Func<Task> operation)
    {
        try
        {
            if (!await _operationGate.TryRunAsync(operation).ConfigureAwait(true))
            {
                Status = "Another emulator operation is in progress.";
            }
        }
        catch (Exception error) when (MainViewExceptions.IsRecoverable(error))
        {
            ReportFailure("Emulator operation", error);
            IsBootEnabled = true;
            IsStopEnabled = false;
            IsInputEnabled = false;
            Gsm.IsEnabled = false;
        }
    }

    async Task BootAsync()
    {
        if (_bootAttempted &&
            _capabilities.HasFlag(MainViewCapabilities.ReloadForRestart))
        {
            await ReloadForRestartAsync().ConfigureAwait(true);
            return;
        }

        _bootAttempted = true;
        IsInputEnabled = false;
        IsBootEnabled = false;
        IsStopEnabled = true;
        await _session.StopForRestartAsync().ConfigureAwait(true);
        PrepareAudio();
        Bluetooth.ApplySettings();
        await _session.StartAsync().ConfigureAwait(true);
        IsInputEnabled = true;
        Gsm.IsEnabled = true;
        Gsm.MarkReady();
        CommuniCam.Update();
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    async Task ReloadForRestartAsync()
    {
        DisableSessionControls();
        Status = "Restarting · releasing emulator workers";
        await _session.StopForRestartAsync().ConfigureAwait(true);
        DisposeAudioOutput();
        Status = "Restarting · reloading runtime";
        _host.ReloadForRestart();
    }

    async Task StopAsync()
    {
        IsInputEnabled = false;
        Gsm.IsEnabled = false;
        await _session.StopAsync().ConfigureAwait(true);
        DisposeAudioOutput();
        IsBootEnabled = true;
        IsStopEnabled = false;
        Bluetooth.MarkStopped();
        CommuniCam.Update();
    }

    void PrepareAudio()
    {
        DisposeAudioOutput();
        if (!_capabilities.HasFlag(MainViewCapabilities.LiveAudio))
        {
            return;
        }

        try
        {
            _audioOutput = _host.CreateAudioOutput(AudioSampleRate);
        }
        catch (Exception error) when (MainViewExceptions.IsRecoverable(error))
        {
            ReportFailure("Live audio", error);
        }
    }

    void ToggleControlOverlay()
    {
        IsControlOverlayOpen = !IsControlOverlayOpen;
        if (!IsControlOverlayOpen)
        {
            FocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    void SubscribeToSession()
    {
        _session.StatusChanged += OnStatusChanged;
        _session.FrameReady += OnFrameReady;
        _session.AudioReady += OnAudioReady;
        _session.StatusLedsChanged += OnStatusLedsChanged;
        _session.PhoneLedsChanged += OnPhoneLedsChanged;
    }

    void UnsubscribeFromSession()
    {
        _session.StatusChanged -= OnStatusChanged;
        _session.FrameReady -= OnFrameReady;
        _session.AudioReady -= OnAudioReady;
        _session.StatusLedsChanged -= OnStatusLedsChanged;
        _session.PhoneLedsChanged -= OnPhoneLedsChanged;
    }

    void OnStatusChanged(object? sender, MainViewTextEventArgs eventArgs) => Post(() =>
    {
        string text = eventArgs.Text;
        Status = text;
        if (text.StartsWith("Stopped", StringComparison.Ordinal) ||
            text.StartsWith("Emulator failed", StringComparison.Ordinal))
        {
            IsBootEnabled = true;
            IsStopEnabled = false;
            IsInputEnabled = false;
            Gsm.IsEnabled = false;
            Bluetooth.MarkStopped();
            DisposeAudioOutput();
            CommuniCam.Update();
        }
    });

    void OnFrameReady(object? sender, MainViewFrameEventArgs eventArgs)
    {
        Interlocked.Exchange(ref _pendingFrame, eventArgs);
        QueueFramePresentation();
    }

    void QueueFramePresentation()
    {
        if (Interlocked.Exchange(ref _frameDispatchPending, 1) == 0)
        {
            Post(PresentPendingFrame);
        }
    }

    void PresentPendingFrame()
    {
        var frame = Interlocked.Exchange(ref _pendingFrame, null);
        if (frame is not null)
        {
            FrameReady?.Invoke(this, frame);
        }
        Volatile.Write(ref _frameDispatchPending, 0);
        if (Volatile.Read(ref _pendingFrame) is not null)
        {
            QueueFramePresentation();
        }
    }

    void OnAudioReady(object? sender, MainViewAudioEventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposeStarted) != 0) return;
        var output = Volatile.Read(ref _audioOutput);
        if (output is { AcceptsWorkerThread: true })
        {
            output.PushSamples(eventArgs.Samples.Span);
            return;
        }
        var samples = eventArgs.Samples;
        if (samples.IsEmpty) return;
        if (samples.Length > MaximumPendingAudioSamples)
            samples = samples[^MaximumPendingAudioSamples..].ToArray();
        _pendingAudio.Enqueue(samples);
        Interlocked.Add(ref _pendingAudioSamples, samples.Length);
        while (Volatile.Read(ref _pendingAudioSamples) > MaximumPendingAudioSamples &&
               _pendingAudio.TryDequeue(out var oldest))
            Interlocked.Add(ref _pendingAudioSamples, -oldest.Length);
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            ClearPendingAudio();
            return;
        }
        QueueAudioPresentation();
    }

    void QueueAudioPresentation()
    {
        if (Interlocked.Exchange(ref _audioDispatchPending, 1) == 0)
            Post(PresentPendingAudio);
    }

    void PresentPendingAudio()
    {
        try
        {
            while (_pendingAudio.TryDequeue(out var samples))
            {
                Interlocked.Add(ref _pendingAudioSamples, -samples.Length);
                _audioOutput?.PushSamples(samples.Span);
            }
        }
        finally
        {
            Volatile.Write(ref _audioDispatchPending, 0);
            if (!_pendingAudio.IsEmpty) QueueAudioPresentation();
        }
    }

    void ClearPendingAudio()
    {
        while (_pendingAudio.TryDequeue(out var samples))
            Interlocked.Add(ref _pendingAudioSamples, -samples.Length);
    }

    void OnStatusLedsChanged(object? sender, MainViewStatusLedEventArgs state) => Post(() =>
    {
        IsBluetoothStatusActive = state.BluetoothBlue;
        IsNetworkStatusActive = state.NetworkGreen;
    });

    void OnPhoneLedsChanged(object? sender, MainViewPhoneLedEventArgs state) =>
        Post(() => PhoneLedsChanged?.Invoke(this, state));

    void Post(Action action) => _host.Post(() =>
    {
        if (Volatile.Read(ref _disposeStarted) == 0)
        {
            action();
        }
    });

    void NotifyLifecycleCommandState(object? sender, EventArgs eventArgs)
    {
        BootCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    void DisableSessionControls()
    {
        IsBootEnabled = false;
        IsStopEnabled = false;
        IsInputEnabled = false;
        Gsm.IsEnabled = false;
    }

    void DisposeAudioOutput()
    {
        IMainViewAudioOutput? audioOutput = Interlocked.Exchange(ref _audioOutput, null);
        ClearPendingAudio();
        audioOutput?.Dispose();
    }

    void ReportFailure(string operation, Exception error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(error);
        Status = $"{operation} failed · {error.Message}";
        Diagnostics.AppendError(operation, error);
        _host.ReportError(operation, error);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        DisableSessionControls();
        UnsubscribeFromSession();
        await _operationGate.CloseAsync().ConfigureAwait(false);
        _operationGate.AvailabilityChanged -= NotifyLifecycleCommandState;
        Gsm.Dispose();
        Bluetooth.Dispose();
        Infrared.Dispose();
        Diagnostics.Dispose();
        CommuniCam.Dispose();
        DisposeAudioOutput();
        await _session.DisposeAsync().ConfigureAwait(false);
    }
}
