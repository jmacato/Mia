// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Firmware-visible PH initialization and channel-encoder command handshakes.
/// The compatibility encoder snapshots the dynamically recovered source
/// descriptor and completes through the native channel-encoder interrupt.
/// </summary>
internal sealed class AsicPhCommandController
{
    public const int CommandAddress = 0x0950;
    public const int StatusAddress = 0x0951;
    public const int ControlAddress = 0x095e;
    public const byte InitializationCommand = 0x0c;
    public const byte ChannelEncodeCommand = 0x2c;
    public const byte AcknowledgeCommand = 0x03;
    public const byte CommandEnable = 0x02;
    public const byte ScheduledCommandEnable = 0x04;
    public const byte CommandComplete = 0x04;
    public const int CommandCompletionCycles = 1;
    public const int EncoderContextAddress = 0x02a634;
    public const int EncoderInputPointerOffset = 0x2a;
    public const int EncoderInputLengthOffset = 0x2d;

    readonly Cpu _cpu;
    readonly MiaSystemClock _clock;
    readonly AsicInterruptController? _interruptController;
    readonly IAsicChannelEncoderSink? _channelEncoderSink;
    readonly MiaWorker? _worker;
    byte _command;
    byte _control;
    byte _status;
    bool _commandPending;
    byte[]? _pendingEncoderInput;
    byte _pendingEncoderState;

    public AsicPhCommandController(
        Cpu cpu,
        MiaSystemClock clock,
        AsicInterruptController? interruptController = null,
        IAsicChannelEncoderSink? channelEncoderSink = null,
        MiaWorker? worker = null,
        AsicTimeGenerator? timeGenerator = null)
    {
        _cpu = cpu;
        _clock = clock;
        _interruptController = interruptController;
        _channelEncoderSink = channelEncoderSink;
        _worker = worker;
        cpu.WriteHooks[CommandAddress] = WriteCommand;
        cpu.ReadHooks[StatusAddress] = _ => _status;
        cpu.WriteHooks[ControlAddress] = WriteControl;
        if (timeGenerator is not null)
        {
            timeGenerator.ActionExecuted += HandleScheduledAction;
        }
    }

    public byte Status => _status;

    public long StartedCommands { get; private set; }

    public long CompletedCommands { get; private set; }

    bool WriteCommand(byte value, byte oldValue, int _, byte mask)
    {
        _command = (byte)((oldValue & ~mask) | (value & mask));
        if (_command is 0 or AcknowledgeCommand)
        {
            _status &= unchecked((byte)~CommandComplete);
            _cpu.Data[StatusAddress] = _status;
        }
        return false;
    }

    bool WriteControl(byte value, byte oldValue, int _, byte mask)
    {
        _control = (byte)((oldValue & ~mask) | (value & mask));
        if (!CanStartCommand())
        {
            return false;
        }

        StartPendingCommand();
        return false;
    }

    bool CanStartCommand() =>
        !_commandPending &&
        (_status & CommandComplete) == 0 &&
        _command is InitializationCommand or ChannelEncodeCommand &&
        (_control & CommandEnable) != 0;

    void StartPendingCommand()
    {
        if (_command == ChannelEncodeCommand)
        {
            _pendingEncoderInput = CaptureEncoderInput();
            _pendingEncoderState = _cpu.Data[EncoderContextAddress];
        }

        _commandPending = true;
        StartedCommands++;
        ScheduleCommandCompletion();
    }

    void HandleScheduledAction(AsicTimeGeneratorActionExecution action)
    {
        if (action.ActionId != AsicTimeGenerator.ScheduledEncoderActionId ||
            action.Operand != 0)
        {
            return;
        }

        if (_worker is null)
        {
            StartScheduledChannelEncode();
        }
        else
        {
            _clock.Schedule(_worker, StartScheduledChannelEncode, 1);
        }
    }

    void StartScheduledChannelEncode()
    {
        if (_commandPending ||
            _command != ChannelEncodeCommand ||
            (_control & ScheduledCommandEnable) == 0)
        {
            return;
        }

        _pendingEncoderInput = CaptureEncoderInput();
        _pendingEncoderState = _cpu.Data[EncoderContextAddress];
        _commandPending = true;
        StartedCommands++;
        ScheduleCommandCompletion();
    }

    void ScheduleCommandCompletion()
    {
        if (_worker is null)
        {
            _clock.Schedule(CompleteCommand, CommandCompletionCycles);
            return;
        }

        _clock.Schedule(_worker, CompleteCommand, CommandCompletionCycles);
    }

    void CompleteCommand()
    {
        _commandPending = false;
        switch (_command)
        {
            case InitializationCommand:
                CompleteInitialization();
                break;
            case ChannelEncodeCommand:
                CompleteChannelEncode();
                break;
        }
        CompletedCommands++;
    }

    void CompleteInitialization()
    {
        _status |= CommandComplete;
        _cpu.Data[StatusAddress] = _status;
    }

    void CompleteChannelEncode()
    {
        if (_pendingEncoderInput is { } input)
        {
            _channelEncoderSink?.Encode(new AsicChannelEncoderRequest(
                input,
                _pendingEncoderState));
            _pendingEncoderInput = null;
        }
        _interruptController?.RaiseHighPriority(
            AsicInterruptController.ChannelEncoderDoneSource);
    }

    byte[]? CaptureEncoderInput()
    {
        var context = EncoderContextAddress;
        if (context + EncoderInputLengthOffset + 1 >= _cpu.Data.Length)
        {
            return null;
        }

        var pointerOffset = context + EncoderInputPointerOffset;
        var address = _cpu.Data[pointerOffset] |
            _cpu.Data[pointerOffset + 1] << 8 |
            _cpu.Data[pointerOffset + 2] << 16;
        var lengthOffset = context + EncoderInputLengthOffset;
        var length = _cpu.Data[lengthOffset] |
            _cpu.Data[lengthOffset + 1] << 8;
        if (length <= 0 || address > _cpu.Data.Length - length)
        {
            return null;
        }

        return _cpu.Data.AsSpan(address, length).ToArray();
    }
}
