// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Ordered RF setup transaction and ADC sample-source boundary. Firmware
/// commits one configuration per TG slot through 0x0a88..0x0a96; later ADC
/// actions carry the same slot selector back to the configured signal source.
/// </summary>
internal sealed class AsicRfFrontend : IAsicAdcSampleSource
{
    public const int FirstTransactionAddress = 0x0a88;
    public const int LastTransactionAddress = 0x0a96;
    public const int TransactionSize =
        LastTransactionAddress - FirstTransactionAddress + 1;
    public const byte MaximumSlot = 5;

    // The band-zero synthesizer table is exactly linear in the firmware:
    // raw = 0xdd78 + 0x28 * (ARFCN + 216), for the accepted -49..124 range.
    const ushort BandZeroSynthesizerBase = 0xdd78;
    const int BandZeroNormalizedOffset = 216;
    const int SynthesizerStep = 0x28;
    public const short BandZeroFirstArfcn = -49;
    public const short BandZeroLastArfcn = 124;

    readonly Cpu _cpu;
    readonly IAsicRfSignalSource _signalSource;
    readonly MiaWorker? _worker;
    readonly byte[] _staging = new byte[TransactionSize];
    readonly Dictionary<byte, AsicRfTransaction> _transactions = [];
    int _nextTransactionAddress = FirstTransactionAddress;

    public AsicRfFrontend(
        Cpu cpu,
        IAsicRfSignalSource? signalSource = null,
        MiaWorker? worker = null)
    {
        _cpu = cpu;
        _signalSource = signalSource ?? AsicNoSignalRfSource.Instance;
        _worker = worker;

        for (var address = FirstTransactionAddress;
             address <= LastTransactionAddress;
             address++)
        {
            var previous = cpu.WriteHooks[address];
            cpu.WriteHooks[address] = (value, oldValue, hookAddress, mask) =>
            {
                CaptureWrite(
                    hookAddress,
                    (byte)((oldValue & ~mask) | (value & mask)));
                return previous?.Invoke(value, oldValue, hookAddress, mask) ?? false;
            };
        }
    }

    public long CommittedTransactionCount { get; private set; }

    public event Action<AsicRfTransaction>? TransactionCommitted;

    public bool TryGetTransaction(byte slot, out AsicRfTransaction? transaction)
    {
        AsicRfTransaction? found = null;
        var result = Invoke(() => _transactions.TryGetValue(slot, out found));
        transaction = found;
        return result;
    }

    public AsicRfTransaction[] GetTransactionsSnapshot() => Invoke(() =>
        _transactions.Values
            .OrderBy(transaction => transaction.Slot)
            .ToArray());

    public ushort ReadRawSample(
        byte adcSelector,
        AsicTimeGeneratorActionExecution action) => Invoke(() =>
    {
        var slot = (byte)(action.ProgramSelector & 0x3f);
        return _transactions.TryGetValue(slot, out var transaction)
            ? _signalSource.ReadRawSample(adcSelector, transaction, action)
            : (ushort)0;
    });

    public static bool TryDecodeBandZeroArfcn(
        byte programSelector,
        AsicRfTransaction transaction,
        out short arfcn)
    {
        if ((programSelector & 0xc0) != 0 || !transaction.HasDuplicatedTuneWord)
        {
            arfcn = 0;
            return false;
        }

        for (var candidate = BandZeroFirstArfcn;
             candidate <= BandZeroLastArfcn;
             candidate++)
        {
            var normalized = candidate + BandZeroNormalizedOffset;
            var raw = unchecked((ushort)(
                BandZeroSynthesizerBase + SynthesizerStep * normalized));
            if (transaction.TuneLow == (byte)raw &&
                transaction.TuneHigh == EncodeSynthesizerHighByte((byte)(raw >> 8)))
            {
                arfcn = candidate;
                return true;
            }
        }

        arfcn = 0;
        return false;
    }

    void CaptureWrite(int address, byte value)
    {
        if (address == FirstTransactionAddress)
        {
            _staging[0] = value;
            _nextTransactionAddress = FirstTransactionAddress + 1;
            return;
        }
        if (address != _nextTransactionAddress)
        {
            _nextTransactionAddress = FirstTransactionAddress;
            return;
        }

        ContinueTransaction(address, value);
    }

    void ContinueTransaction(int address, byte value)
    {
        _staging[address - FirstTransactionAddress] = value;
        _nextTransactionAddress++;
        if (address != LastTransactionAddress)
        {
            return;
        }

        _nextTransactionAddress = FirstTransactionAddress;
        CommitTransaction();
    }

    void CommitTransaction()
    {
        var slot = _staging[0];
        if (slot > MaximumSlot)
        {
            return;
        }

        var transaction = new AsicRfTransaction(slot, _staging.ToArray());
        _transactions[slot] = transaction;
        CommittedTransactionCount++;
        TransactionCommitted?.Invoke(transaction);
    }

    static byte EncodeSynthesizerHighByte(byte value) =>
        (byte)((value & 0x3f) | ((value & 0x80) >> 1));

    TResult Invoke<TResult>(Func<TResult> function)
    {
        if (_worker is null || _worker.IsCurrentThread)
        {
            return function();
        }
        return _worker.Invoke(function);
    }
}
