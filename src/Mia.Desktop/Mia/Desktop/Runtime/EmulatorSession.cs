// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Mia.Emulator;

namespace Mia.Desktop.Runtime;

internal sealed class EmulatorSession : IAsyncDisposable
{
    // Match the verified command-line cadence. Larger frontend grants can
    // overrun the firmware's next dedicated GSM acknowledgement slot.
    const int WorkItemBatch = 16_384;

    readonly DesktopOptions _options;
    readonly IMiaPersistenceStore _persistenceStore;
    readonly IMiaCameraFrameSourceFactory _cameraFrameSourceFactory;
    readonly Channel<byte> _cameraOperationGate = CreateCameraOperationGate();
    CancellationTokenSource? _cancellation;
    Task? _runTask;
    MiaMachine? _machine;
    GdfsOverlayStore? _gdfsOverlayStore;
    MiaPersistenceCoordinator? _persistenceCoordinator;
    MiaCameraFrameSourceLease? _cameraFrameSourceLease;
    EmulatorSessionBluetoothConfiguration _bluetoothConfiguration = new(
        PeerEnabled: true,
        PeerPin: ArmModemBluetoothPeripheral.DefaultEmulatedPeerPin,
        IncomingObjectPushEnabled: true,
        StagedObject: null);
    MiaTransferObject? _stagedInfraredObject;
    int _cameraOperationPending;

    public EmulatorSession(
        DesktopOptions options,
        IMiaPersistenceStore? persistenceStore = null,
        IMiaCameraFrameSourceFactory? cameraFrameSourceFactory = null)
    {
        _options = options;
        _persistenceStore = persistenceStore ?? FileMiaPersistenceStore.Default;
        _cameraFrameSourceFactory = cameraFrameSourceFactory ??
            PlatformCameraFrameSourceFactory.Default;
    }

    public event Action<string>? StatusChanged;

    public event Action<string>? OutputReceived;

    public event Action<string>? GsmMessageEmitted;

    public event Action<ReadOnlyMemory<byte>>? FrameReady;

    public event Action<ReadOnlyMemory<short>>? AudioReady;

    public event Action<MiaStatusLedState>? StatusLedsChanged;

    public event Action<MiaPhoneLedState>? PhoneLedsChanged;

    public event Action<MiaBluetoothEmulationStatus>? BluetoothStatusChanged;

    public event Action<MiaInfraredTransferStatus>? InfraredStatusChanged;

    public event Action<MiaCommuniCamStatus>? CommuniCamStatusChanged;

    public event Action<bool>? CommuniCamConnectionChanged;

    public bool IsRunning =>
        _runTask is { IsCompleted: false } &&
        Volatile.Read(ref _machine) is { IsStopped: false };

    public bool IsCommuniCamConnected =>
        Volatile.Read(ref _cameraFrameSourceLease) is not null;

    public bool IsCommuniCamOperationPending =>
        Volatile.Read(ref _cameraOperationPending) != 0;

    public bool IsHostCameraAvailable => _cameraFrameSourceFactory.IsAvailable;

    public string? HostCameraUnavailableReason =>
        _cameraFrameSourceFactory.UnavailableReason;

    public bool PaceToRealTime => _options.PaceToRealTime;

    public async Task StartAsync()
    {
        await StopAsync().ConfigureAwait(true);
        EmulatorSessionStartupInputs inputs = await LoadStartupInputsAsync()
            .ConfigureAwait(true);
        PublishRestoredInputs(inputs);
        MiaMachine machine = await CreateMachineAsync(inputs).ConfigureAwait(true);
        ConfigureMachine(machine);
        Volatile.Write(ref _machine, machine);
        PublishInitialState(machine);
        _gdfsOverlayStore = inputs.GdfsOverlayStore;
        MiaPersistenceCoordinator persistenceCoordinator =
            ConfigurePersistence(machine, inputs);
        _persistenceCoordinator = persistenceCoordinator;
        StartRunLoop(machine, persistenceCoordinator);
        await Task.Yield();
    }

    async Task<EmulatorSessionStartupInputs> LoadStartupInputsAsync()
    {
        ValidateInputs();
        byte[] otp = Convert.FromHexString(_options.FlashUserOtp);
        var gdfsOverlayStore = new GdfsOverlayStore(
            _options.GdfsPath,
            _options.GdfsOverlayPath);
        Task<byte[]> firmwareRead = File.ReadAllBytesAsync(_options.FirmwarePath);
        Task<GdfsOverlayLoad> gdfsRead = gdfsOverlayStore.LoadAsync(
            _options.IgnoreGdfsOverlay);
        Task<byte[]> modemRead = File.ReadAllBytesAsync(_options.ModemPath);
        await Task.WhenAll(firmwareRead, gdfsRead, modemRead).ConfigureAwait(true);
        byte[] firmware = await firmwareRead.ConfigureAwait(true);
        GdfsOverlayLoad gdfsLoad = await gdfsRead.ConfigureAwait(true);
        byte[] modem = await modemRead.ConfigureAwait(true);
        string persistenceKey = MiaPersistence.CreateKey(firmware);
        MiaPersistenceSnapshot persistenceSnapshot =
            await _persistenceStore.LoadAsync(
                persistenceKey,
                CancellationToken.None).ConfigureAwait(true) ??
            MiaPersistenceSnapshot.Empty;
        return new(
            firmware,
            gdfsLoad.RawImage.ToArray(),
            modem,
            otp,
            gdfsOverlayStore,
            gdfsLoad,
            persistenceKey,
            persistenceSnapshot);
    }

    void ValidateInputs()
    {
        string[] missing = _options.MissingInputs().ToArray();
        if (missing.Length > 0)
        {
            throw new FileNotFoundException(
                "Required emulator input is missing: " +
                string.Join(", ", missing));
        }
    }

    void PublishRestoredInputs(EmulatorSessionStartupInputs inputs)
    {
        if (inputs.GdfsLoad.RecoveryMessage is { } recoveryMessage)
        {
            OutputReceived?.Invoke(recoveryMessage);
        }
        OutputReceived?.Invoke(inputs.GdfsLoad.FromOverlay
            ? $"GDFS overlay: restored {inputs.GdfsLoad.ChangedBlockCount:n0} changed NOR block(s) " +
                $"from {_options.GdfsOverlayPath}"
            : $"GDFS overlay: starting from pristine {_options.GdfsPath}");
        OutputReceived?.Invoke(
            $"SIM persistence: restored " +
            $"{inputs.PersistenceSnapshot.SimFiles.Length:n0} changed file(s); " +
            $"key={inputs.PersistenceKey}");
    }

    static Task<MiaMachine> CreateMachineAsync(
        EmulatorSessionStartupInputs inputs) => Task.Run(() => new MiaMachine(
            inputs.Firmware,
            inputs.Gdfs,
            inputs.Modem,
            inputs.Otp,
            virtualSim: true,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel,
            persistenceSnapshot: inputs.PersistenceSnapshot,
            liveGsm: new MiaLiveGsmOptions()));

    void ConfigureMachine(MiaMachine machine)
    {
        MiaLiveGsmController liveGsm = machine.LiveGsm ??
            throw new InvalidOperationException(
            "Could not create the live GSM controller.");
        machine.BluetoothEmulationStatusChanged += state =>
            BluetoothStatusChanged?.Invoke(state);
        machine.InfraredObjectPeer!.StatusChanged += status =>
            InfraredStatusChanged?.Invoke(status);
        machine.CommuniCamStatusChanged += status =>
        {
            CommuniCamStatusChanged?.Invoke(status);
            OutputReceived?.Invoke(status.Message);
        };
        ApplyBluetoothConfiguration(machine);
        machine.StageEmulatedInfraredObject(_stagedInfraredObject);
        liveGsm.MessageEmitted += message =>
        {
            OutputReceived?.Invoke(message);
            GsmMessageEmitted?.Invoke(message);
        };
        machine.InteractiveInput.DeferredReleaseApplied += contact =>
            OutputReceived?.Invoke(
                $"Keypad input: cycle={machine.Cycles:n0} " +
                $"scan=0x{contact.ScanMask:x2} row=0x{contact.RowMask:x2} " +
                "up applied after firmware MMIO observation");
        machine.FramePublished += frame => FrameReady?.Invoke(frame);
        machine.StatusIndicators.StateChanged += state =>
            StatusLedsChanged?.Invoke(state);
        StatusLedsChanged?.Invoke(machine.StatusIndicators.State);
        machine.PhoneLeds.StateChanged += state =>
            PhoneLedsChanged?.Invoke(state);
        PhoneLedsChanged?.Invoke(machine.PhoneLeds.State);
    }

    void PublishInitialState(MiaMachine machine)
    {
        BluetoothStatusChanged?.Invoke(machine.GetBluetoothEmulationStatus());
        InfraredStatusChanged?.Invoke(machine.GetInfraredTransferStatus());
        CommuniCamStatusChanged?.Invoke(machine.GetCommuniCamStatus());
    }

    MiaPersistenceCoordinator ConfigurePersistence(
        MiaMachine machine,
        EmulatorSessionStartupInputs inputs)
    {
        var persistenceCoordinator = new MiaPersistenceCoordinator(new(
            inputs.PersistenceKey,
            _persistenceStore,
            inputs.PersistenceSnapshot));
        persistenceCoordinator.SnapshotSaved += snapshot =>
            OutputReceived?.Invoke(
                $"SIM persistence: saved {snapshot.SimFiles.Length:n0} changed file(s)");
        persistenceCoordinator.SaveFailed += error =>
            OutputReceived?.Invoke($"SIM persistence save failed: {error.Message}");
        persistenceCoordinator.MarkLoaded(machine);
        machine.PersistenceChanged += _ =>
            persistenceCoordinator.ScheduleSave(machine);
        return persistenceCoordinator;
    }

    void StartRunLoop(
        MiaMachine machine,
        MiaPersistenceCoordinator persistenceCoordinator)
    {
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        StatusChanged?.Invoke(
            _options.PaceToRealTime
                ? "Booting · AVR 12 MHz · ASIC/ARM 13 MHz"
                : "Booting · unthrottled coarse parallel AVR + ARM");
        _runTask = Task.Run(() => Run(
            machine,
            persistenceCoordinator,
            cancellation.Token));
    }

    public void SetKey(
        byte scanMask,
        byte rowMask,
        byte? secondaryScanMask,
        bool pressed)
    {
        var machine = Volatile.Read(ref _machine);
        if (machine is null)
        {
            return;
        }

        _ = ApplyInputAsync(machine, new(
            scanMask,
            rowMask,
            secondaryScanMask,
            Power: false,
            pressed));
    }

    public void SetPowerKey(bool pressed)
    {
        var machine = Volatile.Read(ref _machine);
        if (machine is null)
        {
            return;
        }

        _ = ApplyInputAsync(machine, new(
            AsicKeypad.NoPowerScanMask,
            AsicKeypad.NoPowerRowMask,
            SecondaryScanMask: null,
            Power: true,
            pressed));
    }

    public async Task<bool> PressKeyAsync(
        byte scanMask,
        byte rowMask,
        byte? secondaryScanMask = null)
    {
        var machine = Volatile.Read(ref _machine);
        if (machine is null)
        {
            return false;
        }

        await ApplyInputAsync(machine, new(
            scanMask,
            rowMask,
            secondaryScanMask,
            Power: false,
            Pressed: true)).ConfigureAwait(true);
        await Task.Delay(50).ConfigureAwait(true);
        await ApplyInputAsync(machine, new(
            scanMask,
            rowMask,
            secondaryScanMask,
            Power: false,
            Pressed: false)).ConfigureAwait(true);
        return true;
    }

    public async Task<bool> PressNoKeyAsync()
    {
        var machine = Volatile.Read(ref _machine);
        if (machine is null)
        {
            return false;
        }

        await ApplyInputAsync(machine, new(
            AsicKeypad.NoPowerScanMask,
            AsicKeypad.NoPowerRowMask,
            SecondaryScanMask: null,
            Power: true,
            Pressed: true)).ConfigureAwait(true);
        var releaseCycle = machine.Cycles + 4_000_000;
        try
        {
            await machine.WaitUntilCycleAsync(
                releaseCycle,
                _cancellation?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        if (ReferenceEquals(machine, Volatile.Read(ref _machine)) &&
            !machine.IsStopped)
        {
            await ApplyInputAsync(machine, new(
                AsicKeypad.NoPowerScanMask,
                AsicKeypad.NoPowerRowMask,
                SecondaryScanMask: null,
                Power: true,
                Pressed: false)).ConfigureAwait(true);
        }
        return true;
    }

    public MiaLiveGsmStatus GetGsmStatus()
    {
        var machine = Volatile.Read(ref _machine);
        return machine?.LiveGsm?.GetStatus(machine) ?? MiaLiveGsmStatus.Stopped;
    }

    public byte[] GetFrameSnapshot()
    {
        var machine = Volatile.Read(ref _machine);
        return machine?.Frame.ToArray() ?? [];
    }

    public bool TryQueueIncomingSms(string originator, string text, out string result)
    {
        var liveGsm = Volatile.Read(ref _machine)?.LiveGsm;
        if (liveGsm is null)
        {
            result = "The emulator is not running.";
            return false;
        }
        return liveGsm.TryQueueIncomingSms(originator, text, out result);
    }

    public bool TryQueueIncomingCall(
        string originator,
        bool autoAnswer,
        out string result)
    {
        var liveGsm = Volatile.Read(ref _machine)?.LiveGsm;
        if (liveGsm is null)
        {
            result = "The emulator is not running.";
            return false;
        }
        return liveGsm.TryQueueIncomingCall(originator, autoAnswer, out result);
    }

    public bool TrySetCarrierRawSample(int rawSample, out string result)
    {
        var liveGsm = Volatile.Read(ref _machine)?.LiveGsm;
        if (liveGsm is null)
        {
            result = "The emulator is not running.";
            return false;
        }
        if (rawSample is < ushort.MinValue or > ushort.MaxValue)
        {
            result = "rawSample must be between 0 and 65535.";
            return false;
        }

        liveGsm.CarrierRawSample = (ushort)rawSample;
        result = $"Serving-carrier raw RSSI set to 0x{rawSample:x4}.";
        return true;
    }

    public bool TryConfigureBluetoothPeer(
        bool enabled,
        string pin,
        bool incomingObjectPushEnabled,
        out string result)
    {
        if (string.IsNullOrEmpty(pin) ||
            pin.Length > 10 ||
            pin.Any(character => character is < '0' or > '9'))
        {
            result = "Pairing PIN must contain 1 to 10 digits.";
            return false;
        }

        var previousConfiguration = Volatile.Read(ref _bluetoothConfiguration);
        Volatile.Write(ref _bluetoothConfiguration, previousConfiguration with
        {
            PeerEnabled = enabled,
            PeerPin = pin,
            IncomingObjectPushEnabled = incomingObjectPushEnabled,
        });

        var machine = Volatile.Read(ref _machine);
        if (machine is null)
        {
            result = enabled
                ? "Emulated peer settings saved for the next boot."
                : "Emulated peer will remain disabled on the next boot.";
            return true;
        }

        try
        {
            machine.ConfigureEmulatedBluetoothPeer(
                enabled,
                pin,
                incomingObjectPushEnabled);
            BluetoothStatusChanged?.Invoke(machine.GetBluetoothEmulationStatus());
            result = enabled
                ? $"Emulated peer ready · PIN {pin} · pair from the handset."
                : "Emulated Bluetooth peer disabled.";
            return true;
        }
        catch (ObjectDisposedException)
        {
            result = "Emulated peer settings saved for the next boot.";
            return true;
        }
    }

    public bool TryStageBluetoothObject(
        string name,
        string mediaType,
        ReadOnlyMemory<byte> data,
        out string result)
    {
        name = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
        {
            result = "Bluetooth file names must contain 1 to 64 characters.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(mediaType) ||
            mediaType.Length > 64 ||
            mediaType.Any(character => character > 0x7f))
        {
            result = "Bluetooth media types must contain 1 to 64 ASCII characters.";
            return false;
        }

        var transferObject = new MiaTransferObject(name, mediaType, data.ToArray());
        var previousConfiguration = Volatile.Read(ref _bluetoothConfiguration);
        Volatile.Write(ref _bluetoothConfiguration, previousConfiguration with
        {
            StagedObject = transferObject,
            IncomingObjectPushEnabled = true,
        });

        try
        {
            Volatile.Read(ref _machine)?.StageEmulatedBluetoothObject(transferObject);
        }
        catch (ObjectDisposedException)
        {
        }
        result =
            $"Staged {name} · {data.Length:n0} B. Send an item from the handset " +
            "to Mia Peer; the peer returns this file on the same native link.";
        return true;
    }

    public bool TryGetBluetoothReceivedObject(
        out MiaTransferObject value,
        out string result)
    {
        try
        {
            MiaTransferObject? received =
                Volatile.Read(ref _machine)?.GetBluetoothReceivedObject();
            if (received is not { } available)
            {
                value = default;
                result = "No Bluetooth object has been received from the handset.";
                return false;
            }

            value = available.Snapshot();
            result = $"Bluetooth received {value.Name} · {value.Data.Length:n0} B.";
            return true;
        }
        catch (ObjectDisposedException)
        {
            value = default;
            result = "The emulator stopped before the Bluetooth object was read.";
            return false;
        }
    }

    public bool TryStageInfraredObject(MiaTransferObject value, out string result)
    {
        var machine = Volatile.Read(ref _machine);
        if (machine is null)
        {
            result = "The emulator is not running.";
            return false;
        }
        try
        {
            _stagedInfraredObject = value.Snapshot();
            machine.StageEmulatedInfraredObject(_stagedInfraredObject);
            result =
                $"Staged {value.Name} · {value.Data.Length:n0} B for IrDA. " +
                "Open Connect → Receive item on the handset.";
            return true;
        }
        catch (Exception error) when (
            error is ObjectDisposedException or InvalidOperationException)
        {
            result = $"Could not stage the IrDA object: {error.Message}";
            return false;
        }
    }

    public bool TryGetInfraredReceivedObject(
        out MiaTransferObject value,
        out string result)
    {
        try
        {
            MiaTransferObject? received =
                Volatile.Read(ref _machine)?.GetInfraredReceivedObject();
            if (received is not { } available)
            {
                value = default;
                result = "No IrDA object has been received from the handset.";
                return false;
            }

            value = available.Snapshot();
            result = $"IrDA received {value.Name} · {value.Data.Length:n0} B.";
            return true;
        }
        catch (ObjectDisposedException)
        {
            value = default;
            result = "The emulator stopped before the IrDA object was read.";
            return false;
        }
    }

    public async Task ConnectCommuniCamAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginCameraOperation();
        (bool Connected, string? DisplayName) connection;
        try
        {
            connection = await ConnectCommuniCamCoreAsync(cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            EndCameraOperation();
        }
        PublishCommuniCamConnection(connection);
    }

    void BeginCameraOperation()
    {
        if (!_cameraOperationGate.Reader.TryRead(out _))
        {
            throw new InvalidOperationException(
                "A CommuniCam connection operation is already active.");
        }
        Volatile.Write(ref _cameraOperationPending, 1);
    }

    void EndCameraOperation()
    {
        Volatile.Write(ref _cameraOperationPending, 0);
        _cameraOperationGate.Writer.TryWrite(0);
    }

    async Task<(bool Connected, string? DisplayName)> ConnectCommuniCamCoreAsync(
        CancellationToken cancellationToken)
    {
        if (IsCommuniCamConnected)
        {
            return default;
        }

        ValidateCameraAvailability();
        (MiaMachine machine, CancellationToken sessionCancellation) =
            GetRunningSession();
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                sessionCancellation);
        MiaCameraFrameSourceLease lease =
            await MiaCameraFrameSourceLease.CreateAsync(
                _cameraFrameSourceFactory,
                linkedCancellation.Token)
            .ConfigureAwait(true);
        lease.BackendFailed += error => OutputReceived?.Invoke(
            $"CommuniCam: host camera failed: {error.Message}");
        await AttachCameraLeaseAsync(machine, lease, sessionCancellation)
            .ConfigureAwait(true);
        Volatile.Write(ref _cameraFrameSourceLease, lease);
        return (true, lease.Source.DisplayName);
    }

    void ValidateCameraAvailability()
    {
        if (!_cameraFrameSourceFactory.IsAvailable)
        {
            throw new PlatformNotSupportedException(
                _cameraFrameSourceFactory.UnavailableReason);
        }
    }

    (MiaMachine Machine, CancellationToken CancellationToken) GetRunningSession()
    {
        CancellationTokenSource? cancellation = Volatile.Read(ref _cancellation);
        MiaMachine? machine = Volatile.Read(ref _machine);
        if (machine is null || !IsRunning || machine.IsStopped ||
            cancellation is null || cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException("Start the emulator first.");
        }
        return (machine, cancellation.Token);
    }

    async Task AttachCameraLeaseAsync(
        MiaMachine machine,
        MiaCameraFrameSourceLease lease,
        CancellationToken sessionCancellation)
    {
        try
        {
            ValidateCameraSession(machine, sessionCancellation);
            machine.ConnectCommuniCam(lease.Source);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(true);
            throw;
        }
    }

    void ValidateCameraSession(
        MiaMachine machine,
        CancellationToken sessionCancellation)
    {
        if (!ReferenceEquals(machine, Volatile.Read(ref _machine)) ||
            machine.IsStopped || sessionCancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "The emulator stopped while the CommuniCam was connecting.");
        }
    }

    void PublishCommuniCamConnection(
        (bool Connected, string? DisplayName) connection)
    {
        if (connection.Connected)
        {
            CommuniCamConnectionChanged?.Invoke(true);
            OutputReceived?.Invoke(
                $"CommuniCam: connected to {connection.DisplayName}.");
        }
    }

    public async Task DisconnectCommuniCamAsync()
    {
        BeginCameraOperation();
        bool wasConnected = IsCommuniCamConnected;
        try
        {
            await DisconnectCommuniCamCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            EndCameraOperation();
            PublishCommuniCamDisconnectedIf(wasConnected);
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation = _cancellation;
        Task? runTask = _runTask;
        bool hadSession =
            cancellation is not null || runTask is not null || _machine is not null;
        _runTask = null;
        await CancelRunAsync(cancellation).ConfigureAwait(true);
        _cancellation = null;
        await ObserveRunTaskAsync(runTask, cancellation).ConfigureAwait(true);
        await DisconnectCameraForStopAsync().ConfigureAwait(true);
        MiaMachine? machine = Interlocked.Exchange(ref _machine, null);
        GdfsOverlayStore? gdfsOverlayStore = Interlocked.Exchange(
            ref _gdfsOverlayStore,
            null);
        MiaPersistenceCoordinator? persistenceCoordinator = Interlocked.Exchange(
            ref _persistenceCoordinator,
            null);
        await StopMachineAsync(
            machine,
            persistenceCoordinator,
            gdfsOverlayStore).ConfigureAwait(true);
        PublishStoppedState(hadSession);
    }

    public Task StopForRestartAsync() => StopAsync();

    static async Task CancelRunAsync(CancellationTokenSource? cancellation)
    {
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            cancellation.Dispose();
        }
    }

    static async Task ObserveRunTaskAsync(
        Task? runTask,
        CancellationTokenSource? cancellation)
    {
        if (runTask is null)
        {
            return;
        }

        try
        {
            await runTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (
            cancellation?.IsCancellationRequested == true)
        {
        }
    }

    async Task DisconnectCameraForStopAsync()
    {
        _ = await _cameraOperationGate.Reader.ReadAsync().ConfigureAwait(true);
        Volatile.Write(ref _cameraOperationPending, 1);
        bool wasCommuniCamConnected = IsCommuniCamConnected;
        try
        {
            await DisconnectCommuniCamCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            Volatile.Write(ref _cameraOperationPending, 0);
            _cameraOperationGate.Writer.TryWrite(0);
            PublishCommuniCamDisconnectedIf(wasCommuniCamConnected);
        }
    }

    async Task StopMachineAsync(
        MiaMachine? machine,
        MiaPersistenceCoordinator? persistenceCoordinator,
        GdfsOverlayStore? gdfsOverlayStore)
    {
        if (machine is null)
        {
            return;
        }

        await FlushPersistenceAsync(machine, persistenceCoordinator)
            .ConfigureAwait(true);
        try
        {
            await SaveGdfsOverlayAsync(machine, gdfsOverlayStore)
                .ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or
            UnauthorizedAccessException)
        {
            OutputReceived?.Invoke(
                $"GDFS overlay save failed: {error.Message}");
        }
        finally
        {
            machine.Dispose();
        }
    }

    async Task FlushPersistenceAsync(
        MiaMachine machine,
        MiaPersistenceCoordinator? persistenceCoordinator)
    {
        if (persistenceCoordinator is null)
        {
            return;
        }

        try
        {
            await persistenceCoordinator.FlushAsync(machine).ConfigureAwait(true);
        }
        catch (IOException error)
        {
            OutputReceived?.Invoke(
                $"SIM persistence flush failed: {error.Message}");
        }
    }

    async Task SaveGdfsOverlayAsync(
        MiaMachine machine,
        GdfsOverlayStore? gdfsOverlayStore)
    {
        if (gdfsOverlayStore is null)
        {
            return;
        }

        byte[] currentGdfs = await Task.Run(machine.CaptureGdfsImage)
            .ConfigureAwait(true);
        GdfsOverlaySave saved = await gdfsOverlayStore.SaveAsync(currentGdfs)
            .ConfigureAwait(true);
        OutputReceived?.Invoke(
            $"GDFS overlay: saved {saved.ChangedBlockCount:n0} changed NOR block(s), " +
            $"{saved.FileLength:n0} bytes to {_options.GdfsOverlayPath}");
    }

    void PublishStoppedState(bool hadSession)
    {
        if (hadSession)
        {
            StatusLedsChanged?.Invoke(default);
            PhoneLedsChanged?.Invoke(default);
            StatusChanged?.Invoke("Stopped");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(true);

    async Task DisconnectCommuniCamCoreAsync()
    {
        MiaCameraFrameSourceLease? lease = Interlocked.Exchange(
            ref _cameraFrameSourceLease,
            null);
        if (lease is null)
        {
            return;
        }

        try
        {
            Volatile.Read(ref _machine)?.DisconnectCommuniCam();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(true);
        }
    }

    void PublishCommuniCamDisconnected()
    {
        CommuniCamConnectionChanged?.Invoke(false);
        CommuniCamStatusChanged?.Invoke(MiaCommuniCamStatus.Disconnected);
        OutputReceived?.Invoke("CommuniCam: disconnected.");
    }

    void PublishCommuniCamDisconnectedIf(bool wasConnected)
    {
        if (wasConnected)
        {
            PublishCommuniCamDisconnected();
        }
    }

    static Channel<byte> CreateCameraOperationGate()
    {
        Channel<byte> gate = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        if (!gate.Writer.TryWrite(0))
        {
            throw new InvalidOperationException(
                "Could not initialize the host-camera operation gate.");
        }
        return gate;
    }

    void ApplyBluetoothConfiguration(MiaMachine machine)
    {
        var configuration = Volatile.Read(ref _bluetoothConfiguration);
        machine.ConfigureEmulatedBluetoothPeer(
            configuration.PeerEnabled,
            configuration.PeerPin,
            configuration.IncomingObjectPushEnabled);
        if (configuration.StagedObject.HasValue)
        {
            machine.StageEmulatedBluetoothObject(configuration.StagedObject);
        }
    }

    void Run(
        MiaMachine machine,
        MiaPersistenceCoordinator persistenceCoordinator,
        CancellationToken cancellationToken)
    {
        var pacer = new RealTimePacer();
        pacer.Reanchor(machine.Cycles);
        EmulatorSessionAudioPipeline? audio = TryCreateAudioPipeline(machine);
        try
        {
            while (!cancellationToken.IsCancellationRequested && !machine.IsStopped)
            {
                machine.RunWorkItems(WorkItemBatch);
                audio = DrainAudio(audio, machine.Cycles);
                Pace(pacer, machine.Cycles, cancellationToken);
            }

            PublishMachineStop(machine, persistenceCoordinator);
        }
        catch (Exception error) when (error is InvalidOperationException or
            IOException or ObjectDisposedException)
        {
            StatusChanged?.Invoke($"Emulator failed · {error.Message}");
            OutputReceived?.Invoke(error.ToString());
        }
        finally
        {
            audio?.Dispose();
        }
    }

    EmulatorSessionAudioPipeline? TryCreateAudioPipeline(MiaMachine machine)
    {
        if (!_options.PaceToRealTime)
        {
            return null;
        }

        try
        {
            var audio = new EmulatorSessionAudioPipeline(machine, samples =>
                AudioReady?.Invoke(samples));
            OutputReceived?.Invoke(
                $"Audio: firmware tone generator · {audio.SampleRate:n0} Hz mono");
            return audio;
        }
        catch (Exception error) when (error is DllNotFoundException or
            BadImageFormatException or InvalidOperationException or NotSupportedException)
        {
            OutputReceived?.Invoke(
                $"Audio output unavailable; emulation will continue: {error.Message}");
            return null;
        }
    }

    EmulatorSessionAudioPipeline? DrainAudio(
        EmulatorSessionAudioPipeline? audio,
        long targetCycle)
    {
        try
        {
            audio?.Drain(targetCycle);
            return audio;
        }
        catch (Exception error) when (error is InvalidOperationException or
            ObjectDisposedException)
        {
            audio?.Dispose();
            OutputReceived?.Invoke(
                $"Audio output stopped; emulation will continue: {error.Message}");
            return null;
        }
    }

    void Pace(
        RealTimePacer pacer,
        long targetCycle,
        CancellationToken cancellationToken)
    {
        if (_options.PaceToRealTime)
        {
            pacer.Pace(targetCycle, cancellationToken);
        }
    }

    void PublishMachineStop(
        MiaMachine machine,
        MiaPersistenceCoordinator persistenceCoordinator)
    {
        if (machine.IsStopped)
        {
            persistenceCoordinator.ScheduleSave(machine, force: true);
            StatusChanged?.Invoke($"Stopped · {machine.StopReason}");
            OutputReceived?.Invoke(machine.StopReason ?? "Emulator stopped.");
        }
    }

    async Task ApplyInputAsync(MiaMachine machine, EmulatorSessionInputCommand command)
    {
        try
        {
            InteractiveKeypadTransition transition = command.Power
                ? await machine.SetPowerKeyAsync(command.Pressed).ConfigureAwait(true)
                : await machine.SetKeyAsync(
                    command.ScanMask,
                    command.RowMask,
                    command.SecondaryScanMask,
                    command.Pressed).ConfigureAwait(true);
            OutputReceived?.Invoke(
                $"Keypad input: cycle={machine.Cycles:n0} " +
                $"{(command.Power ? "power" : "key")} " +
                $"{(command.Pressed ? "down" : "up")} {transition.ToString().ToUpperInvariant()}");
        }
        catch (ObjectDisposedException)
        {
            // Session shutdown can retire the machine after an input edge was queued.
        }
    }
}
