// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Firmware-visible channel-decoder handshakes. The proven cases consume an
/// SCH or 456-soft-bit control-channel input from bank 0x10, optionally write
/// only source-derived output bytes, publish the failure bit, and raise the
/// native decoder-done source. The recovered scheduled TCH/F receive path
/// publishes FACCH through its separate mapped-SRAM result latch. Unknown
/// commands and control sequences remain inert.
/// </summary>
internal sealed class AsicChannelDecoder
{
    public const int ControlAddress = 0x0940;
    public const int CommandAddress = 0x0941;
    public const int StatusAddress = 0x094b;
    public const byte SchCommand = 0xac;
    public const byte SchArmedControl = 0x50;
    public const byte SchStartControl = 0x52;
    public const byte ControlChannelArmedControl = 0x40;
    public const byte ControlChannelStartControl = 0x42;
    public const byte FailureMask = 0x80;
    public const int SchInputAddress = 0x100676;
    public const int SchInputLength = 0x4e;
    public const int SchOutputAddress = 0x1006c4;
    public const int SchOutputLength = 4;
    public const int ControlChannelInputAddress = 0x1018f9;
    public const int ControlChannelInputLength = 0x01c8;
    public const int ControlChannelOutputAddress = 0x1006c8;
    public const int ControlChannelOutputLength = 0x17;
    public const int TrafficFacchOutputAddress = 0x1006df;
    public const int TrafficFacchOutputLength = 0x17;
    public const int TrafficFacchReadyAddress = 0x02a6ce;
    public const int FirmwareStateAddress = 0x02a6e9;
    public const int ResultFailureAddress = 0x02a6c5;
    public const byte TrafficFacchFirmwareState = 0x0d;
    public const int CompletionCycles = 1;

    readonly Cpu _cpu;
    readonly MiaSystemClock _clock;
    readonly AsicInterruptController _interruptController;
    readonly IAsicChannelDecoderSource _source;
    readonly MiaWorker? _worker;
    byte _command;
    byte _control;
    byte[]? _pendingInput;
    byte _pendingFirmwareState;
    AsicChannelDecoderOperation _pendingOperation;

    public AsicChannelDecoder(
        Cpu cpu,
        MiaSystemClock clock,
        AsicInterruptController interruptController,
        IAsicChannelDecoderSource? source = null,
        MiaWorker? worker = null,
        AsicTimeGenerator? timeGenerator = null)
    {
        _cpu = cpu;
        _clock = clock;
        _interruptController = interruptController;
        _source = source ?? AsicNoSignalChannelDecoderSource.Instance;
        _worker = worker;

        cpu.WriteHooks[CommandAddress] = WriteCommand;
        cpu.WriteHooks[ControlAddress] = WriteControl;
        if (timeGenerator is not null)
        {
            timeGenerator.ActionExecuted += HandleScheduledAction;
        }
    }

    public long StartedCount { get; private set; }

    public long CompletedCount { get; private set; }

    public long SuccessfulCount { get; private set; }

    public long SchStartedCount { get; private set; }

    public long ControlChannelStartedCount { get; private set; }

    public long ScheduledTrafficFacchStartedCount { get; private set; }

    bool WriteCommand(byte value, byte _, int __, byte mask)
    {
        _command = Merge(_command, value, mask);
        return false;
    }

    void HandleScheduledAction(AsicTimeGeneratorActionExecution action)
    {
        if (action.ActionId != AsicTimeGenerator.ScheduledDecoderActionId ||
            action.Operand != 0)
        {
            return;
        }

        if (_worker is null)
        {
            StartScheduledControlChannelDecode();
        }
        else
        {
            _clock.Schedule(_worker, StartScheduledControlChannelDecode, 1);
        }
    }

    void StartScheduledControlChannelDecode()
    {
        if (_pendingInput is not null ||
            _cpu.Data[FirmwareStateAddress] != TrafficFacchFirmwareState)
        {
            return;
        }

        // TCH/F receive is scheduled by the traffic subsystem, independently
        // of the ordinary bank-2 decoder descriptor. The private soft-bit
        // transport is unknown, so this compatibility boundary reuses the
        // firmware-visible 456-byte radio workspace while preserving the
        // recovered token, state, result latch, status, and IRQ contract.
        _pendingOperation = AsicChannelDecoderOperation.TrafficFacch;
        _pendingInput = _cpu.Data.AsSpan(
            ControlChannelInputAddress,
            ControlChannelInputLength).ToArray();
        _pendingFirmwareState = _cpu.Data[FirmwareStateAddress];
        ScheduledTrafficFacchStartedCount++;
        ControlChannelStartedCount++;
        StartedCount++;
        ScheduleCompletion();
    }

    bool WriteControl(byte value, byte _, int __, byte mask)
    {
        var previous = _control;
        _control = Merge(_control, value, mask);
        if (_pendingInput is not null || _command != SchCommand)
        {
            return false;
        }

        var operation = ResolveOperation(previous, _control);
        if (operation is not { } pending)
        {
            return false;
        }

        _pendingOperation = pending.Operation;
        _pendingInput = _cpu.Data.AsSpan(pending.InputAddress, pending.InputLength).ToArray();
        _pendingFirmwareState = _cpu.Data[FirmwareStateAddress];
        StartedCount++;
        CountStartedOperation(pending.Operation);
        ScheduleCompletion();
        return false;
    }

    static (AsicChannelDecoderOperation Operation, int InputAddress, int InputLength)?
        ResolveOperation(byte previous, byte current) => (previous, current) switch
        {
            (SchArmedControl, SchStartControl) =>
                (AsicChannelDecoderOperation.Sch, SchInputAddress, SchInputLength),
            (ControlChannelArmedControl, ControlChannelStartControl) =>
                (AsicChannelDecoderOperation.ControlChannel,
                    ControlChannelInputAddress,
                    ControlChannelInputLength),
            _ => null,
        };

    void CountStartedOperation(AsicChannelDecoderOperation operation)
    {
        SchStartedCount += operation == AsicChannelDecoderOperation.Sch ? 1 : 0;
        ControlChannelStartedCount +=
            operation == AsicChannelDecoderOperation.ControlChannel ? 1 : 0;
    }

    void ScheduleCompletion()
    {
        if (_worker is null)
        {
            _clock.Schedule(Complete, CompletionCycles);
            return;
        }

        _clock.Schedule(_worker, Complete, CompletionCycles);
    }

    void Complete()
    {
        var input = _pendingInput ?? throw new InvalidOperationException(
            "Channel-decoder completion has no captured input.");
        _pendingInput = null;
        var controlChannelOperation =
            _pendingOperation == AsicChannelDecoderOperation.ControlChannel;

        var (result, outputAddress, outputLength) = _pendingOperation switch
        {
            AsicChannelDecoderOperation.Sch => (
                _source.DecodeSch(input),
                SchOutputAddress,
                SchOutputLength),
            AsicChannelDecoderOperation.ControlChannel => (
                _source.DecodeControlChannel(new AsicControlChannelDecodeRequest(
                    input,
                    _pendingFirmwareState)),
                ControlChannelOutputAddress,
                ControlChannelOutputLength),
            AsicChannelDecoderOperation.TrafficFacch => (
                _source.DecodeControlChannel(new AsicControlChannelDecodeRequest(
                    input,
                    _pendingFirmwareState)),
                TrafficFacchOutputAddress,
                TrafficFacchOutputLength),
            _ => throw new InvalidOperationException(
                "Channel-decoder completion has no operation."),
        };
        var trafficFacchOperation =
            _pendingOperation == AsicChannelDecoderOperation.TrafficFacch;
        _pendingOperation = AsicChannelDecoderOperation.None;

        var success = result.Success && result.Bytes.Length == outputLength;
        if (success)
        {
            result.Bytes.Span.CopyTo(
                _cpu.Data.AsSpan(outputAddress, outputLength));
            _cpu.Data[StatusAddress] &= unchecked((byte)~FailureMask);
            SuccessfulCount++;
        }
        else
        {
            _cpu.Data[StatusAddress] |= FailureMask;
        }
        if (trafficFacchOperation)
        {
            // Hardware exposes whether the scheduled traffic block carried a
            // valid FACCH result. Firmware consumes this latch in its native
            // state-0x0d done handler and then derives ResultFailureAddress
            // from StatusAddress before the PH task reads the mapped result at
            // emulator address 0x1006df.
            _cpu.Data[TrafficFacchReadyAddress] = success ? (byte)1 : (byte)0;
        }

        CompletedCount++;
        _interruptController.RaiseHighPriority(
            AsicInterruptController.ChannelDecoderDoneSource);

        if (controlChannelOperation &&
            result.LateResultCompletionCycles is > 0)
        {
            _clock.Schedule(
                () => CompleteLateControlChannelResult(result),
                result.LateResultCompletionCycles.Value);
        }
    }

    void CompleteLateControlChannelResult(AsicChannelDecoderResult result)
    {
        // PH's late radio callback consumes this shared decoder result without
        // a second firmware-issued 0x0940/0x0941 transaction. Model that
        // hardware-owned latch only from the original decoder result; it does
        // not change any firmware task state or manufacture protocol bytes.
        var success = result.Success &&
            result.Bytes.Length == ControlChannelOutputLength;
        if (success)
        {
            result.Bytes.Span.CopyTo(_cpu.Data.AsSpan(
                ControlChannelOutputAddress,
                ControlChannelOutputLength));
            _cpu.Data[StatusAddress] &= unchecked((byte)~FailureMask);
            _cpu.Data[ResultFailureAddress] = 0;
            return;
        }

        _cpu.Data[StatusAddress] |= FailureMask;
        _cpu.Data[ResultFailureAddress] = 1;
    }

    static byte Merge(byte oldValue, byte value, byte mask) =>
        (byte)((oldValue & ~mask) | (value & mask));
}
