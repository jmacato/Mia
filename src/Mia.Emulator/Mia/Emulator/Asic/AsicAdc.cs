// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Firmware-visible five-channel ADC serializer. Time-generator actions
/// complete conversions into selector-local two-byte FIFOs and raise the
/// native ADC-done source; sample values come from an explicit RF/analog
/// source rather than firmware state.
/// </summary>
internal sealed class AsicAdc : IDisposable
{
    public const int ControlAddress = 0x0960;
    public const int SetupStreamAddress = 0x0961;
    public const int ResultStreamAddress = 0x0962;
    public const int ConfigurationStreamAddress = 0x0963;
    public const int TimingStreamAddress = 0x0964;
    public const int GateStreamAddress = 0x0965;
    public const int SelectorAddress = 0x0966;
    public const int SelectorCount = 5;
    internal const int MaximumPendingResults = 16;
    public const byte FirstActionId = 52;
    public const byte LastActionId = 60;
    public const byte SchActionId = 8;
    public const ushort SchActionOperand = 474;

    readonly Cpu _cpu;
    readonly AsicTimeGenerator _timeGenerator;
    readonly AsicInterruptController _interruptController;
    readonly IAsicAdcSampleSource _sampleSource;
    readonly MiaWorker? _worker;
    readonly Queue<ushort>[] _resultFifos =
        Enumerable.Range(0, SelectorCount).Select(_ => new Queue<ushort>()).ToArray();
    readonly byte[] _resultReadPhases = new byte[SelectorCount];
    readonly byte[] _controlLatches = new byte[SelectorCount];
    readonly byte[] _emptyResultLatches = new byte[SelectorCount];
    byte _selectorRegister;
    bool _disposed;

    public AsicAdc(
        Cpu cpu,
        AsicTimeGenerator timeGenerator,
        AsicInterruptController interruptController,
        IAsicAdcSampleSource? sampleSource = null,
        MiaWorker? worker = null)
    {
        _cpu = cpu;
        _timeGenerator = timeGenerator;
        _interruptController = interruptController;
        _sampleSource = sampleSource ?? AsicNoSignalAdcSampleSource.Instance;
        _worker = worker;

        cpu.ReadHooks[ControlAddress] = ReadControl;
        cpu.WriteHooks[ControlAddress] = WriteControl;
        cpu.ReadHooks[ResultStreamAddress] = ReadResult;
        cpu.WriteHooks[ResultStreamAddress] = WriteResult;
        cpu.ReadHooks[SelectorAddress] = _ => _selectorRegister;
        cpu.WriteHooks[SelectorAddress] = WriteSelector;
        _timeGenerator.ActionExecuted += OnActionExecuted;
    }

    public long CompletionCount { get; private set; }

    public long ResultReadCount { get; private set; }

    public byte SelectedAdc => (byte)((_selectorRegister >> 2) & 0x07);

    public int GetPendingResultCount(byte adcSelector)
    {
        if (adcSelector >= SelectorCount)
        {
            throw new ArgumentOutOfRangeException(nameof(adcSelector));
        }
        return Invoke(() => _resultFifos[adcSelector].Count);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _timeGenerator.ActionExecuted -= OnActionExecuted;
        _disposed = true;
    }

    byte ReadControl(int _)
    {
        var selector = SelectedAdc;
        return selector < SelectorCount
            ? _controlLatches[selector]
            : _cpu.Data[ControlAddress];
    }

    bool WriteControl(byte value, byte _, int __, byte mask)
    {
        var selector = SelectedAdc;
        if (selector < SelectorCount)
        {
            _controlLatches[selector] = Merge(
                _controlLatches[selector],
                value,
                mask);
        }
        return false;
    }

    byte ReadResult(int _)
    {
        var selector = SelectedAdc;
        if (selector >= SelectorCount)
        {
            return _cpu.Data[ResultStreamAddress];
        }

        var fifo = _resultFifos[selector];
        if (fifo.Count == 0)
        {
            _resultReadPhases[selector] = 0;
            return _emptyResultLatches[selector];
        }

        var result = fifo.Peek();
        var highByte = _resultReadPhases[selector] != 0;
        var value = highByte ? (byte)(result >> 8) : (byte)result;
        if (highByte)
        {
            fifo.Dequeue();
            _resultReadPhases[selector] = 0;
        }
        else
        {
            _resultReadPhases[selector] = 1;
        }
        ResultReadCount++;
        return value;
    }

    bool WriteResult(byte value, byte _, int __, byte mask)
    {
        var selector = SelectedAdc;
        if (selector < SelectorCount)
        {
            _emptyResultLatches[selector] = Merge(
                _emptyResultLatches[selector],
                value,
                mask);
        }
        return false;
    }

    bool WriteSelector(byte value, byte _, int __, byte mask)
    {
        _selectorRegister = Merge(_selectorRegister, value, mask);
        return false;
    }

    void OnActionExecuted(AsicTimeGeneratorActionExecution action)
    {
        var isPowerScanConversion =
            action.ActionId >= FirstActionId &&
            action.ActionId <= LastActionId &&
            (action.ActionId & 1) == 0 &&
            action.OccurrenceCount == 2 &&
            action.OccurrenceIndex == 1;
        var isSchConversion =
            action.ActionId == SchActionId &&
            action.Operand == SchActionOperand &&
            action.OccurrenceCount == 1 &&
            action.OccurrenceIndex == 0;
        if (!isPowerScanConversion && !isSchConversion)
        {
            return;
        }

        Invoke(() =>
        {
            var selector = isSchConversion
                ? SelectedAdc
                : (byte)((action.ActionId - FirstActionId) / 2);
            if (selector < SelectorCount)
            {
                CompleteConversion(selector, action);
            }
        });
    }

    void CompleteConversion(
        byte selector,
        AsicTimeGeneratorActionExecution action)
    {
        var result = _sampleSource.ReadRawSample(selector, action);
        // Preserve any half-read result on overrun, including its high byte.
        if (_resultFifos[selector].Count < MaximumPendingResults)
        {
            _resultFifos[selector].Enqueue(result);
        }
        CompletionCount++;
        _interruptController.RaiseHighPriority(
            AsicInterruptController.AdcDoneSource);
    }

    static byte Merge(byte oldValue, byte value, byte mask) =>
        (byte)((oldValue & ~mask) | (value & mask));

    void Invoke(Action action)
    {
        if (_worker is null || _worker.IsCurrentThread)
        {
            action();
        }
        else
        {
            _worker.Invoke(action);
        }
    }

    TResult Invoke<TResult>(Func<TResult> function)
    {
        if (_worker is null || _worker.IsCurrentThread)
        {
            return function();
        }
        return _worker.Invoke(function);
    }
}
