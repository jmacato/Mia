// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using AvrCore;

namespace Mia.Emulator.Machine;

/// <summary>
/// Reusable, host-agnostic Mia machine for interactive front ends. All input
/// images are supplied as bytes; firmware-visible behavior still crosses the
/// same ROM ABI, MMIO, register, IRQ, and serial-link boundaries as the CLI.
/// </summary>
internal sealed class MiaMachine : IDisposable
{
    public const int ProgramSize = 0x800000;
    public const int DataSize = 0x1000000;
    const int PhysicalDataSize = DataSize - 0x100;
    public const long DefaultPowerKeyReleaseCycle = 30_000_000;
#if !BROWSER || BROWSER_THREADS
    const int CoarseParallelQuantum = 16_384;
#endif
    const int IdleLoopInstructionCount = 19;
    const int IdleLoopCycleCount = 23;
    const int MaximumIdleAdvanceAsicCycles = 60_000;
#if !BROWSER || BROWSER_THREADS
    const int MaximumInteractiveGrantAsicCycles = 60_000;
#endif
    readonly ArmModemLinkBridge? _modemLinkBridge;
    readonly List<MiaWorker> _workers = [];
    readonly MiaWorker _avrWorker;
    readonly MiaWorker? _armWorker;
    readonly MiaWorker _inputWorker;
    readonly MiaWorker _displayWorker;
    readonly MiaWorker _byteChannelWorker;
    readonly MiaWorker _hostOutputWorker;
    readonly bool _dedicatedWorkers;
    readonly int _idleLoopStartWord;
    readonly bool _gdfsSectorHeaderFastPathAvailable;
    readonly bool _gdfsUnitStateFastPathAvailable;
#if !BROWSER || BROWSER_THREADS
    readonly bool _coarseParallel;
#endif
    byte[] _frame = new byte[S4595Display.Width * S4595Display.Height];
#if !BROWSER || BROWSER_THREADS
    long _coarseCyclesPerWorkItemQ16 = 5L << 14;
    long _coarseGrantCount;
    long _armGrantReadyCount;
    long _armCatchupCount;
#endif
    int _frameVersion;
    bool _disposed;
    bool _firmwareFastPathsBlockedByDiagnostics;
    IMiaExecutionObserver? _executionObserver;
    Dictionary<int, ExecutionHotspotCounts>? _executionHotspots;
    bool _idleFastForwardDiagnosticsEnabled;
    long _idleFastForwardCandidateCount;
    long _idleFastForwardSuccessCount;
    long _idleFastForwardedInstructionsAtDiagnosticsStart;
    MiaIdleFastForwardBlockReason _lastIdleFastForwardBlockReason;
    long[]? _idleFastForwardBlockCounts;

    public MiaMachine(
        ReadOnlySpan<byte> firmwareImage,
        ReadOnlySpan<byte> gdfsImage = default,
        ReadOnlySpan<byte> modemBih = default,
        ReadOnlySpan<byte> flashUserOtp = default,
        bool virtualSim = true,
        bool powerPressedInitially = true,
        long powerKeyReleaseCycle = DefaultPowerKeyReleaseCycle,
        MiaCoreSchedulingMode coreSchedulingMode = MiaCoreSchedulingMode.CycleLocked,
        MiaPersistenceSnapshot? persistenceSnapshot = null,
        IAsicRfSignalSource? rfSignalSource = null,
        IAsicFchSource? fchSource = null,
        IAsicEqualizerSource? equalizerSource = null,
        IAsicChannelDecoderSource? channelDecoderSource = null,
        IAsicChannelEncoderSink? channelEncoderSink = null,
        MiaLiveGsmOptions? liveGsm = null,
        MiaSecondaryPortProfile? secondaryPortProfile = null,
        bool externalPowerConnectedInitially = false,
        byte powerPortSiliconRevision =
            MiaPowerPortController.LegacySiliconRevision,
        bool dedicatedWorkers = true,
        Cpu? preparedCpu = null,
        ArmModem? preparedModem = null)
    {
        if (flashUserOtp.Length is not (0 or AsicFlashMemory.UserProtectionRegisterLength))
        {
            throw new ArgumentException(
                $"Flash user OTP must contain exactly " +
                $"{AsicFlashMemory.UserProtectionRegisterLength} bytes.",
                nameof(flashUserOtp));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(powerKeyReleaseCycle);
        if (!Enum.IsDefined(coreSchedulingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(coreSchedulingMode));
        }

        byte[] program = preparedCpu?.ProgBytes ?? BuildProgram(
            firmwareImage,
            gdfsImage);
        if (program.Length != ProgramSize)
        {
            throw new ArgumentException(
                $"Prepared program must contain exactly 0x{ProgramSize:x} bytes.",
                nameof(preparedCpu));
        }
        _idleLoopStartWord =
            MiaFirmwareRuntimeHooks.ResolveIdleLoopStartWord(program);
        _gdfsSectorHeaderFastPathAvailable =
            MiaFirmwareRuntimeHooks.HasKnownGdfsSectorHeaderLoop(
                program);
        _gdfsUnitStateFastPathAvailable =
            MiaFirmwareRuntimeHooks.HasKnownGdfsUnitStateLoop(program);
#if BROWSER && !BROWSER_THREADS
        if (coreSchedulingMode == MiaCoreSchedulingMode.CoarseParallel)
        {
            throw new PlatformNotSupportedException(
                "Coarse parallel scheduling requires host threads.");
        }
#endif

        _dedicatedWorkers = dedicatedWorkers;
        Cpu = preparedCpu ?? CreateCpu(program);
        if (liveGsm is not null &&
            (rfSignalSource is not null ||
             fchSource is not null ||
             equalizerSource is not null ||
             channelDecoderSource is not null ||
             channelEncoderSink is not null))
        {
            throw new ArgumentException(
                "Live GSM cannot be combined with separately supplied GSM/RF sources.",
                nameof(liveGsm));
        }
        // Decode and encode already had to serialize on the cell's owner
        // whenever GSM traffic touched shared state, so sharing one worker
        // across both plus the GSM controller trades away no real
        // parallelism while turning every cell call from these two
        // peripherals into a same-thread, synchronization-free call.
        var channelDecoderWorker = CreateWorker(liveGsm is null
            ? "channel decoder peripheral"
            : "channel decoder/PH command/GSM controller peripheral");
        var phCommandWorker = liveGsm is null
            ? CreateWorker("PH command peripheral")
            : channelDecoderWorker;
        LiveGsm = liveGsm is null
            ? null
            : new MiaLiveGsmController(Cpu, liveGsm, channelDecoderWorker);
        if (LiveGsm is not null)
        {
            rfSignalSource = LiveGsm;
            fchSource = LiveGsm.Cell;
            channelDecoderSource = LiveGsm.Cell;
            channelEncoderSink = LiveGsm.Cell;
        }
        Clock = new MiaSystemClock();
        StatusIndicators = new MiaStatusIndicatorController();
        _avrWorker = CreateWorker("AVR core");
        var interruptWorker = CreateWorker("interrupt fabric peripheral");
        var flashWorker = CreateWorker("NOR flash peripheral");
        var rtcWorker = CreateWorker("RTC peripheral");
        var sedWorker = CreateWorker("SED timer peripheral");
        var simWorker = CreateWorker("SIM UART peripheral");
        var simCardWorker = CreateWorker("SIM card peripheral");
        var highPriorityRequestWorker = CreateWorker("high-priority request peripheral");
        var interruptRouterWorker = CreateWorker("interrupt router peripheral");
        var firmwareControllerWorker = CreateWorker("firmware controller peripheral");
        var commandPortWorker = CreateWorker("graphics command peripheral");
        var timeGeneratorWorker = CreateWorker("time-generator peripheral");
        var rfWorker = CreateWorker("RF frontend peripheral");
        var fchWorker = CreateWorker("FCH detector peripheral");
        var adcWorker = CreateWorker("ADC peripheral");
        var equalizerWorker = CreateWorker("equalizer peripheral");
        var transferControllerWorker = CreateWorker("transfer-controller peripheral");
        var primaryI2cWorker = CreateWorker("primary I2C peripheral");
        var displayI2cWorker = CreateWorker("display I2C peripheral");
        _displayWorker = CreateWorker("LCD peripheral");
        _byteChannelWorker = CreateWorker("ASIC byte-channel peripheral");
        var powerWorker = CreateWorker("power-port peripheral");
        var secondaryPortWorker = CreateWorker("secondary-port peripheral");
        _inputWorker = CreateWorker("keypad/input peripheral");
        var traceWorker = CreateWorker("trace-port peripheral");
        _hostOutputWorker = CreateWorker("host output");
        AsicExecutableRam.Attach(Cpu);
        FlashMemory = new AsicFlashMemory(Cpu, flashUserOtp, flashWorker);
        InterruptController = new AsicInterruptController(Cpu, interruptWorker);
        Rtc = new AsicRtc(Cpu, Clock, InterruptController, rtcWorker);
        HighPriorityRequests = new AsicHighPriorityRequests(Cpu, InterruptController);
        PowerPorts = new MiaPowerPortController(
            InterruptController,
            powerPressedInitially,
            externalPowerConnectedInitially,
            powerPortSiliconRevision,
            worker: powerWorker);
        PhoneLeds = new MiaPhoneLedController(Cpu, PowerPorts);
        SecondaryPorts = new MiaSecondaryPortController(
            secondaryPortProfile,
            secondaryPortWorker);
        SimInterface = new AsicSimInterface(
            Cpu,
            Clock,
            InterruptController,
            worker: simWorker);
        SimCard = virtualSim
            ? new GsmSimCard(SimInterface, worker: simCardWorker)
            : null;
        if (SimCard is not null)
        {
            SimCard.PersistenceChanged += OnPersistenceChanged;
        }
        if (SimCard is not null &&
            persistenceSnapshot?.Version == MiaPersistenceSnapshot.CurrentVersion)
        {
            SimCard.ApplyOverlay(persistenceSnapshot.SimFiles);
        }
        SedTimer = new AsicSedTimer(Cpu, Clock, InterruptController, sedWorker);
        InterruptRouter = new AsicInterruptRouter(
            Cpu,
            InterruptController,
            interruptRouterWorker);
        FirmwareController = new AsicFirmwareController(Cpu);
        _ = new AsicHandsetPort(Cpu);
        CommandPort = new AsicCommandPort(Cpu);
        TimeGenerator = new AsicTimeGenerator(
            Cpu,
            Clock,
            InterruptRouter,
            timeGeneratorWorker);
        ToneGenerator = new AsicToneGenerator(Cpu, Clock);
        RfFrontend = new AsicRfFrontend(
            Cpu,
            signalSource: rfSignalSource,
            worker: rfWorker);
        FchDetector = new AsicFchDetector(
            Cpu,
            Clock,
            RfFrontend,
            fchSource);
        Adc = new AsicAdc(
            Cpu,
            TimeGenerator,
            InterruptController,
            sampleSource: RfFrontend,
            worker: adcWorker);
        Equalizer = new AsicEqualizer(
            Cpu,
            Clock,
            InterruptController,
            equalizerSource,
            equalizerWorker);
        TransferController = new AsicTransferController(
            Cpu,
            Clock,
            transferControllerWorker);
        ChannelDecoder = new AsicChannelDecoder(
            Cpu,
            Clock,
            InterruptController,
            channelDecoderSource,
            worker: channelDecoderWorker,
            timeGenerator: TimeGenerator);
        PhCommandController = new AsicPhCommandController(
            Cpu,
            Clock,
            InterruptController,
            channelEncoderSink,
            phCommandWorker,
            TimeGenerator);
        Keypad = new AsicKeypad(Cpu);
        Keypad.InterruptRequested += () =>
            InterruptController.RaiseHighPriority(AsicInterruptController.KeypadSource);
        if (powerPressedInitially)
        {
            Keypad.Press(
                AsicKeypad.NoPowerScanMask,
                AsicKeypad.NoPowerRowMask);
        }
        InteractiveInput = new InteractiveKeypadInput(
            Keypad,
            PowerPorts,
            InterruptController);
        Display = new S4595Display();
        PrimaryI2c = new AsicI2cController(
            Cpu,
            Clock,
            InterruptController,
            0x0839,
            AsicInterruptController.I2cSource,
            address => MiaPowerPortController.Acknowledges(address) ||
                MiaSecondaryPortController.Acknowledges(address),
            (address, payload) =>
            {
                if (MiaPowerPortController.Acknowledges(address))
                {
                    PowerPorts.WriteTransaction(address, payload);
                }
                else if (MiaSecondaryPortController.Acknowledges(address))
                {
                    SecondaryPorts.WriteTransaction(address, payload);
                }
            },
            address => MiaPowerPortController.Acknowledges(address)
                ? PowerPorts.ReadByte(address)
                : SecondaryPorts.ReadByte(address),
            worker: primaryI2cWorker);
        DisplayI2c = new AsicI2cController(
            Cpu,
            Clock,
            InterruptController,
            AsicI2cController.DataAddress,
            AsicInterruptController.I2c1Source,
            address => (address & 0xfe) == S4595Display.WriteAddress,
            (address, payload) => _displayWorker.Invoke(
                () => Display.WriteTransaction(address, payload)),
            readByte: null,
            supportsTransmitDma: true,
            worker: displayI2cWorker);
        DisplayI2c.TransactionCompleted += OnDisplayTransactionCompleted;
        ByteChannels = new AsicByteChannels(
            Cpu,
            Clock,
            InterruptController,
            _byteChannelWorker);
        var modem = preparedModem;
        if (modem is null && !modemBih.IsEmpty)
        {
            modem = CreateModem(modemBih);
        }
        if (modem is not null)
        {
            Modem = modem;
            _armWorker = CreateWorker("ARM core");
#if BROWSER && !BROWSER_THREADS
            const bool coarseParallel = false;
#else
            bool coarseParallel =
                coreSchedulingMode == MiaCoreSchedulingMode.CoarseParallel;
            _coarseParallel = coarseParallel;
#endif
            _modemLinkBridge = new ArmModemLinkBridge(
                ByteChannels,
                Modem,
                bufferCoreEffects: coarseParallel,
                asicWorker: _byteChannelWorker,
                modemWorker: _armWorker,
                asicClock: Clock);
            BluetoothPeripheral = new ArmModemBluetoothPeripheral(Modem);
            BluetoothPeripheral.EmulationStatusChanged +=
                OnBluetoothEmulationStatusChanged;
            InfraredPeripheral = new ArmModemInfraredPeripheral(Modem);
            InfraredObjectPeer = new ArmModemInfraredObjectPeer(
                InfraredPeripheral);
            CommuniCamPeripheral = new MiaCommuniCamPeripheral(
                ByteChannels,
                Modem.Bus,
                _armWorker);
            CommuniCamPeripheral.StatusChanged +=
                OnCommuniCamStatusChanged;
            StatusIndicators.Attach(new MiaStatusIndicatorDependencies(
                Modem.Bus,
                BluetoothPeripheral,
                PowerPorts,
                Clock,
                _avrWorker));
        }

        TracePort = new AsicTracePort(Cpu);
        MiaMmioWorkerBinding.Bind(
            Cpu,
            rtcWorker,
            AsicRtc.SubsecondAddress,
            AsicRtc.InterruptStatusAddress);
        MiaMmioWorkerBinding.Bind(
            Cpu,
            sedWorker,
            AsicSedTimer.InterruptControlAddress);
        MiaMmioWorkerBinding.Bind(
            Cpu,
            simWorker,
            AsicSimInterface.DataAddress,
            AsicSimInterface.ControlAddress,
            AsicSimInterface.StatusAddress);
        MiaMmioWorkerBinding.Bind(
            Cpu,
            highPriorityRequestWorker,
            AsicHighPriorityRequests.RequestAddress);
        MiaMmioWorkerBinding.Bind(
            Cpu,
            timeGeneratorWorker,
            AsicTimeGenerator.FrameControlAddress);
        MiaMmioWorkerBinding.BindWrites(
            Cpu,
            timeGeneratorWorker,
            AsicTimeGenerator.CommandStatusAddress);
        MiaMmioWorkerBinding.BindRange(
            Cpu,
            rfWorker,
            AsicRfFrontend.FirstTransactionAddress,
            AsicRfFrontend.LastTransactionAddress);
        MiaMmioWorkerBinding.BindWrites(
            Cpu,
            fchWorker,
            AsicFchDetector.CommandAddress,
            AsicFchDetector.ParameterAddress);
        MiaMmioWorkerBinding.BindRange(
            Cpu,
            adcWorker,
            AsicAdc.ControlAddress,
            AsicAdc.SelectorAddress);
        MiaMmioWorkerBinding.BindRange(
            Cpu,
            equalizerWorker,
            AsicEqualizer.ControlAddress,
            AsicEqualizer.LastResultAddress);
        MiaMmioWorkerBinding.BindRange(
            Cpu,
            transferControllerWorker,
            AsicTransferController.ControlAddress,
            AsicTransferController.LastParameterAddress);
        MiaMmioWorkerBinding.BindWrites(
            Cpu,
            channelDecoderWorker,
            AsicChannelDecoder.ControlAddress,
            AsicChannelDecoder.CommandAddress);
        MiaMmioWorkerBinding.Bind(
            Cpu,
            phCommandWorker,
            AsicPhCommandController.CommandAddress,
            AsicPhCommandController.StatusAddress,
            AsicPhCommandController.ControlAddress);
        MiaMmioWorkerBinding.BindWrites(
            Cpu,
            primaryI2cWorker,
            0x0839,
            0x083a,
            0x083c);
        MiaMmioWorkerBinding.BindWrites(
            Cpu,
            displayI2cWorker,
            AsicI2cController.DataAddress,
            AsicI2cController.ControlAddress,
            AsicI2cController.ConfigurationAddress);
        MiaMmioWorkerBinding.BindRange(
            Cpu,
            _byteChannelWorker,
            0x0900,
            0x091f);
#if !BROWSER || BROWSER_THREADS
        if (_coarseParallel)
        {
            ByteChannels.EnablePostedTransmitWrites();
        }
#endif
        MiaMmioWorkerBinding.Bind(
            Cpu,
            firmwareControllerWorker,
            0x08e0,
            0x08e1);
        MiaMmioWorkerBinding.BindWrites(
            Cpu,
            commandPortWorker,
            AsicCommandPort.DataAddress);
        MiaMmioWorkerBinding.BindReadWithPostReply(
            Cpu,
            _inputWorker,
            AsicKeypad.RowStateAddress,
            InteractiveInput.TakePostTickFlushRequest,
            () => Cpu.ScheduleAfterTick(
                () => _inputWorker.Invoke(
                    InteractiveInput.FlushDeferredReleases)));
        MiaMmioWorkerBinding.BindWrites(
            Cpu,
            _inputWorker,
            AsicKeypad.ScanControlAddress);
        MiaMmioWorkerBinding.Bind(
            Cpu,
            traceWorker,
            AsicTracePort.StatusAddress);
        Diagnostics = new MiaMachineDiagnostics(
            this,
            _avrWorker,
            _inputWorker,
            simWorker,
            _byteChannelWorker,
            interruptRouterWorker);
        if (powerKeyReleaseCycle == 0)
        {
            ReleaseBootPowerKey();
        }
        else if (powerKeyReleaseCycle != long.MaxValue)
        {
            Clock.Schedule(_inputWorker, ReleaseBootPowerKey, powerKeyReleaseCycle);
        }
    }

    internal static ArmModem CreateModem(ReadOnlySpan<byte> bih) =>
        new(bih, ArmModemFlashProfile.StM36Dr216C);

    internal static Cpu CreateCpu(byte[] program) =>
        new(program, PhysicalDataSize, directDataRampRegister: 0x5b,
            dataAddressSpaceSize: DataSize);

    internal static byte[] BuildProgram(
        ReadOnlySpan<byte> firmwareImage,
        ReadOnlySpan<byte> gdfsImage = default)
    {
        if (firmwareImage.Length == 0 || firmwareImage.Length > ProgramSize)
        {
            throw new ArgumentException(
                $"Firmware must contain between 1 and 0x{ProgramSize:x} bytes.",
                nameof(firmwareImage));
        }

        var program = new byte[ProgramSize];
        Array.Fill(program, (byte)0xff);
        firmwareImage.CopyTo(program);
        if (!gdfsImage.IsEmpty)
        {
            GdfsImage.Load(gdfsImage, program);
        }

        return program;
    }

    public Cpu Cpu { get; }

    public MiaSystemClock Clock { get; }

    public AsicFlashMemory FlashMemory { get; }

    public AsicInterruptController InterruptController { get; }

    public AsicRtc Rtc { get; }

    public AsicHighPriorityRequests HighPriorityRequests { get; }

    public MiaPowerPortController PowerPorts { get; }

    public MiaPhoneLedController PhoneLeds { get; }

    public MiaSecondaryPortController SecondaryPorts { get; }

    public MiaStatusIndicatorController StatusIndicators { get; }

    public AsicSimInterface SimInterface { get; }

    public GsmSimCard? SimCard { get; }

    public MiaLiveGsmController? LiveGsm { get; }

    public long PersistenceVersion => SimCard?.PersistenceVersion ?? 0;

    public AsicSedTimer SedTimer { get; }

    public AsicInterruptRouter InterruptRouter { get; }

    public AsicFirmwareController FirmwareController { get; }

    public AsicCommandPort CommandPort { get; }

    public AsicTimeGenerator TimeGenerator { get; }

    public AsicToneGenerator ToneGenerator { get; }

    public AsicRfFrontend RfFrontend { get; }

    public AsicFchDetector FchDetector { get; }

    public AsicAdc Adc { get; }

    public AsicEqualizer Equalizer { get; }

    public AsicTransferController TransferController { get; }

    public AsicChannelDecoder ChannelDecoder { get; }

    public AsicPhCommandController PhCommandController { get; }

    public AsicKeypad Keypad { get; }

    public InteractiveKeypadInput InteractiveInput { get; }

    public S4595Display Display { get; }

    public AsicI2cController PrimaryI2c { get; }

    public AsicI2cController DisplayI2c { get; }

    public AsicByteChannels ByteChannels { get; }

    public ArmModem? Modem { get; }

    /// <summary>
    /// Firmware-visible Bluetooth DSP transport, when the ARM modem is loaded.
    /// It handles only measured transport handshakes; higher-level device
    /// records remain raw until their layouts are recovered.
    /// </summary>
    public ArmModemBluetoothPeripheral? BluetoothPeripheral { get; }

    /// <summary>
    /// Physical SIR frame boundary backed by the modem firmware's native
    /// IrLAP/IrLMP/TinyTP/OBEX stack.
    /// </summary>
    public ArmModemInfraredPeripheral? InfraredPeripheral { get; }

    /// <summary>
    /// Full IrLAP/IrLMP/IAS/TinyTP/OBEX peer used for deterministic infrared
    /// file transfer through the native handset stack.
    /// </summary>
    public ArmModemInfraredObjectPeer? InfraredObjectPeer { get; }

    /// <summary>
    /// Ericsson CommuniCam peer connected through the native accessory and
    /// normal-cable boundaries, when the ARM modem is loaded.
    /// </summary>
    public MiaCommuniCamPeripheral? CommuniCamPeripheral { get; }

    public void ConnectCommuniCam(IMiaCameraFrameSource frameSource)
    {
        if (CommuniCamPeripheral is null || _armWorker is null)
        {
            throw new InvalidOperationException(
                "CommuniCam requires a loaded ARM modem.");
        }
        _armWorker.Invoke(() => CommuniCamPeripheral.Connect(frameSource));
    }

    public void DisconnectCommuniCam()
    {
        if (CommuniCamPeripheral is null || _armWorker is null)
        {
            return;
        }
        _armWorker.Invoke(CommuniCamPeripheral.Disconnect);
    }

    public MiaCommuniCamStatus GetCommuniCamStatus()
    {
        if (CommuniCamPeripheral is null || _armWorker is null)
        {
            return MiaCommuniCamStatus.Disconnected;
        }
        return _armWorker.Invoke(() => CommuniCamPeripheral.Status);
    }

    /// <summary>
    /// Queues one complete IrLAP frame on the ARM owner's worker. The
    /// peripheral adds SIR escaping/FCS and delivers it through FIFO3/IRQ.
    /// </summary>
    public void QueueInfraredFrame(ReadOnlySpan<byte> frame)
    {
        if (InfraredPeripheral is null || _armWorker is null)
        {
            throw new InvalidOperationException(
                "Infrared frames require a loaded ARM modem.");
        }

        byte[] copy = frame.ToArray();
        _armWorker.Invoke(() => InfraredPeripheral.QueueReceivedFrame(copy));
    }

    public void StageEmulatedInfraredObject(MiaTransferObject? value)
    {
        if (InfraredObjectPeer is null || _armWorker is null)
        {
            throw new InvalidOperationException(
                "Infrared object transfer requires a loaded ARM modem.");
        }

        MiaTransferObject? copy = value?.Snapshot();
        _armWorker.Invoke(
            () => InfraredObjectPeer.StageObjectForHandset(copy));
    }

    public MiaTransferObject? GetInfraredReceivedObject()
    {
        if (InfraredObjectPeer is null || _armWorker is null)
        {
            throw new InvalidOperationException(
                "Infrared object transfer requires a loaded ARM modem.");
        }

        return _armWorker.Invoke(InfraredObjectPeer.GetReceivedObject)
            ?.Snapshot();
    }

    public MiaInfraredTransferStatus GetInfraredTransferStatus()
    {
        if (InfraredObjectPeer is null || _armWorker is null)
        {
            throw new InvalidOperationException(
                "Infrared object transfer requires a loaded ARM modem.");
        }

        return _armWorker.Invoke(InfraredObjectPeer.CaptureStatus);
    }

    /// <summary>
    /// Queues one recovered Bluetooth-controller record on the ARM owner's
    /// worker. This preserves the DSP FIFO/IRQ boundary while preventing a
    /// host caller from racing ARM execution.
    /// </summary>
    public void QueueBluetoothControllerRecord(ReadOnlySpan<byte> record)
    {
        if (BluetoothPeripheral is null || _armWorker is null)
        {
            throw new InvalidOperationException(
                "Bluetooth controller records require a loaded ARM modem.");
        }

        byte[] copy = record.ToArray();
        _armWorker.Invoke(() => BluetoothPeripheral.QueueControllerRecord(copy));
    }

    /// <summary>
    /// Configures only the deterministic peer on the ARM owner's worker. The
    /// native handset UI still performs discovery, PIN entry, and bonding.
    /// </summary>
    public void ConfigureEmulatedBluetoothPeer(
        bool enabled,
        string pin,
        bool incomingObjectPushEnabled)
    {
        if (BluetoothPeripheral is null || _armWorker is null)
        {
            throw new InvalidOperationException(
                "Bluetooth peer emulation requires a loaded ARM modem.");
        }

        _armWorker.Invoke(() => BluetoothPeripheral.ConfigureEmulatedPeer(
            enabled,
            pin,
            incomingObjectPushEnabled));
    }

    /// <summary>
    /// Captures controller-emulation settings and progress on the ARM owner's
    /// worker so browser and desktop front ends never race modem execution.
    /// </summary>
    public MiaBluetoothEmulationStatus GetBluetoothEmulationStatus()
    {
        if (BluetoothPeripheral is null || _armWorker is null)
        {
            throw new InvalidOperationException(
                "Bluetooth peer emulation requires a loaded ARM modem.");
        }

        return _armWorker.Invoke(BluetoothPeripheral.CaptureEmulationStatus);
    }

    public void StageEmulatedBluetoothObject(MiaTransferObject? value)
    {
        if (BluetoothPeripheral is null || _armWorker is null)
        {
            throw new InvalidOperationException(
                "Bluetooth peer emulation requires a loaded ARM modem.");
        }

        MiaTransferObject? copy = value?.Snapshot();
        _armWorker.Invoke(
            () => BluetoothPeripheral.StageEmulatedIncomingObject(copy));
    }

    public MiaTransferObject? GetBluetoothReceivedObject()
    {
        if (BluetoothPeripheral is null || _armWorker is null)
        {
            throw new InvalidOperationException(
                "Bluetooth peer emulation requires a loaded ARM modem.");
        }

        return _armWorker.Invoke(
            () => BluetoothPeripheral.EmulatedObexTransferredObjectSnapshot)
            ?.Snapshot();
    }

    public AsicTracePort TracePort { get; }

    internal MiaMachineDiagnostics Diagnostics { get; }

    public long ExecutedInstructions { get; private set; }

    public bool IdleFastForwardEnabled { get; set; } = true;

    /// <summary>
    /// Word address of the unique firmware idle-loop signature, when the
    /// loaded firmware provides one.
    /// </summary>
    public int? IdleFastForwardLoopStartWord =>
        _idleLoopStartWord >= 0 ? _idleLoopStartWord : null;

    public bool DeferredAvrClockSynchronizationEnabled { get; set; } = true;

    public long IdleFastForwardCount { get; private set; }

    public long IdleFastForwardedInstructions { get; private set; }

    /// <summary>
    /// Enables the cycle-exact GDFS sector-header scan fast path when the
    /// loaded firmware has the verified instruction body.
    /// </summary>
    public bool GdfsSectorHeaderFastPathEnabled { get; set; } = true;
    public bool GdfsUnitStateFastPathEnabled { get; set; } = true;

    public long GdfsSectorHeaderFastForwardCount { get; private set; }

    public long GdfsSectorHeaderFastForwardedInstructions { get; private set; }

    internal void StartExecutionHotspotProfiling() =>
        _executionHotspots = [];

    internal void DisableFirmwareFastPathsForDiagnostics() =>
        _firmwareFastPathsBlockedByDiagnostics = true;

    internal IReadOnlyList<MiaExecutionHotspot> SnapshotExecutionHotspots() =>
        _executionHotspots is null
            ? []
            : _executionHotspots
                .Select(pair => new MiaExecutionHotspot(
                    pair.Key,
                    pair.Value.Executions,
                    pair.Value.BackwardBranches))
                .OrderByDescending(hotspot => hotspot.Executions)
                .ThenBy(hotspot => hotspot.ProgramCounter)
                .ToArray();

    internal void EnableIdleFastForwardDiagnostics()
    {
        _idleFastForwardDiagnosticsEnabled = true;
        _idleFastForwardCandidateCount = 0;
        _idleFastForwardSuccessCount = 0;
        _idleFastForwardedInstructionsAtDiagnosticsStart =
            IdleFastForwardedInstructions;
        _lastIdleFastForwardBlockReason =
            MiaIdleFastForwardBlockReason.None;
        _idleFastForwardBlockCounts = new long[
            Enum.GetValues<MiaIdleFastForwardBlockReason>().Length];
    }

    internal MiaIdleFastForwardDiagnostics
        SnapshotIdleFastForwardDiagnostics()
    {
        var counts = _idleFastForwardBlockCounts;
        var blocks = counts is null
            ? []
            : Enumerable.Range(1, counts.Length - 1)
                .Where(index => counts[index] != 0)
                .Select(index => new MiaIdleFastForwardBlockCount(
                    (MiaIdleFastForwardBlockReason)index,
                    counts[index]))
                .ToArray();
        return new(
            IdleFastForwardLoopStartWord,
            _idleFastForwardCandidateCount,
            _idleFastForwardSuccessCount,
            IdleFastForwardedInstructions -
                _idleFastForwardedInstructionsAtDiagnosticsStart,
            _lastIdleFastForwardBlockReason,
            blocks);
    }

#if !BROWSER || BROWSER_THREADS
    public int? ArmWorkerThreadId => _armWorker?.ThreadId;

    public long CoarseGrantCount => _coarseGrantCount;

    public long ArmGrantReadyCount => _armGrantReadyCount;

    public long ArmCatchupCount => _armCatchupCount;
#endif

    public IReadOnlyList<MiaWorker> Workers => _workers;

    public long Cycles => Clock.Cycles;

    public int FrameVersion => Volatile.Read(ref _frameVersion);

    public ReadOnlyMemory<byte> Frame => Volatile.Read(ref _frame);

    /// <summary>
    /// Returns a stable raw snapshot of the complete nonvolatile GDFS window.
    /// Pending NOR-owner work is drained before the backing flash is copied.
    /// </summary>
    public byte[] CaptureGdfsImage() => _avrWorker.Invoke(() =>
    {
        FlashMemory.Synchronize();
        return Cpu.ProgBytes
            .AsSpan(GdfsImage.RawAddress, GdfsImage.RawLength)
            .ToArray();
    });

    public MiaPersistenceSnapshot CreatePersistenceSnapshot() => new(
        MiaPersistenceSnapshot.CurrentVersion,
        SimCard?.CreateOverlay() ?? []);

    public MiaPersistenceCapture CapturePersistenceSnapshot()
    {
        (long version, SimFileOverlay[] overlay) = SimCard?.CapturePersistence() ??
            (0, []);
        return new(
            version,
            new(MiaPersistenceSnapshot.CurrentVersion, overlay));
    }

    /// <summary>
    /// Publishes immutable LCD snapshots asynchronously from emulated device
    /// execution, so frontends never need to poll framebuffer state.
    /// </summary>
    public event Action<ReadOnlyMemory<byte>>? FramePublished;

    /// <summary>
    /// Publishes SIM mutations from the SIM-card owner after a complete
    /// firmware write, so persistence frontends never sample a version counter.
    /// </summary>
    public event Action<long>? PersistenceChanged;

    /// <summary>
    /// Publishes deterministic peer progress from the ARM/controller owner.
    /// </summary>
    public event Action<MiaBluetoothEmulationStatus>?
        BluetoothEmulationStatusChanged;

    public event Action<MiaCommuniCamStatus>?
        CommuniCamStatusChanged;

    public IMiaExecutionObserver? ExecutionObserver
    {
        get => _executionObserver;
        set => _executionObserver = value;
    }

    public long AsicToModemByteCount =>
        _modemLinkBridge?.AsicToModemByteCount ?? 0;

    public long ModemToAsicByteCount =>
        _modemLinkBridge?.ModemToAsicByteCount ?? 0;

    public bool IsStopped { get; private set; }

    public string? StopReason { get; private set; }

    public InteractiveKeypadTransition SetKey(
        byte scanMask,
        byte rowMask,
        byte? secondaryScanMask,
        bool pressed) =>
        _avrWorker.Invoke(() => _inputWorker.Invoke(
            () => InteractiveInput.SetKey(
                scanMask,
                rowMask,
                secondaryScanMask,
                pressed)));

    public InteractiveKeypadTransition SetPowerKey(bool pressed) =>
        _avrWorker.Invoke(() => _inputWorker.Invoke(
            () => InteractiveInput.SetPower(pressed)));

    public ValueTask<InteractiveKeypadTransition> SetKeyAsync(
        byte scanMask,
        byte rowMask,
        byte? secondaryScanMask,
        bool pressed) =>
        _avrWorker.InvokeAsync(() => _inputWorker.Invoke(
            () => InteractiveInput.SetKey(
                scanMask,
                rowMask,
                secondaryScanMask,
                pressed)));

    public ValueTask<InteractiveKeypadTransition> SetPowerKeyAsync(bool pressed) =>
        _avrWorker.InvokeAsync(() => _inputWorker.Invoke(
            () => InteractiveInput.SetPower(pressed)));

    /// <summary>
    /// Completes when the guest ASIC clock reaches an exact deadline. The
    /// completion is scheduled in the machine clock rather than sampled by a
    /// host timer.
    /// </summary>
    public async ValueTask WaitUntilCycleAsync(
        long targetCycle,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await _avrWorker.InvokeAsync(() =>
        {
            if (Clock.Cycles >= targetCycle)
            {
                completion.TrySetResult();
                return;
            }
            Clock.ScheduleAt(
                () => completion.TrySetResult(),
                targetCycle);
        }).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Advances a bounded amount of host work. ROM dispatches consume work
    /// items but not AVR instructions, keeping cooperative browser callers from
    /// monopolizing the UI thread while preserving native cycle accounting.
    /// </summary>
    public int RunWorkItems(int workItemBudget)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workItemBudget);
        return _avrWorker.Invoke(() => RunWorkItemsCore(workItemBudget));
    }

    /// <summary>
    /// Advances one frontend-oriented host grant. In threaded coarse mode this
    /// keeps AVR ownership across short returns to the verified idle loop, but
    /// never beyond one 60,000-cycle GSM frame. Diagnostic stepping retains
    /// the finer single-grant boundary exposed by <see cref="RunWorkItems"/>.
    /// </summary>
    public int RunInteractiveWorkItems(int workItemBudget)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workItemBudget);
        return _avrWorker.Invoke(() => RunWorkItemsCore(
            workItemBudget,
            coalescePartialCoarseGrants: true));
    }

    int RunWorkItemsCore(
        int workItemBudget,
        bool coalescePartialCoarseGrants = false)
    {
#if !BROWSER || BROWSER_THREADS
        if (_coarseParallel && _armWorker is not null)
        {
            return RunCoarseParallelBatches(
                workItemBudget,
                coalescePartialCoarseGrants);
        }
#endif
        return RunCycleLockedWorkItems(workItemBudget);
    }

    int RunCycleLockedWorkItems(int workItemBudget)
    {
        int completed = 0;
        while (completed < workItemBudget && !IsStopped)
        {
            bool fastForwarded = StepCycleLocked();
            completed++;
            if (fastForwarded)
            {
                break;
            }
        }
        SynchronizeRunBoundary();
        return completed;
    }

#if !BROWSER || BROWSER_THREADS
    int RunCoarseParallelBatches(
        int workItemBudget,
        bool coalescePartialGrants)
    {
        int totalCompleted = 0;
        long maximumGrantCycle = coalescePartialGrants
            ? Clock.Cycles <= long.MaxValue - MaximumInteractiveGrantAsicCycles
                ? Clock.Cycles + MaximumInteractiveGrantAsicCycles
                : long.MaxValue
            : long.MaxValue;
        while (totalCompleted < workItemBudget &&
               !IsStopped &&
               (!coalescePartialGrants || Clock.Cycles < maximumGrantCycle))
        {
            int quantum = Math.Min(
                CoarseParallelQuantum,
                workItemBudget - totalCompleted);
            int quantumCompleted = RunCoarseParallelWorkItems(
                quantum,
                maximumGrantCycle);
            totalCompleted += quantumCompleted;
            if (quantumCompleted != quantum &&
                (!coalescePartialGrants || quantumCompleted == 0))
            {
                break;
            }
        }
        SynchronizeRunBoundary();
        return totalCompleted;
    }
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void SynchronizeRunBoundary()
    {
        FlashMemory.Synchronize();
        TimeGenerator.Synchronize();
        LiveGsm?.Advance(this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        DetachPeripheralEvents();
        DisposePeripherals();
        DisposeWorkers();
        _disposed = true;
    }

    void DetachPeripheralEvents()
    {
        DisplayI2c.TransactionCompleted -= OnDisplayTransactionCompleted;
        if (SimCard is not null)
        {
            SimCard.PersistenceChanged -= OnPersistenceChanged;
        }
        StatusIndicators.Detach();
        if (BluetoothPeripheral is not null)
        {
            BluetoothPeripheral.EmulationStatusChanged -=
                OnBluetoothEmulationStatusChanged;
        }
        if (CommuniCamPeripheral is not null)
        {
            CommuniCamPeripheral.StatusChanged -=
                OnCommuniCamStatusChanged;
        }
    }

    void DisposePeripherals()
    {
        BluetoothPeripheral?.Dispose();
        CommuniCamPeripheral?.Dispose();
        InfraredObjectPeer?.Dispose();
        InfraredPeripheral?.Dispose();
        Adc.Dispose();
        Diagnostics.Dispose();
        PhoneLeds.Dispose();
        _modemLinkBridge?.Dispose();
    }

    void DisposeWorkers()
    {
        for (var index = _workers.Count - 1; index >= 0; index--)
        {
            _workers[index].Dispose();
        }
        // Every worker, including these six, is already disposed by the loop
        // above (each is also added to _workers by CreateWorker); MiaWorker's
        // Dispose is idempotent, so these calls are redundant but harmless,
        // and let the analyzer see each field's own disposal directly.
        _avrWorker.Dispose();
        _armWorker?.Dispose();
        _inputWorker.Dispose();
        _displayWorker.Dispose();
        _byteChannelWorker.Dispose();
        _hostOutputWorker.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool StepCycleLocked()
    {
        if (Modem is not null && !Modem.IsStopped && Modem.Cycles <= Clock.Cycles)
        {
            if (_armWorker!.IsCurrentThread)
            {
                Modem.RunUntilCycle(Clock.Cycles + 1);
            }
            else
            {
                _armWorker.Invoke(() => Modem.RunUntilCycle(Clock.Cycles + 1));
            }
            _modemLinkBridge!.CommitModemSchedules();
        }
        return StepAvr(allowIdleFastForward: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool StepAvr(
        bool allowIdleFastForward,
        bool synchronizeClock = true)
    {
        var executionObserver = _executionObserver;
        if (TryApplyDiagnosticStop(executionObserver))
        {
            return false;
        }
        if (allowIdleFastForward && TryFastForwardAvr())
        {
            return true;
        }
        if (TryHandleNonExecutableProgramCounter(executionObserver))
        {
            return false;
        }
        ExecuteAvrInstruction(synchronizeClock);
        return false;
    }

    bool TryApplyDiagnosticStop(IMiaExecutionObserver? executionObserver)
    {
        string? diagnosticStop = executionObserver?.BeforeWorkItem(this);
        if (diagnosticStop is null)
        {
            return false;
        }
        Stop(diagnosticStop);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool TryFastForwardAvr() =>
        (Cpu.PC == _idleLoopStartWord && TryFastForwardIdleLoop()) ||
        (Cpu.PC == AsicFirmwareFastPaths.GdfsSectorHeaderScanWord &&
         TryFastForwardGdfsSectorHeaderLoop()) ||
        (Cpu.PC == AsicFirmwareFastPaths.GdfsUnitStateScanWord &&
         TryFastForwardGdfsUnitStateLoop());

    bool TryHandleNonExecutableProgramCounter(
        IMiaExecutionObserver? executionObserver)
    {
        if (IsNonExecutableGdfsWord(Cpu.PC))
        {
            Stop($"entered non-executable GDFS flash at word 0x{Cpu.PC:x6}");
            return true;
        }
        if (Cpu.PC >= AsicRom.StartWord)
        {
            DispatchRomService(executionObserver);
            return true;
        }
        int opcode = Cpu.GetProgWord(Cpu.PC);
        if (opcode != 0xffff || !IsErasedFlashRun(Cpu, Cpu.PC, opcode))
        {
            return false;
        }
        Stop($"entered erased flash at word 0x{Cpu.PC:x6}");
        return true;
    }

    void ExecuteAvrInstruction(bool synchronizeClock)
    {
        int programCounter = Cpu.PC;
        int opcode = Cpu.GetProgWord(programCounter);
        AvrInstruction.Execute(Cpu, opcode);
        RecordExecutionHotspot(
            programCounter,
            executions: 1,
            backwardBranches: Cpu.PC <= programCounter ? 1 : 0);
        SynchronizeExecutedInstruction(synchronizeClock);
        NotifyReturnedInterrupt(opcode);
        DispatchScheduledInterrupt();
        ExecutedInstructions++;
    }

    void SynchronizeExecutedInstruction(bool synchronizeClock)
    {
        if (synchronizeClock)
        {
            Clock.SynchronizeAvrCycles(Cpu.Cycles);
        }
    }

    void NotifyReturnedInterrupt(int opcode)
    {
        if (opcode == 0x9518)
        {
            InterruptController.NotifyHighPriorityReturned();
        }
    }

    void DispatchScheduledInterrupt()
    {
        int dispatchedInterrupt = Cpu.Tick();
        if (dispatchedInterrupt == AsicInterruptController.HighPriorityVectorWord)
        {
            AsicRom.CaptureHighPriorityInterrupt(Cpu);
            InterruptController.NotifyHighPriorityDispatched();
        }
        if (dispatchedInterrupt >= 0)
        {
            Clock.SynchronizeAvrCycles(Cpu.Cycles);
        }
    }

    static bool IsNonExecutableGdfsWord(int programCounter) =>
        programCounter >= AsicFlashMemory.PhysicalGdfsBase / 2 &&
        programCounter < AsicRom.StartWord &&
        (programCounter < AsicExecutableRam.ProgramWordStart ||
         programCounter >= AsicExecutableRam.ProgramWordStart +
            AsicExecutableRam.Length / 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void DispatchRomService(IMiaExecutionObserver? executionObserver)
    {
        int entry = Cpu.PC;
        AsicRomDispatchResult result = AsicRom.Dispatch(Cpu);
        Clock.SynchronizeAvrCycles(Cpu.Cycles);
        executionObserver?.AfterRomDispatch(this, entry, result);
        switch (result)
        {
            case AsicRomDispatchResult.Idle:
                Stop($"scheduler idle/context boundary at ROM word 0x{entry:x6}");
                return;
            case AsicRomDispatchResult.Unknown:
                Stop($"unimplemented ROM service at word 0x{entry:x6}");
                return;
        }
        if (entry == 0x3f0162)
        {
            InterruptController.NotifyHighPriorityReturned();
        }
    }

#if !BROWSER || BROWSER_THREADS
    int RunCoarseParallelWorkItems(
        int workItemBudget,
        long maximumIdleCycle = long.MaxValue)
    {
        var modem = Modem!;
        var armWorker = _armWorker!;
        var bridge = _modemLinkBridge!;

        // The scan touches only AVR registers and GDFS reads. Collapse it up
        // to the next device deadline, then run the ARM to the same barrier.
        if ((Cpu.PC == AsicFirmwareFastPaths.GdfsSectorHeaderScanWord &&
             TryFastForwardGdfsSectorHeaderLoop(maximumIdleCycle)) ||
            (Cpu.PC == AsicFirmwareFastPaths.GdfsUnitStateScanWord &&
             TryFastForwardGdfsUnitStateLoop(maximumIdleCycle)))
        {
            CompleteCoarseBarrier(modem, armWorker, bridge);
            return 1;
        }

        int idleWorkCompleted = TryRunCoarseParallelIdleFastForward(
            modem,
            armWorker,
            bridge,
            maximumIdleCycle);
        if (idleWorkCompleted != 0)
        {
            return idleWorkCompleted;
        }

        long startCycles = Clock.Cycles;
        long estimatedCycles = Math.Max(
            1,
            (workItemBudget * _coarseCyclesPerWorkItemQ16 + 0xffff) >> 16);
        MiaWorkerConcurrentInvocation armRun = armWorker.BeginConcurrent(
            () => modem.RunUntilCycle(startCycles + estimatedCycles));

        var completed = 0;
        long synchronizedAvrCycles = Cpu.Cycles;
        long maximumDeferredCycles = Clock.GetMaximumAdditionalAvrCyclesBefore(
            Clock.NextEventCycle);
        while (completed < workItemBudget && !IsStopped)
        {
            bool deferClockSynchronization =
                DeferredAvrClockSynchronizationEnabled &&
                _executionObserver is null &&
                CanDeferClockForNextAvrInstruction();
            if (!deferClockSynchronization &&
                Cpu.Cycles != synchronizedAvrCycles)
            {
                Clock.SynchronizeAvrCycles(Cpu.Cycles);
                synchronizedAvrCycles = Cpu.Cycles;
                maximumDeferredCycles =
                    Clock.GetMaximumAdditionalAvrCyclesBefore(
                        Clock.NextEventCycle);
            }

            StepAvr(
                allowIdleFastForward: false,
                synchronizeClock: !deferClockSynchronization);
            completed++;
            if (!deferClockSynchronization)
            {
                synchronizedAvrCycles = Cpu.Cycles;
                maximumDeferredCycles =
                    Clock.GetMaximumAdditionalAvrCyclesBefore(
                        Clock.NextEventCycle);
            }
            else if (Cpu.Cycles - synchronizedAvrCycles >
                     maximumDeferredCycles)
            {
                SynchronizeDeferredAvrClockBoundary();
                synchronizedAvrCycles = Cpu.Cycles;
                maximumDeferredCycles =
                    Clock.GetMaximumAdditionalAvrCyclesBefore(
                        Clock.NextEventCycle);
            }
            if (IdleFastForwardEnabled &&
                _executionObserver is null &&
                Cpu.PC == _idleLoopStartWord &&
                MatchesIdleLoopSignature())
            {
                // End the active core grant as soon as firmware returns to its
                // verified scheduler loop. Both cores still meet at the normal
                // effect barrier; the next grant can establish and collapse
                // the idle fixed point without spinning out this whole quantum.
                break;
            }
            if (!_firmwareFastPathsBlockedByDiagnostics &&
                _executionObserver is null &&
                ((GdfsSectorHeaderFastPathEnabled && _gdfsSectorHeaderFastPathAvailable &&
                  Cpu.PC == AsicFirmwareFastPaths.GdfsSectorHeaderScanWord) ||
                 (GdfsUnitStateFastPathEnabled && _gdfsUnitStateFastPathAvailable &&
                  Cpu.PC == AsicFirmwareFastPaths.GdfsUnitStateScanWord)))
            {
                break;
            }
        }
        if (Cpu.Cycles != synchronizedAvrCycles)
        {
            Clock.SynchronizeAvrCycles(Cpu.Cycles);
        }

        _coarseGrantCount++;
        if (armRun.IsCompleted)
        {
            _armGrantReadyCount++;
        }
        ByteChannels.FlushPostedTransmitWrites();
        armRun.Wait();
        if (!modem.IsStopped && modem.Cycles < Clock.Cycles)
        {
            _armCatchupCount++;
            armWorker.Invoke(() => modem.RunUntilCycle(Clock.Cycles));
        }
        bridge.CommitAsicEffects();
        bridge.CommitModemEffects();

        if (completed != 0)
        {
            long observedQ16 = Math.Clamp(
                ((Clock.Cycles - startCycles) << 16) / completed,
                1,
                16L << 16);
            _coarseCyclesPerWorkItemQ16 =
                (_coarseCyclesPerWorkItemQ16 * 3 + observedQ16) / 4;
        }
        return completed;
    }

    bool CanDeferClockForNextAvrInstruction()
    {
        if (Cpu.NextInterrupt >= 0 ||
            Cpu.HasScheduledAfterTick ||
            Cpu.PC >= AsicFlashMemory.PhysicalGdfsBase / 2 ||
            Cpu.PC >= AsicRom.StartWord)
        {
            return false;
        }

        int opcode = Cpu.GetProgWord(Cpu.PC);
        uint group = (uint)opcode >> 10;
        return group switch
        {
            0 => opcode == 0, // NOP only; other group-zero forms vary.
            >= 1 and <= 23 => true, // Register ALU, compare, immediate ALU.
            >= 24 and <= 31 => true, // ORI / ANDI.
            >= 48 and <= 51 => true, // RJMP; RCALL begins at group 52.
            >= 56 and <= 61 => true, // LDI and conditional branches.
            _ => false,
        };
    }

    void SynchronizeDeferredAvrClockBoundary()
    {
        Clock.SynchronizeAvrCycles(Cpu.Cycles);
        int dispatchedInterrupt = Cpu.Tick();
        if (dispatchedInterrupt == AsicInterruptController.HighPriorityVectorWord)
        {
            AsicRom.CaptureHighPriorityInterrupt(Cpu);
            InterruptController.NotifyHighPriorityDispatched();
        }
        if (dispatchedInterrupt >= 0)
        {
            Clock.SynchronizeAvrCycles(Cpu.Cycles);
        }
    }

    int TryRunCoarseParallelIdleFastForward(
        ArmModem modem,
        MiaWorker armWorker,
        ArmModemLinkBridge bridge,
        long maximumIdleCycle = long.MaxValue)
    {
        if (!IdleFastForwardEnabled ||
            _executionObserver is not null ||
            !IsIdleLoopInstruction(Cpu.PC) ||
            modem.IsStopped ||
            Cpu.NextInterrupt >= 0 ||
            Cpu.HasScheduledAfterTick ||
            !InterruptController.IsQuiescentForIdleLoop ||
            !MatchesIdleLoopSignature())
        {
            return 0;
        }

        ByteChannels.FlushPostedTransmitWrites();
        if (!modem.IsStopped && modem.Cycles < Clock.Cycles)
        {
            armWorker.Invoke(() => modem.RunUntilCycle(Clock.Cycles));
        }
        bridge.CommitAsicEffects();
        bridge.CommitModemEffects();

        // A coarse grant can end at any instruction boundary in this loop, and
        // real interrupt work can return to its first instruction with scratch
        // registers that are not at the loop fixed point yet. Try the fixed
        // point first; otherwise execute at most one real iteration. If an
        // interrupt or active scheduler state diverts execution, finish this
        // short grant normally instead of assuming that the loop stayed idle.
        long instructionsBeforeAlignment = ExecutedInstructions;
        if (Cpu.PC == _idleLoopStartWord &&
            TryFastForwardIdleLoop(
                requireQuiescentModem: false,
                constrainToModemEvent: false,
                maximumTargetCycle: maximumIdleCycle))
        {
            CompleteCoarseBarrier(modem, armWorker, bridge);
            return 1;
        }
        if (Cpu.PC == _idleLoopStartWord)
        {
            StepAvr(allowIdleFastForward: false);
        }
        while (Cpu.PC != _idleLoopStartWord &&
               IsIdleLoopInstruction(Cpu.PC) &&
               ExecutedInstructions - instructionsBeforeAlignment <
                   IdleLoopInstructionCount)
        {
            StepAvr(allowIdleFastForward: false);
        }
        int alignmentWork = checked((int)(
            ExecutedInstructions - instructionsBeforeAlignment));

        // Coarse mode already journals both cores' serial effects until this
        // barrier. The AVR loop can therefore be collapsed independently while
        // the ARM executes its real instruction stream to the same clock. ARM
        // bus deadlines are handled by RunUntilCycle rather than limiting the
        // AVR advance as they must in cycle-locked mode.
        bool fastForwarded = TryFastForwardIdleLoop(
                requireQuiescentModem: false,
                constrainToModemEvent: false,
                maximumTargetCycle: maximumIdleCycle);
        if (!fastForwarded)
        {
            if (alignmentWork == 0)
            {
                return 0;
            }

            CompleteCoarseBarrier(modem, armWorker, bridge);
            return alignmentWork;
        }

        CompleteCoarseBarrier(modem, armWorker, bridge);
        return alignmentWork + 1;
    }

    void CompleteCoarseBarrier(
        ArmModem modem,
        MiaWorker armWorker,
        ArmModemLinkBridge bridge)
    {
        if (!modem.IsStopped && modem.Cycles < Clock.Cycles)
        {
            armWorker.Invoke(() => modem.RunUntilCycle(Clock.Cycles));
        }
        bridge.CommitAsicEffects();
        bridge.CommitModemEffects();
    }
#endif

    bool TryFastForwardGdfsUnitStateLoop(long maximumTargetCycle = long.MaxValue)
    {
        if (!GdfsUnitStateFastPathEnabled || !_gdfsUnitStateFastPathAvailable ||
            _firmwareFastPathsBlockedByDiagnostics || _executionObserver is not null ||
            Cpu.NextInterrupt >= 0 || Cpu.HasScheduledAfterTick ||
            Cpu.WriteHooks[Cpu.DirectDataRampRegister] is not null)
        {
            return false;
        }
        long maximumCycles = Clock.GetMaximumAdditionalAvrCyclesBefore(
            Math.Min(Clock.NextEventCycle, maximumTargetCycle));
        if (!AsicFirmwareFastPaths.TrySkipGdfsUnitStateZeroRun(
                Cpu, maximumCycles, out int skippedInstructions))
        {
            return false;
        }
        Clock.SynchronizeAvrCycles(Cpu.Cycles);
        ExecutedInstructions += skippedInstructions;
        return true;
    }

    bool TryFastForwardGdfsSectorHeaderLoop(long maximumTargetCycle = long.MaxValue)
    {
        if (!GdfsSectorHeaderFastPathEnabled ||
            !_gdfsSectorHeaderFastPathAvailable ||
            _firmwareFastPathsBlockedByDiagnostics ||
            _executionObserver is not null ||
            Cpu.PC != AsicFirmwareFastPaths.GdfsSectorHeaderScanWord ||
            Cpu.NextInterrupt >= 0 ||
            Cpu.HasScheduledAfterTick ||
            Cpu.WriteHooks[Cpu.DirectDataRampRegister] is not null)
        {
            return false;
        }

        long maximumCycles = Clock.GetMaximumAdditionalAvrCyclesBefore(
            Math.Min(Clock.NextEventCycle, maximumTargetCycle));
        if (!AsicFirmwareFastPaths.TrySkipGdfsSectorHeaderMismatchRun(
                Cpu,
                maximumCycles,
                out int skippedInstructions))
        {
            return false;
        }

        Clock.SynchronizeAvrCycles(Cpu.Cycles);
        ExecutedInstructions += skippedInstructions;
        GdfsSectorHeaderFastForwardCount++;
        GdfsSectorHeaderFastForwardedInstructions += skippedInstructions;
        return true;
    }

    bool TryFastForwardIdleLoop(
        bool requireQuiescentModem = true,
        bool constrainToModemEvent = true,
        long maximumTargetCycle = long.MaxValue)
        => TryPrepareIdleFastForward(requireQuiescentModem, out var modem) &&
            TryGetIdleFastForwardLoopCount(
                modem,
                constrainToModemEvent,
                maximumTargetCycle,
                out var loopCount) &&
            ApplyIdleFastForward(loopCount);

    bool TryPrepareIdleFastForward(
        bool requireQuiescentModem,
        out ArmModem? modem)
    {
        modem = Modem;
        var readiness = InspectIdleFastForwardReadiness(
            Cpu.PC == _idleLoopStartWord,
            requireQuiescentModem,
            modem);
        if (!readiness.CanAdvance)
        {
            RecordIdleFastForwardBlock(
                readiness.Candidate,
                readiness.Reason);
            return false;
        }
        return true;
    }

    MiaIdleFastForwardReadiness InspectIdleFastForwardReadiness(
        bool atIdleLoopEntry,
        bool requireQuiescentModem,
        ArmModem? modem) => true switch
        {
            _ when !IdleFastForwardEnabled => new(
                false, atIdleLoopEntry, MiaIdleFastForwardBlockReason.Disabled),
            _ when _executionObserver is not null => new(
                false, atIdleLoopEntry,
                MiaIdleFastForwardBlockReason.ExecutionObserverAttached),
            _ when !atIdleLoopEntry => new(
                false, false, MiaIdleFastForwardBlockReason.None),
            _ when Cpu.NextInterrupt >= 0 => new(
                false, true, MiaIdleFastForwardBlockReason.InterruptPending),
            _ when Cpu.HasScheduledAfterTick => new(
                false, true, MiaIdleFastForwardBlockReason.DeferredTickPending),
            _ when !InterruptController.IsQuiescentForIdleLoop => new(
                false, true,
                MiaIdleFastForwardBlockReason.InterruptControllerActive),
            _ when !MatchesIdleLoopSignature() => new(
                false, true,
                MiaIdleFastForwardBlockReason.FirmwareSignatureChanged),
            _ when modem is { IsStopped: true } => new(
                false, true, MiaIdleFastForwardBlockReason.ModemStopped),
            _ when modem is not null && requireQuiescentModem &&
                (!modem.IsSleeping || modem.Bus.IrqLineAsserted) => new(
                    false, true,
                    MiaIdleFastForwardBlockReason.ModemNotQuiescent),
            _ when HasIdleLoopDataHook() => new(
                false, true, MiaIdleFastForwardBlockReason.DataHookInstalled),
            _ when GetIdleRegisterBlockReason() is var reason &&
                reason != MiaIdleFastForwardBlockReason.None => new(
                    false, true, reason),
            _ => new(true, true, MiaIdleFastForwardBlockReason.None),
        };

    bool HasIdleLoopDataHook()
    {
        int schedulerStateAddress = Cpu.TranslateDataAddress(0x00f603);
        int sourceAddress = Cpu.TranslateDataAddress(0x02df0c);
        int targetAddress = Cpu.TranslateDataAddress(0x000ac0);
        return Cpu.ReadHooks[schedulerStateAddress] is not null ||
            Cpu.ReadHooks[sourceAddress] is not null ||
            Cpu.WriteHooks[targetAddress] is not null ||
            Cpu.WriteHooks[Cpu.DirectDataRampRegister] is not null;
    }

    MiaIdleFastForwardBlockReason GetIdleRegisterBlockReason()
    {
        int rampzAddress = Cpu.DirectDataRampRegister;
        int schedulerStateAddress = Cpu.TranslateDataAddress(0x00f603);
        int sourceAddress = Cpu.TranslateDataAddress(0x02df0c);
        int targetAddress = Cpu.TranslateDataAddress(0x000ac0);
        byte schedulerState = Cpu.Data[schedulerStateAddress];
        byte result = (byte)(Cpu.Data[sourceAddress] | 0x20);
        return true switch
        {
            _ when schedulerState == 1 =>
                MiaIdleFastForwardBlockReason.SchedulerActive,
            _ when Cpu.Data[16] != result ||
                Cpu.Data[rampzAddress] != 0 ||
                Cpu.GetUint16(30) != 0x0ac0 ||
                Cpu.Data[targetAddress] != result ||
                Cpu.SREG != GetIdleLoopResultStatus(
                    Cpu.SREG,
                    schedulerState,
                    result) => MiaIdleFastForwardBlockReason.RegisterStateChanged,
            _ => MiaIdleFastForwardBlockReason.None,
        };
    }

    bool TryGetIdleFastForwardLoopCount(
        ArmModem? modem,
        bool constrainToModemEvent,
        long maximumTargetCycle,
        out long loopCount)
    {

        long nextEventCycle = Clock.NextEventCycle;
        if (constrainToModemEvent && modem is not null)
        {
            nextEventCycle = Math.Min(nextEventCycle, modem.Bus.NextEventCycle);
        }
        long maximumTarget = Clock.Cycles <=
            long.MaxValue - MaximumIdleAdvanceAsicCycles
                ? Clock.Cycles + MaximumIdleAdvanceAsicCycles
                : long.MaxValue;
        nextEventCycle = Math.Min(nextEventCycle, maximumTarget);
        nextEventCycle = Math.Min(nextEventCycle, maximumTargetCycle);

        long maximumCycles =
            Clock.GetMaximumAdditionalAvrCyclesBefore(nextEventCycle);
        loopCount = maximumCycles / IdleLoopCycleCount;
        if (loopCount == 0 ||
            loopCount > (long.MaxValue - Cpu.Cycles) / IdleLoopCycleCount ||
            loopCount > (long.MaxValue - ExecutedInstructions) /
                IdleLoopInstructionCount)
        {
            RecordIdleFastForwardBlock(
                candidate: true,
                reason: MiaIdleFastForwardBlockReason.NoWholeLoopBeforeDeadline);
            return false;
        }
        return true;
    }

    bool ApplyIdleFastForward(long loopCount)
    {
        long skippedCycles = loopCount * IdleLoopCycleCount;
        long skippedInstructions = loopCount * IdleLoopInstructionCount;
        Cpu.Cycles += skippedCycles;
        Clock.SynchronizeAvrCycles(Cpu.Cycles);
        ExecutedInstructions += skippedInstructions;
        IdleFastForwardCount++;
        IdleFastForwardedInstructions += skippedInstructions;
        RecordExecutionHotspot(
            _idleLoopStartWord,
            skippedInstructions,
            loopCount);
        RecordIdleFastForwardSuccess();
        return true;
    }

    void RecordExecutionHotspot(
        int programCounter,
        long executions,
        long backwardBranches)
    {
        if (_executionHotspots is not { } hotspots)
        {
            return;
        }

        hotspots.TryGetValue(programCounter, out var counts);
        counts.Executions += executions;
        counts.BackwardBranches += backwardBranches;
        hotspots[programCounter] = counts;
    }

    void RecordIdleFastForwardBlock(
        bool candidate,
        MiaIdleFastForwardBlockReason reason)
    {
        if (!candidate || !_idleFastForwardDiagnosticsEnabled)
        {
            return;
        }

        _idleFastForwardCandidateCount++;
        _lastIdleFastForwardBlockReason = reason;
        _idleFastForwardBlockCounts![(int)reason]++;
    }

    void RecordIdleFastForwardSuccess()
    {
        if (!_idleFastForwardDiagnosticsEnabled)
        {
            return;
        }

        _idleFastForwardCandidateCount++;
        _idleFastForwardSuccessCount++;
        _lastIdleFastForwardBlockReason =
            MiaIdleFastForwardBlockReason.None;
    }

    bool MatchesIdleLoopSignature()
    {
        var words = MiaFirmwareRuntimeHooks.IdleLoopWords;
        if (_idleLoopStartWord < 0 ||
            _idleLoopStartWord + words.Length > Cpu.ProgWords)
        {
            return false;
        }
        for (var index = 0; index < words.Length; index++)
        {
            if (Cpu.GetProgWord(_idleLoopStartWord + index) != words[index])
            {
                return false;
            }
        }
        return true;
    }

    bool IsIdleLoopInstruction(int wordAddress) =>
        _idleLoopStartWord >= 0 &&
        wordAddress >= _idleLoopStartWord &&
        wordAddress < _idleLoopStartWord +
            MiaFirmwareRuntimeHooks.IdleLoopWords.Length &&
        wordAddress != _idleLoopStartWord + 4;

    static byte GetIdleLoopResultStatus(
        byte initialStatus,
        byte schedulerState,
        byte result)
    {
        int comparison = schedulerState - 1;
        int status = initialStatus & 0x40;
        status |= comparison != 0 ? 0 : 2;
        status |= (comparison & 0x80) != 0 ? 4 : 0;
        status |= ((schedulerState ^ 1) & (schedulerState ^ comparison) & 0x80) != 0
            ? 8
            : 0;
        status |= (((status >> 2) & 1) ^ ((status >> 3) & 1)) != 0 ? 0x10 : 0;
        status |= 1 > schedulerState ? 1 : 0;
        status |= (1 & ((~schedulerState & 1) | (1 & comparison) |
            (comparison & ~schedulerState))) != 0
                ? 0x20
                : 0;

        status &= 0xe1;
        status |= result != 0 ? 0 : 2;
        status |= (result & 0x80) != 0 ? 4 : 0;
        status |= (result & 0x80) != 0 ? 0x10 : 0;
        return (byte)(status | 0x80);
    }

    void OnDisplayTransactionCompleted(byte address, byte[] payload)
    {
        var isScanRow =
            address == S4595Display.WriteAddress &&
            payload.Length > 1 &&
            payload[0] == S4595Display.ScanRowCommand;
        bool changesScanout = address == S4595Display.WriteAddress &&
            !isScanRow && ChangesDisplayScanout(payload);
        byte[] snapshot = isScanRow || changesScanout
            ? _displayWorker.Invoke(() => Display.Framebuffer.ToArray())
            : [];
        Diagnostics.PublishDisplayTransaction(new(
            address,
            payload.ToArray(),
            snapshot,
            DisplayI2c.LastWriteCycle,
            DisplayI2c.LastWritePc,
            AsicRom.GetActiveSchedulerRecordAddress(Cpu),
            DisplayI2c.LastDmaCycle,
            DisplayI2c.LastDmaPc,
            DisplayI2c.LastDmaSource,
            DisplayI2c.LastDmaLength));

        if (!isScanRow && !changesScanout)
        {
            return;
        }

        Volatile.Write(ref _frame, snapshot);
        Interlocked.Increment(ref _frameVersion);
        var framePublished = FramePublished;
        if (framePublished is not null)
        {
            _hostOutputWorker.Post(() => framePublished(snapshot));
        }
    }

    static bool ChangesDisplayScanout(ReadOnlySpan<byte> payload)
    {
        for (int index = 0; index + 1 < payload.Length; index += 2)
        {
            if (payload[index] is S4595Display.ModeRegister or
                S4595Display.PartialRowCountRegister or S4595Display.PartialRowStartRegister)
            {
                return true;
            }
        }
        return false;
    }

    void OnPersistenceChanged(long version)
    {
        var persistenceChanged = PersistenceChanged;
        if (persistenceChanged is not null)
        {
            _hostOutputWorker.Post(() => persistenceChanged(version));
        }
    }

    void OnBluetoothEmulationStatusChanged(
        MiaBluetoothEmulationStatus status)
    {
        var statusChanged = BluetoothEmulationStatusChanged;
        if (statusChanged is not null)
        {
            _hostOutputWorker.Post(() => statusChanged(status));
        }
    }

    void OnCommuniCamStatusChanged(MiaCommuniCamStatus status)
    {
        var statusChanged = CommuniCamStatusChanged;
        if (statusChanged is not null)
        {
            _hostOutputWorker.Post(() => statusChanged(status));
        }
    }

    MiaWorker CreateWorker(string name)
    {
        var worker = new MiaWorker(
            name,
            dedicatedThread: _dedicatedWorkers);
        _workers.Add(worker);
        return worker;
    }

    internal void FlushHostEvents() => _hostOutputWorker.Invoke(() => { });

    internal void PostHostEvent(Action callback) =>
        _hostOutputWorker.Post(callback);

    void ReleaseBootPowerKey()
    {
        Keypad.Release(AsicKeypad.NoPowerScanMask, AsicKeypad.NoPowerRowMask);
        PowerPorts.ReleasePower();
        InterruptController.RaiseHighPriority(AsicInterruptController.KeypadSource);
    }

    void Stop(string reason)
    {
        StopReason = reason;
        IsStopped = true;
    }

    static bool IsErasedFlashRun(Cpu cpu, int startWord, int firstWord)
    {
        const int evidenceWords = 8;
        if (startWord < 0 || startWord + evidenceWords > cpu.ProgWords)
        {
            return false;
        }
        if (firstWord != 0xffff)
        {
            return false;
        }
        for (var offset = 1; offset < evidenceWords; offset++)
        {
            if (cpu.GetProgWord(startWord + offset) != 0xffff)
            {
                return false;
            }
        }
        return true;
    }
}
