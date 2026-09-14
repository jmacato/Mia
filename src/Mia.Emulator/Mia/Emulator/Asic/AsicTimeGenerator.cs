// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Firmware-visible command, schedule, and frame-rollover portion of the ASIC
/// time-generator block. Definitions provide absolute quarter-bit compare
/// times; the effects of actions other than explicitly modeled consumers
/// remain opaque.
/// </summary>
internal sealed class AsicTimeGenerator
{
    public const int FrameControlAddress = 0x08c0;
    public const int ActionProgramAddress = 0x08c3;
    public const byte ActionProgramAdvance = 0x01;
    public const byte ActionProgramAdvancePrefix = 0x09;
    public const byte ActionProgramFrameControl = 0x89;
    public const byte ScheduledEncoderActionToken = 0x64;
    public const byte ScheduledEncoderActionId = 0x24;
    public const byte ScheduledDecoderActionToken = 0x65;
    public const byte ScheduledDecoderActionId = 0x25;
    public const int ActionDefinitionSelectorAddress = 0x08c6;
    public const int ActionDefinitionDataAddress = 0x08c7;
    public const int ActionSelectorAddress = 0x08c9;
    public const byte FrameRolloverEnablePattern = 0xe0;
    public const int FrameRolloverCycles = 60_000;
    public const int QuarterBitCycles = 12;
    public const int FrameQuarterBits = FrameRolloverCycles / QuarterBitCycles;
    public const byte PhProcessPhysicalSource = 0x32;
    public const byte PhDispatcherPhysicalSource = 0x2e;
    public const int FirstSchedulePortAddress = 0x0880;
    public const int LastSchedulePortAddress = 0x0886;
    public const int SchedulePortCount =
        LastSchedulePortAddress - FirstSchedulePortAddress + 1;
    public const int DescriptorSize = 3;
    public const int FirstActionSchedulePortAddress = 0x0881;
    public const int LastActionSchedulePortAddress = 0x0885;
    public const int ActionSchedulePortCount =
        LastActionSchedulePortAddress - FirstActionSchedulePortAddress + 1;
    public const int DirectCommandPortAddress = 0x088e;
    public const int ConfigurationCommandPortAddress = 0x0890;
    public const int CommandStatusAddress = 0x0895;
    public const byte ScheduleCommit = 0x80;
    public const byte ConfigurationCommit = 0x10;
    public const byte CommandMask = ScheduleCommit | ConfigurationCommit;

    // The exact internal clock-domain latency is not yet known. One ASIC tick
    // models the recovered self-clearing command handshake without claiming an
    // external radio/timer event or inventing an interrupt.
    public const int CommandCompletionCycles = 1;

    readonly Cpu _cpu;
    readonly MiaSystemClock _clock;
    readonly AsicInterruptRouter _interruptRouter;
    readonly Action _frameRollover;
    readonly Action _drainPendingTransactions;
    readonly MiaWorker? _worker;
    // These byte latches are the AVR-facing serializer. The time-generator
    // owner receives one complete descriptor transaction rather than three
    // separate host-thread roundtrips.
    readonly byte[][] _pendingDescriptorBytes = CreatePendingDescriptorBytes();
    readonly byte[] _pendingDescriptorLengths = new byte[SchedulePortCount];
    readonly Channel<AsicTimeGeneratorPendingTransaction> _pendingTransactions =
        Channel.CreateUnbounded<AsicTimeGeneratorPendingTransaction>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    int _pendingTransactionCount;
    int _pendingDrainScheduledFlag;
    readonly Dictionary<int, AsicTimeGeneratorDescriptor> _descriptors = [];
    readonly Dictionary<byte, AsicTimeGeneratorActionProgram> _actionPrograms = [];
    readonly Dictionary<byte, AsicTimeGeneratorActionDefinition> _actionDefinitions = [];
    readonly Dictionary<byte, AsicTimeGeneratorActionDefinition>
        _actionDefinitionsById = [];
    readonly byte[] _latestActionProgramSelectors =
        new byte[ActionSchedulePortCount];
    readonly bool[] _hasLatestActionProgram = new bool[ActionSchedulePortCount];
    readonly long[] _scheduleGenerations = new long[ActionSchedulePortCount];
    readonly List<byte> _pendingActionBytes = [];
    readonly List<byte> _pendingActionDefinitionBytes = [];
    bool _actionProgramAdvancePrefixPending;
    bool _actionProgramAdvancePending;
    byte _commandStatus;
    bool _frameRolloverScheduled;
    long _currentFrameStartCycle = -1;

    public AsicTimeGenerator(
        Cpu cpu,
        MiaSystemClock clock,
        AsicInterruptRouter interruptRouter,
        MiaWorker? worker = null)
    {
        _cpu = cpu;
        _clock = clock;
        _interruptRouter = interruptRouter;
        _frameRollover = FrameRollover;
        _drainPendingTransactions = DrainPendingTransactionsCore;
        _worker = worker;

        var previousControlHook = cpu.WriteHooks[FrameControlAddress];
        cpu.WriteHooks[FrameControlAddress] = (value, oldValue, address, mask) =>
        {
            var newValue = (byte)((oldValue & ~mask) | (value & mask));
            SetFrameRolloverEnabled(
                (newValue & FrameRolloverEnablePattern) == FrameRolloverEnablePattern);
            return previousControlHook?.Invoke(value, oldValue, address, mask) ?? false;
        };

        for (var address = FirstSchedulePortAddress;
             address <= LastSchedulePortAddress;
             address++)
        {
            var previousHook = cpu.WriteHooks[address];
            cpu.WriteHooks[address] = (value, oldValue, hookAddress, mask) =>
            {
                CaptureDescriptorByte(
                    hookAddress,
                    (byte)((oldValue & ~mask) | (value & mask)));
                return previousHook?.Invoke(value, oldValue, hookAddress, mask) ?? false;
            };
        }
        var previousActionHook = cpu.WriteHooks[ActionProgramAddress];
        cpu.WriteHooks[ActionProgramAddress] = (value, oldValue, address, mask) =>
        {
            CaptureActionByte((byte)((oldValue & ~mask) | (value & mask)));
            return previousActionHook?.Invoke(value, oldValue, address, mask) ?? false;
        };
        var previousActionDefinitionDataHook =
            cpu.WriteHooks[ActionDefinitionDataAddress];
        cpu.WriteHooks[ActionDefinitionDataAddress] =
            (value, oldValue, address, mask) =>
            {
                CaptureActionDefinitionByte(
                    (byte)((oldValue & ~mask) | (value & mask)));
                return previousActionDefinitionDataHook?.Invoke(
                    value,
                    oldValue,
                    address,
                    mask) ?? false;
            };
        var previousActionDefinitionSelectorHook =
            cpu.WriteHooks[ActionDefinitionSelectorAddress];
        cpu.WriteHooks[ActionDefinitionSelectorAddress] =
            (value, oldValue, address, mask) =>
            {
                CommitActionDefinition(
                    (byte)((oldValue & ~mask) | (value & mask)));
                return previousActionDefinitionSelectorHook?.Invoke(
                    value,
                    oldValue,
                    address,
                    mask) ?? false;
            };
        var previousSelectorHook = cpu.WriteHooks[ActionSelectorAddress];
        cpu.WriteHooks[ActionSelectorAddress] = (value, oldValue, address, mask) =>
        {
            CommitActionProgram((byte)((oldValue & ~mask) | (value & mask)));
            return previousSelectorHook?.Invoke(value, oldValue, address, mask) ?? false;
        };
        cpu.WriteHooks[CommandStatusAddress] = WriteCommandStatus;
        // The worker owns command execution; this atomic byte is the physical
        // read-only register latch it publishes to the AVR bus.
        cpu.ReadHooks[CommandStatusAddress] = _ =>
            Volatile.Read(ref _commandStatus);

        SetFrameRolloverEnabled(
            (cpu.Data[FrameControlAddress] & FrameRolloverEnablePattern) ==
            FrameRolloverEnablePattern);
    }

    public bool FrameRolloverEnabled { get; private set; }

    public long FrameRolloverCount { get; private set; }

    public long CurrentFrameStartCycle => _currentFrameStartCycle;

    public byte CommandStatus => Volatile.Read(ref _commandStatus);

    public long StartedCommands { get; private set; }

    public long CompletedCommands { get; private set; }

    public long ProgrammedDescriptorCount { get; private set; }

    public long ProgrammedActionDefinitionCount { get; private set; }

    public long ExecutedActionCount { get; private set; }

    public long ActionProgramAdvanceCount { get; private set; }

    public IReadOnlyDictionary<int, AsicTimeGeneratorDescriptor> Descriptors => _descriptors;

    public IReadOnlyDictionary<byte, AsicTimeGeneratorActionProgram> ActionPrograms =>
        _actionPrograms;

    public IReadOnlyDictionary<byte, AsicTimeGeneratorActionDefinition>
        ActionDefinitions => _actionDefinitions;

    public event Action<int, AsicTimeGeneratorDescriptor>? DescriptorProgrammed;

    public event Action<byte, AsicTimeGeneratorActionProgram>? ActionProgrammed;

    public event Action<byte, AsicTimeGeneratorActionDefinition>?
        ActionDefinitionProgrammed;

    public event Action<AsicTimeGeneratorActionExecution>? ActionExecuted;

    void SetFrameRolloverEnabled(bool enabled)
    {
        if (FrameRolloverEnabled == enabled)
        {
            return;
        }

        FrameRolloverEnabled = enabled;
        if (enabled)
        {
            ScheduleFrameRollover();
            return;
        }

        if (_frameRolloverScheduled)
        {
            _clock.Cancel(_frameRollover);
            _frameRolloverScheduled = false;
        }
        _currentFrameStartCycle = -1;
    }

    void ScheduleFrameRollover()
    {
        if (_frameRolloverScheduled || !FrameRolloverEnabled)
        {
            return;
        }

        Schedule(_frameRollover, FrameRolloverCycles);
        _frameRolloverScheduled = true;
    }

    void FrameRollover()
    {
        _frameRolloverScheduled = false;
        if (!FrameRolloverEnabled)
        {
            return;
        }

        _currentFrameStartCycle = _clock.Cycles;
        DrainPendingTransactionsCore();
        FrameRolloverCount++;

        // Each pair of physical records is indistinguishable after the
        // firmware-programmed router maps it to a process. Use the first
        // member of each proven pair and retain the producer-before-consumer
        // order established by the native PH signal path.
        _interruptRouter.RaiseHighPriorityPair(
            PhProcessPhysicalSource,
            PhDispatcherPhysicalSource);
        ScheduleFrameRollover();
    }

    public bool TryGetDescriptor(
        int portAddress,
        out AsicTimeGeneratorDescriptor descriptor) =>
        _descriptors.TryGetValue(portAddress, out descriptor);

    void CaptureDescriptorByte(int address, byte value)
    {
        var port = address - FirstSchedulePortAddress;
        var length = _pendingDescriptorLengths[port];
        _pendingDescriptorBytes[port][length] = value;
        length++;

        if (length < DescriptorSize)
        {
            _pendingDescriptorLengths[port] = length;
            return;
        }

        _pendingDescriptorLengths[port] = 0;
        var descriptor = new AsicTimeGeneratorDescriptor(
            _pendingDescriptorBytes[port][0],
            _pendingDescriptorBytes[port][1],
            _pendingDescriptorBytes[port][2]);
        if (_worker is null || _worker.IsCurrentThread)
        {
            ProgramDescriptor(address, descriptor);
        }
        else
        {
            QueuePendingTransaction(AsicTimeGeneratorPendingTransaction.ForDescriptor(
                address,
                descriptor));
        }
    }

    void CaptureActionByte(byte value)
    {
        // Firmware's dedicated-channel PH path prefixes the sequencer-advance
        // run with 0x09. Defer a word-boundary 0x09 until its following byte
        // proves whether it is the 09 01 control prefix or real action data.
        if (ConsumePendingAdvancePrefix(value))
        {
            return;
        }

        CaptureUnprefixedActionByte(value);
    }

    void CaptureUnprefixedActionByte(byte value)
    {
        if (!_actionProgramAdvancePending &&
            value == ActionProgramAdvancePrefix &&
            (IsAtActionWordBoundary() || HasTrailingScheduledActionToken()))
        {
            _actionProgramAdvancePrefixPending = true;
            return;
        }

        // Firmware uses 0x01 as a sequencer-advance strobe rather than an
        // action byte. Odd bytes after it remain in the control sequence: the
        // short form is 01 89, while idle PH emits
        // 01 05 07 09 89 8b 8d 8f. An even byte starts a new staged action
        // program, whose observed first bytes are 00, 02, 04, 06, or 08. If
        // that happens before a selector commit, the prior staged program was
        // abandoned during a PH mode transition and must not join the new one.
        if (value == ActionProgramAdvance)
        {
            ActionProgramAdvanceCount++;
            _actionProgramAdvancePending = true;
            return;
        }
        if (_actionProgramAdvancePending && !BeginProgramAfterAdvance(value))
        {
            return;
        }

        _pendingActionBytes.Add(value);
    }

    bool ConsumePendingAdvancePrefix(byte value)
    {
        if (!_actionProgramAdvancePrefixPending)
        {
            return false;
        }

        _actionProgramAdvancePrefixPending = false;
        if (value != ActionProgramAdvance)
        {
            _pendingActionBytes.Add(ActionProgramAdvancePrefix);
            return false;
        }

        ActionProgramAdvanceCount++;
        _actionProgramAdvancePending = true;
        return true;
    }

    bool BeginProgramAfterAdvance(byte value)
    {
        if ((value & 0x01) != 0)
        {
            return false;
        }

        _actionProgramAdvancePending = false;
        _pendingActionBytes.Clear();
        return true;
    }

    bool IsAtActionWordBoundary() => (_pendingActionBytes.Count & 1) == 0;

    bool HasTrailingScheduledActionToken() =>
        (_pendingActionBytes.Count & 1) != 0 &&
        _pendingActionBytes[^1] is
            ScheduledEncoderActionToken or ScheduledDecoderActionToken;

    void CaptureActionDefinitionByte(byte value) =>
        _pendingActionDefinitionBytes.Add(value);

    void CommitActionDefinition(byte selector)
    {
        var bytes = _pendingActionDefinitionBytes.ToArray();
        _pendingActionDefinitionBytes.Clear();
        var definition = new AsicTimeGeneratorActionDefinition(bytes);
        if (_worker is null || _worker.IsCurrentThread)
        {
            ProgramActionDefinition(selector, definition);
        }
        else
        {
            QueuePendingTransaction(AsicTimeGeneratorPendingTransaction.ForActionDefinition(
                selector,
                definition));
        }
    }

    void CommitActionProgram(byte selector)
    {
        if (_actionProgramAdvancePrefixPending)
        {
            _pendingActionBytes.Add(ActionProgramAdvancePrefix);
            _actionProgramAdvancePrefixPending = false;
        }
        _actionProgramAdvancePending = false;
        var bytes = _pendingActionBytes.ToArray();
        _pendingActionBytes.Clear();
        var program = new AsicTimeGeneratorActionProgram(bytes);
        if (_worker is null || _worker.IsCurrentThread)
        {
            ProgramAction(selector, program);
        }
        else
        {
            QueuePendingTransaction(AsicTimeGeneratorPendingTransaction.ForAction(
                selector,
                program));
        }
    }

    void QueuePendingTransaction(AsicTimeGeneratorPendingTransaction transaction)
    {
        _pendingTransactions.Writer.TryWrite(transaction);
        Interlocked.Increment(ref _pendingTransactionCount);
        if (Interlocked.CompareExchange(ref _pendingDrainScheduledFlag, 1, 0) != 0)
        {
            return;
        }

        Schedule(_drainPendingTransactions, 1);
    }

    internal void Synchronize()
    {
        if (_worker is null || _worker.IsCurrentThread)
        {
            DrainPendingTransactionsCore();
            return;
        }

        if (Volatile.Read(ref _pendingTransactionCount) == 0)
        {
            return;
        }
        _worker.Invoke(DrainPendingTransactionsCore);
    }

    void DrainPendingTransactionsCore()
    {
        CancelScheduledDrain();
        while (_pendingTransactions.Reader.TryRead(out var transaction))
        {
            Interlocked.Decrement(ref _pendingTransactionCount);
            ProgramPendingTransaction(transaction);
        }
    }

    void CancelScheduledDrain()
    {
        if (Interlocked.Exchange(ref _pendingDrainScheduledFlag, 0) != 0)
        {
            _clock.Cancel(_drainPendingTransactions);
        }
    }

    void ProgramPendingTransaction(AsicTimeGeneratorPendingTransaction transaction)
    {
        if (transaction.ActionDefinition is { } actionDefinition)
        {
            ProgramActionDefinition(transaction.Selector, actionDefinition);
            return;
        }
        if (transaction.ActionProgram is { } actionProgram)
        {
            ProgramAction(transaction.Selector, actionProgram);
            return;
        }

        ProgramDescriptor(transaction.Address, transaction.Descriptor);
    }

    void ProgramAction(
        byte selector,
        AsicTimeGeneratorActionProgram program)
    {
        _actionPrograms[selector] = program;
        var slot = selector & 0x3f;
        if (slot < ActionSchedulePortCount)
        {
            _latestActionProgramSelectors[slot] = selector;
            _hasLatestActionProgram[slot] = true;
        }
        ActionProgrammed?.Invoke(selector, program);
    }

    void ProgramActionDefinition(
        byte selector,
        AsicTimeGeneratorActionDefinition definition)
    {
        _actionDefinitions[selector] = definition;
        var actionId = unchecked((byte)((selector - 1) & 0x3f));
        _actionDefinitionsById[actionId] = definition;
        ProgrammedActionDefinitionCount++;
        ActionDefinitionProgrammed?.Invoke(selector, definition);
    }

    void ProgramDescriptor(
        int address,
        AsicTimeGeneratorDescriptor descriptor)
    {
        _descriptors[address] = descriptor;
        ProgrammedDescriptorCount++;
        DescriptorProgrammed?.Invoke(address, descriptor);
        ScheduleDescriptorActions(address);
    }

    void ScheduleDescriptorActions(int address)
    {
        if (!TryCreateProgramSchedule(address, out var schedule))
        {
            return;
        }

        ScheduleActionWords(schedule);
        ScheduleTrailingAction(schedule);
    }

    bool TryCreateProgramSchedule(
        int address,
        out AsicTimeGeneratorProgramSchedule schedule)
    {
        schedule = default;
        var slot = address - FirstActionSchedulePortAddress;
        if ((uint)slot >= ActionSchedulePortCount ||
            !_hasLatestActionProgram[slot] ||
            !FrameRolloverEnabled ||
            _currentFrameStartCycle < 0)
        {
            return false;
        }

        var programSelector = _latestActionProgramSelectors[slot];
        var bytes = _actionPrograms[programSelector].Bytes;
        if (bytes.IsEmpty)
        {
            return false;
        }

        var span = bytes.Span;
        var scheduledActionToken = (span.Length & 1) != 0
            ? span[^1]
            : (byte)0;
        var hasScheduledAction = scheduledActionToken is
            ScheduledEncoderActionToken or ScheduledDecoderActionToken;
        if ((span.Length & 1) != 0 && !hasScheduledAction)
        {
            return false;
        }

        schedule = new(
            slot,
            ++_scheduleGenerations[slot],
            _currentFrameStartCycle,
            programSelector,
            address,
            bytes,
            scheduledActionToken);
        return true;
    }

    void ScheduleTrailingAction(AsicTimeGeneratorProgramSchedule schedule)
    {
        // In TCH/F mode firmware arms encoder command 0x2c with control 0x04,
        // defines token 0x64 with its absolute compare time, and appends that
        // single token to the otherwise word-oriented TX program. It is a
        // scheduled command strobe, not a truncated operand.
        if (!TryGetScheduledAction(
                schedule.ScheduledActionToken,
                out var scheduledActionId,
                out var scheduledActionDefinition))
        {
            return;
        }

        ScheduleDefinedAction(new(
            schedule.Slot,
            schedule.Generation,
            schedule.FrameStartCycle,
            schedule.ProgramSelector,
            schedule.Address,
            scheduledActionId,
            Operand: 0,
            scheduledActionDefinition));
    }

    void ScheduleActionWords(AsicTimeGeneratorProgramSchedule schedule)
    {
        var bytes = schedule.Bytes.Span;
        var pairedLength = bytes.Length & ~1;
        for (var offset = 0; offset < pairedLength; offset += 2)
        {
            ScheduleActionWord(
                schedule,
                (ushort)(bytes[offset] | bytes[offset + 1] << 8));
        }
    }

    void ScheduleActionWord(
        AsicTimeGeneratorProgramSchedule schedule,
        ushort word)
    {
        var actionId = (byte)(word & 0x3f);
        if (!_actionDefinitionsById.TryGetValue(actionId, out var definition))
        {
            return;
        }

        ScheduleDefinedAction(new(
            schedule.Slot,
            schedule.Generation,
            schedule.FrameStartCycle,
            schedule.ProgramSelector,
            schedule.Address,
            actionId,
            (ushort)(word >> 6),
            definition));
    }

    bool TryGetScheduledAction(
        byte token,
        out byte actionId,
        out AsicTimeGeneratorActionDefinition definition)
    {
        actionId = token switch
        {
            ScheduledEncoderActionToken => ScheduledEncoderActionId,
            ScheduledDecoderActionToken => ScheduledDecoderActionId,
            _ => 0,
        };
        if (actionId == 0)
        {
            definition = default;
            return false;
        }

        if (_actionDefinitions.TryGetValue(token, out definition))
        {
            return true;
        }

        // The observed TCH/F turnaround writes token 0x65 after encoder-done,
        // but does not program a separate 0x65 compare. Both operations occupy
        // the same traffic timeslot, so the compatibility sequencer reuses the
        // immediately preceding source-derived 0x64 compare. This is an
        // explicit timing approximation; the token and firmware descriptor are
        // still required and no task state is synthesized.
        return token == ScheduledDecoderActionToken &&
            _actionDefinitions.TryGetValue(
                ScheduledEncoderActionToken,
                out definition);
    }

    void ScheduleDefinedAction(AsicTimeGeneratorActionSchedule schedule)
    {
        var definitionBytes = schedule.Definition.Bytes.Span;
        if (definitionBytes.Length == 0 || (definitionBytes.Length & 1) != 0)
        {
            return;
        }

        var occurrenceCount = definitionBytes.Length / 2;
        for (var occurrence = 0; occurrence < occurrenceCount; occurrence++)
        {
            ScheduleDefinitionOccurrence(
                schedule,
                definitionBytes,
                occurrence,
                occurrenceCount);
        }
    }

    void ScheduleDefinitionOccurrence(
        AsicTimeGeneratorActionSchedule schedule,
        ReadOnlySpan<byte> definitionBytes,
        int occurrence,
        int occurrenceCount)
    {
        var definitionOffset = occurrence * 2;
        var quarterBit = (ushort)(
            definitionBytes[definitionOffset] |
            definitionBytes[definitionOffset + 1] << 8);
        if (quarterBit >= FrameQuarterBits)
        {
            return;
        }

        var dueCycle = schedule.FrameStartCycle + quarterBit * QuarterBitCycles;
        if (dueCycle <= _clock.Cycles)
        {
            return;
        }

        var execution = new AsicTimeGeneratorActionExecution(
            schedule.ProgramSelector,
            schedule.Address,
            schedule.ActionId,
            schedule.Operand,
            quarterBit,
            occurrence,
            occurrenceCount);
        ScheduleAt(
            () => ExecuteAction(
                schedule.Slot,
                schedule.Generation,
                schedule.FrameStartCycle,
                execution),
            dueCycle);
    }

    void ExecuteAction(
        int slot,
        long generation,
        long frameStartCycle,
        AsicTimeGeneratorActionExecution execution)
    {
        if (!FrameRolloverEnabled ||
            _currentFrameStartCycle != frameStartCycle ||
            _scheduleGenerations[slot] != generation)
        {
            return;
        }

        ExecutedActionCount++;
        ActionExecuted?.Invoke(execution);
    }

    bool WriteCommandStatus(byte value, byte _, int __, byte mask)
    {
        var started = (byte)(value & mask & CommandMask);
        if (started == 0)
        {
            return true;
        }

        var commandStatus = (byte)(_commandStatus | started);
        Volatile.Write(ref _commandStatus, commandStatus);
        _cpu.Data[CommandStatusAddress] = commandStatus;
        StartedCommands += CountCommands(started);
        Schedule(() => CompleteCommands(started), CommandCompletionCycles);
        return true;
    }

    void CompleteCommands(byte commands)
    {
        DrainPendingTransactionsCore();
        var commandStatus = _commandStatus;
        var completed = (byte)(commandStatus & commands);
        commandStatus &= (byte)~commands;
        Volatile.Write(ref _commandStatus, commandStatus);
        _cpu.Data[CommandStatusAddress] = commandStatus;
        CompletedCommands += CountCommands(completed);
    }

    static int CountCommands(byte value) =>
        ((value & ScheduleCommit) != 0 ? 1 : 0) +
        ((value & ConfigurationCommit) != 0 ? 1 : 0);

    static byte[][] CreatePendingDescriptorBytes()
    {
        var rows = new byte[SchedulePortCount][];
        for (var port = 0; port < SchedulePortCount; port++)
        {
            rows[port] = new byte[DescriptorSize];
        }
        return rows;
    }

    void Schedule(Action callback, long delayCycles)
    {
        if (_worker is null)
        {
            _clock.Schedule(callback, delayCycles);
        }
        else
        {
            _clock.Schedule(_worker, callback, delayCycles);
        }
    }

    void ScheduleAt(Action callback, long cycle)
    {
        if (_worker is null)
        {
            _clock.ScheduleAt(callback, cycle);
        }
        else
        {
            _clock.ScheduleAt(_worker, callback, cycle);
        }
    }

}
