// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

/// <summary>
/// Connects the transport-independent <see cref="SwSimCard"/> model to the
/// Ericsson ASIC's firmware-visible SIM UART, timing, and interrupt protocol.
/// </summary>
internal sealed class GsmSimCard
{
    public const int AtrStartDelayCardClocks = 400;
    public const int CpuCyclesPerInitialCardClock = 4;
    public const int AtrStartDelayCycles =
        AtrStartDelayCardClocks * CpuCyclesPerInitialCardClock;

    // This is the simplest valid direct-convention T=0 ATR. Keep interface
    // negotiation out of the path until the ASIC's clock/prescaler registers
    // and the firmware's PPS state machine have been recovered dynamically.
    static readonly byte[] MiaAnswerToReset = [0x3b, 0x00];

    readonly AsicSimInterface _simInterface;
    readonly SwSimCard _card;
    readonly MiaWorker? _worker;

    public GsmSimCard(
        AsicSimInterface simInterface,
        string? imsi = null,
        string? serviceProviderName = null,
        MiaWorker? worker = null)
    {
        _simInterface = simInterface;
        _worker = worker;
        _card = new SwSimCard(
            imsi,
            MiaAnswerToReset,
            serviceProviderName);
        _card.CommandReceived += OnCommandReceived;
        _card.PersistenceChanged += version =>
            PersistenceChanged?.Invoke(version);
        simInterface.ControlChanged += OnControlChanged;
        simInterface.ByteTransmitted += OnByteTransmitted;
    }

    public long ActivationCount { get; private set; }

    public long CommandCount { get; private set; }

    public string Imsi => Invoke(() => _card.Imsi);

    public long PersistenceVersion => Invoke(() => _card.PersistenceVersion);

    public void ApplyOverlay(IEnumerable<SimFileOverlay> overlays)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        SimFileOverlay[] snapshot = overlays.ToArray();
        Invoke(() => _card.ApplyOverlay(snapshot));
    }

    public SimFileOverlay[] CreateOverlay() => Invoke(_card.CreateOverlay);

    public (long Version, SimFileOverlay[] Overlay) CapturePersistence() =>
        Invoke(() => (_card.PersistenceVersion, _card.CreateOverlay()));

    public event Action<byte[]>? CommandReceived;

    public event Action<long>? PersistenceChanged;

    void OnControlChanged(byte oldValue, byte newValue)
    {
        if ((oldValue & 0x04) == 0 && (newValue & 0x04) != 0)
        {
            Activate();
        }
    }

    void Activate()
    {
        var atr = Invoke(() =>
        {
            ActivationCount++;
            return _card.AnswerToReset().ToArray();
        });
        _simInterface.ScheduleReceivedBytes(
            atr,
            AtrStartDelayCycles,
            _simInterface.TransmitCompletionCycles);
    }

    void OnByteTransmitted(byte value)
    {
        SimCardResponse? response = Invoke(() => _card.Transmit(value));
        if (response is null || response.Value.Data.Length == 0)
        {
            return;
        }

        _simInterface.ScheduleReceivedBytes(
            response.Value.Data,
            _simInterface.TransmitCompletionCycles,
            _simInterface.TransmitCompletionCycles);
    }

    void OnCommandReceived(byte[] command)
    {
        CommandCount++;
        CommandReceived?.Invoke(command);
    }

    TResult Invoke<TResult>(Func<TResult> function)
    {
        if (_worker is null || _worker.IsCurrentThread)
        {
            return function();
        }
        return _worker.Invoke(function);
    }

    void Invoke(Action action)
    {
        if (_worker is null || _worker.IsCurrentThread)
        {
            action();
            return;
        }
        _worker.Invoke(action);
    }
}
