// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using AvrCore;

namespace Mia.Emulator.Asic;

internal static class AsicRom
{
    public const int StartWord = 0x3e0000;

    const int PrologueFirst = 0x3f0006;
    const int PrologueLast = 0x3f0020;
    const int EpilogueFirst = 0x3f0028;
    const int EpilogueLast = 0x3f0042;
    const int EpilogueOffset = 0x22;
    const int RampD = 0x58;
    const int RampX = 0x59;
    const int RampY = 0x5a;
    const int RampZ = 0x5b;
    const int Eind = 0x5c;
    const int Sreg = 0x5f;
    const int Y = 28;
    const int Z = 30;
    const int ContextHardwareStackPointerOffset = 18;
    const int ContextStateOffset = 4;
    const int ContextInitialSoftwareStackPointerOffset = 26;
    const int ContextInitialRampYOffset = 28;
    const int DescriptorTypeOffset = 10;
    const int DescriptorFromContextOffset = 31;
    const int DescriptorHardwareStackTopOffset = 31;
    const int DescriptorHardwareStackFloorOffset = 33;
    const int DescriptorMemoryBankOffset = 35;
    const int DescriptorSoftwareStackTopOffset = 37;
    const int DescriptorSoftwareStackFloorOffset = 51;
    const int SchedulerHardwareStackFloor = 0xf4a0;
    const int InitialHardwareStackTop = 0xf5fe;
    const int InitialHardwareStackFloor = 0xf4d0;
    const int InterruptRomCallOverwriteFirst = InitialHardwareStackTop - 5;
    const int SchedulerHardwareStackTop = 0xf4c8;
    const int IdleHardwareStackFloor = 0xf756;
    const int IdleHardwareStackTop = 0xf78b;
    const int InterruptDispatcherContext = 0xf7ab;
    const int ActiveSchedulerContext = 0xf608;
    const int InterruptNestingDepth = 0xf600;
    const int StringWalkerContinuation = 0x3f0124;
    const int ProcessDescriptorTable = 0xf3ba;
    const int ProcessDescriptorCount = 0x80;
    const int ExtendedDataBankBase = 0xd60000;
    const int ExtendedDataBankEnd = 0xd80000;
    const int LowSbnImageSource = 0x000200;
    const int MainDataImageDestination = 0x027000;
    const int MainInitializedDataLength = 0x001f2a;
    const int UploadInitializedImageLength = 0x004d12;
    const int AsicDataAllocationLength = 0x00fa9b;

    // The shared software-stack entry points preserve the compiler ABI's
    // callee-saved register family in this frame order. Entry 0x3f0006 saves
    // the first three registers, and each successive even entry adds one.
    // In particular, the four-byte frame at 0x3f0008 preserves r24-r27;
    // several firmware loops keep their index in r24 across such calls.
    static readonly int[] SoftwareStackRegisters =
    [
        25, 26, 27, 24,
        4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    ];

    static readonly ConditionalWeakTable<Cpu, AsicRomSchedulerState> SchedulerStates = new();
    static readonly ConditionalWeakTable<Cpu, Stack<AsicRomStringWalkState>> StringWalkStates = new();
    static readonly ConditionalWeakTable<Cpu, AsicRomFirmwareUploadState> FirmwareUploadStates = new();

    public static AsicRomDispatchResult Dispatch(Cpu cpu)
    {
        var entry = cpu.PC;
        var serviceResult = DispatchService(cpu, entry);
        if (serviceResult != AsicRomDispatchResult.Unknown)
        {
            return serviceResult;
        }

        if (TryExecuteStackHelper(cpu, entry))
        {
            return AsicRomDispatchResult.Handled;
        }

        return CompleteRomCall(cpu, ExecuteArithmeticHelper(cpu, entry));
    }

    static AsicRomDispatchResult DispatchService(Cpu cpu, int entry) => entry switch
    {
        0x3f8000 => CompleteVoidRomCall(cpu),
        0x3f0152 => CompleteRomCall(cpu, CopyFixedByteBlocks(cpu, 16)),
        0x3f0156 => CompleteRomCall(cpu, CopyFixedByteBlocks(cpu, 32)),
        0x3f015a => CompleteRomCall(cpu, CopyFixedByteBlocks(cpu, 64)),
        0x3f015c => ResultOf(InitializeContext(cpu)),
        0x3f0160 => ResultOf(SaveContext(cpu)),
        0x3f0164 => ResultOf(FinalizeInterruptContextSave(cpu)),
        0x3f0162 => ResultOf(RestoreInterruptedContext(cpu)),
        0x3f015e => ResultOf(RestoreContext(cpu)),
        0x3f0122 => ResultOf(StartStringWalk(cpu)),
        StringWalkerContinuation => ResultOf(ContinueStringWalk(cpu)),
        0x3f0136 => CompleteRomCall(cpu, ReceivePeripheralBytes(cpu)),
        0x3f0138 => CompleteRomCall(cpu, TransmitPeripheralBytes(cpu)),
        0x3f013a => CompleteRomCall(cpu, CopyFixedByteBlocks(cpu, 4)),
        0x3f0142 => CompleteRomCall(cpu, CopyFixedByteBlocks(cpu, 8)),
        0x3f0078 => CompleteRomCall(cpu, OpenFirmwareUploadSession(cpu)),
        0x3f0074 => CompleteRomCall(cpu, PrepareFirmwareUploadMemory(cpu)),
        0x3f0076 => CompleteRomCall(cpu, UploadFirmware(cpu)),
        0x3f0072 => CompleteRomCall(cpu, UploadImplicitFirmwareSpan(cpu)),
        0x3f006e => CompleteRomCall(cpu, CopyMemoryBytes(cpu)),
        0x3f0070 => CompleteRomCall(cpu, PushDataBytes(cpu)),
        0x3f009c => CompleteRomCall(cpu, CopyDataBytes(cpu)),
        0x3f009e => CompleteRomCall(cpu, CompareBytes(cpu)),
        0x3f00a0 => CompleteRomCall(cpu, FindByte(cpu)),
        0x3f00a2 => CompleteRomCall(cpu, MoveDataBytes(cpu)),
        0x3f00a4 => CompleteRomCall(cpu, FillDataBytes(cpu)),
        0x3f00a6 => CompleteRomCall(cpu, ConcatenateString(cpu)),
        0x3f00a8 => CompleteRomCall(cpu, FindByteInString(cpu)),
        0x3f00aa => CompleteRomCall(cpu, CompareStrings(cpu)),
        0x3f00ac => CompleteRomCall(cpu, CopyString(cpu)),
        0x3f00ae => CompleteRomCall(cpu, GetRejectedStringPrefixLength(cpu)),
        0x3f00b0 => CompleteRomCall(cpu, GetStringLength(cpu)),
        0x3f00b2 => CompleteRomCall(cpu, CompareBoundedStrings(cpu)),
        0x3f00b4 => CompleteRomCall(cpu, CopyBoundedString(cpu)),
        0x3f00b6 => CompleteRomCall(cpu, GetAcceptedStringPrefixLength(cpu)),
        0x3f00bc => CompleteRomCall(cpu, ConcatenateBoundedString(cpu)),
        0x3f00c0 => CompleteRomCall(cpu, FindLastByteInString(cpu)),
        0x3f00c2 => CompleteRomCall(cpu, FindString(cpu)),
        0x3f00e8 => CompleteRomCall(cpu, ConvertStringToLong(cpu)),
        0x3f00ea => CompleteRomCall(cpu, ConvertStringToInt(cpu)),
        _ => AsicRomDispatchResult.Unknown,
    };

    static AsicRomDispatchResult CompleteVoidRomCall(Cpu cpu)
    {
        Return(cpu);
        return AsicRomDispatchResult.Handled;
    }

    static AsicRomDispatchResult ResultOf(bool succeeded) => succeeded
        ? AsicRomDispatchResult.Handled
        : AsicRomDispatchResult.Unknown;

    static bool TryExecuteStackHelper(Cpu cpu, int entry)
    {
        if (entry >= PrologueFirst && entry <= PrologueLast && (entry & 1) == 0)
        {
            ExecutePrologue(cpu, entry / 2 & 0xff);
            return true;
        }
        if (entry >= EpilogueFirst && entry <= EpilogueLast && (entry & 1) == 0)
        {
            ExecuteEpilogue(cpu, (entry - EpilogueOffset) / 2 & 0xff);
            return true;
        }

        return false;
    }

    static AsicRomDispatchResult CompleteRomCall(Cpu cpu, bool succeeded)
    {
        if (!succeeded)
        {
            return AsicRomDispatchResult.Unknown;
        }

        Return(cpu);
        return AsicRomDispatchResult.Handled;
    }

    public static ReadOnlyMemory<byte> GetUploadedFirmware(Cpu cpu)
    {
        return FirmwareUploadStates.TryGetValue(cpu, out var state)
            ? state.Image.AsMemory(0, state.Length)
            : ReadOnlyMemory<byte>.Empty;
    }

    public static int GetUploadedFirmwareByteCount(Cpu cpu)
    {
        return FirmwareUploadStates.TryGetValue(cpu, out var state) ? state.UploadedBytes : 0;
    }

    public static int? GetUploadedFirmwareZeroLengthDestination(Cpu cpu)
    {
        return FirmwareUploadStates.TryGetValue(cpu, out var state)
            ? state.ZeroLengthDestination
            : null;
    }

    public static IReadOnlyList<AsicSchedulerContextInfo> GetSchedulerContexts(Cpu cpu)
    {
        if (!SchedulerStates.TryGetValue(cpu, out var state))
        {
            return [];
        }

        return state.Contexts
            .Where(pair => state.Metadata.ContainsKey(pair.Key))
            .Select(pair =>
            {
                var metadata = state.Metadata[pair.Key];
                var saved = pair.Value;
                var hardwareStack = saved.HardwareStack;
                return new AsicSchedulerContextInfo(
                    pair.Key,
                    metadata.DescriptorAddress,
                    metadata.Process,
                    metadata.TaskEntry,
                    saved.Pc,
                    saved.StackPointer,
                    saved.Registers[Y] | saved.Registers[Y + 1] << 8 | saved.RampY << 16,
                    saved.RampD,
                    saved.RampX,
                    saved.RampZ,
                    saved.Eind,
                    saved.Sreg,
                    saved.Registers.ToArray(),
                    hardwareStack?.StartAddress ?? 0,
                    hardwareStack?.Bytes.ToArray() ?? [],
                    cpu.ReadData(pair.Key + ContextStateOffset),
                    IsProcessRunnable(cpu, metadata.DescriptorAddress));
            })
            .OrderBy(context => context.Process)
            .ThenBy(context => context.Address)
            .ToArray();
    }

    public static AsicSchedulerRestoreInfo GetSchedulerRestoreInfo(Cpu cpu)
    {
        if (!SchedulerStates.TryGetValue(cpu, out var state))
        {
            return new AsicSchedulerRestoreInfo(0, 0);
        }
        return new AsicSchedulerRestoreInfo(
            state.CapturedInterruptRestoreCount,
            state.RejectedUncapturedInterruptRestoreCount);
    }

    public static int GetActiveSchedulerRecordAddress(Cpu cpu)
    {
        return GetDataUInt16(cpu, ActiveSchedulerContext);
    }

    /// <summary>
    /// Preserves task state at the actual interrupt boundary. The primary
    /// interrupt wrapper switches back to the shared hardware stack before it
    /// calls mask ROM, overwriting the return PC pushed by AVR hardware. The
    /// ASIC retains this state outside the firmware-visible stack.
    /// </summary>
    public static bool CaptureHighPriorityInterrupt(Cpu cpu)
    {
        if (cpu.ReadData(InterruptNestingDepth) != 0)
        {
            return false;
        }

        // A peripheral can interrupt the firmware's initial runnable-process
        // scan before any task has reached ROM context-init entry 0x015c.  The
        // scan itself is represented by the dedicated dispatcher context, so
        // its ROM-private snapshot must not depend on a task context already
        // existing.  Task interrupts remain gated by the metadata check below.
        var state = SchedulerStates.GetValue(
            cpu,
            static _ => new AsicRomSchedulerState());
        var activeContextAddress = GetDataUInt16(cpu, ActiveSchedulerContext);
        var interruptedFrame = ValidateInterruptedFrame(cpu);
        if (interruptedFrame is not { } frame)
        {
            return false;
        }

        var ownership = ResolveInterruptedContext(
            state,
            activeContextAddress,
            frame.StackPointer);
        var stackTop = ResolveInterruptedStackTop(
            state,
            ownership.ContextAddress,
            frame.StackPointer);
        if (stackTop < frame.StackPointer)
        {
            return false;
        }

        var capturedContext = MaterializeInterruptedContext(
            cpu,
            ownership.ContextAddress,
            ownership.AddressEnvironmentDescriptor,
            (frame.StackPointer, frame.Pc, stackTop));
        return PublishInterruptCapture(
            state,
            ownership.ContextAddress,
            capturedContext);
    }

    static (int StackPointer, int Pc)? ValidateInterruptedFrame(Cpu cpu)
    {
        var stackPointer = cpu.SP + 3;
        if (cpu.SP < 0 || stackPointer >= cpu.Data.Length)
        {
            return null;
        }

        // Called immediately after AvrInterrupt.Execute: the hardware-pushed
        // PC occupies the three bytes above SP.
        var pc = cpu.ReadData(stackPointer) |
            cpu.ReadData(stackPointer - 1) << 8 |
            cpu.ReadData(stackPointer - 2) << 16;
        if (pc < 0 || pc >= cpu.ProgWords)
        {
            return null;
        }

        return (stackPointer, pc);
    }

    static (int ContextAddress, int AddressEnvironmentDescriptor)
        ResolveInterruptedContext(
        AsicRomSchedulerState state,
        int activeContextAddress,
        int stackPointer)
    {
        var hasActiveMetadata =
            state.Metadata.TryGetValue(activeContextAddress, out var metadata);
        // Scheduler helpers can run on either a task stack or the shared
        // dispatcher stack, so the interrupted PC alone cannot prove ownership.
        var ownedByActiveContext = hasActiveMetadata &&
            stackPointer >= metadata!.HardwareStackFloor &&
            stackPointer <= metadata.HardwareStackTop;
        return (
            ownedByActiveContext ? activeContextAddress : InterruptDispatcherContext,
            hasActiveMetadata ? metadata!.DescriptorAddress : 0);
    }

    static int ResolveInterruptedStackTop(
        AsicRomSchedulerState state,
        int contextAddress,
        int stackPointer) => contextAddress == InterruptDispatcherContext
        ? stackPointer switch
        {
            >= SchedulerHardwareStackFloor and <= SchedulerHardwareStackTop =>
                SchedulerHardwareStackTop,
            >= InitialHardwareStackFloor and <= InitialHardwareStackTop =>
                InitialHardwareStackTop,
            >= IdleHardwareStackFloor and <= IdleHardwareStackTop =>
                IdleHardwareStackTop,
            _ => -1,
        }
        : state.Metadata.TryGetValue(contextAddress, out var metadata)
            ? metadata.HardwareStackTop
            : -1;

    static AsicRomCpuContext MaterializeInterruptedContext(
        Cpu cpu,
        int contextAddress,
        int addressEnvironmentDescriptor,
        (int StackPointer, int Pc, int StackTop) frame) => new(
            cpu.Data[..32].ToArray(),
            frame.Pc,
            frame.StackPointer,
            cpu.Data[RampD],
            cpu.Data[RampX],
            cpu.Data[RampY],
            cpu.Data[RampZ],
            cpu.Data[Eind],
            (byte)(cpu.Data[Sreg] | 0x80),
            SnapshotHardwareStack(cpu, frame.StackPointer, frame.StackTop),
            addressEnvironmentDescriptor);

    static bool PublishInterruptCapture(
        AsicRomSchedulerState state,
        int contextAddress,
        AsicRomCpuContext context)
    {
        if (!RetirePreviousInterruptCapture(state, contextAddress))
        {
            return false;
        }

        state.InterruptCaptures[contextAddress] = new AsicRomInterruptCapture(
            AsicRomInterruptCapturePhase.EdgeCaptured,
            context);
        return true;
    }

    static bool RetirePreviousInterruptCapture(
        AsicRomSchedulerState state,
        int contextAddress)
    {
        if (!state.InterruptCaptures.TryGetValue(contextAddress, out var capture))
        {
            return true;
        }
        if (contextAddress != InterruptDispatcherContext ||
            capture.Phase != AsicRomInterruptCapturePhase.FrameFinalized)
        {
            return false;
        }

        // A reschedule abandons the finalized dispatcher continuation. A task
        // capture remains live until that task restores it.
        state.InterruptCaptures.Remove(contextAddress);
        state.Contexts.Remove(contextAddress);
        return true;
    }


    public static IReadOnlyList<AsicProcessDescriptorInfo> GetProcessDescriptors(Cpu cpu)
    {
        var descriptors = new List<AsicProcessDescriptorInfo>();
        for (var process = 0; process < ProcessDescriptorCount; process++)
        {
            var address = GetDataUInt16(cpu, ProcessDescriptorTable + process * 2);
            if (address <= 0 || address + 39 >= cpu.Data.Length ||
                cpu.ReadData(address) != 0xfd || cpu.ReadData(address + 1) != 0xfd ||
                cpu.ReadData(address + 5) != process)
            {
                continue;
            }

            descriptors.Add(new AsicProcessDescriptorInfo(
                (byte)process,
                address,
                GetDataUInt24(cpu, address + 22),
                cpu.ReadData(address + 4)));
        }
        return descriptors;
    }

    static bool IsProcessRunnable(Cpu cpu, int descriptorAddress)
    {
        if (descriptorAddress < 0 || descriptorAddress + 25 >= cpu.Data.Length)
        {
            return false;
        }

        var bitmapAddress = GetDataUInt16(cpu, descriptorAddress + 16);
        var mask = cpu.ReadData(descriptorAddress + 25);
        return bitmapAddress > 0 && bitmapAddress < cpu.Data.Length && mask != 0 &&
               (cpu.ReadData(bitmapAddress) & mask) != 0;
    }

    static bool CopyFixedByteBlocks(Cpu cpu, int blockSize)
    {
        var destination = GetDataRegisterUInt24(cpu, 16);
        var source = GetDataRegisterUInt24(cpu, 20);
        var stack = GetY(cpu);
        if (stack < 0 || stack + 1 >= cpu.Data.Length)
        {
            return false;
        }

        var blocks = GetDataUInt16(cpu, stack);
        var byteCount = blocks * blockSize;
        if (destination < 0 || source < 0 ||
            (long)destination + byteCount > 0x1000000 ||
            (long)source + byteCount > 0x1000000)
        {
            return false;
        }

        for (var offset = 0; offset < byteCount; offset++)
        {
            if (!TryReadDataByte(cpu, source + offset, out var value) ||
                !TryWriteDataByte(cpu, destination + offset, value))
            {
                return false;
            }
        }
        SetY(cpu, stack + 2);
        return true;
    }

    static bool UploadFirmware(Cpu cpu)
    {
        if (!FirmwareUploadStates.TryGetValue(cpu, out var state) || !state.MemoryPrepared)
        {
            return false;
        }

        var destination = GetUploadPointer(cpu);
        var source = GetDataRegisterUInt24(cpu, 16);
        var count = GetDataRegisterUInt24(cpu, 20);
        if (count == 0)
        {
            return RecordZeroLengthUpload(state, source, destination);
        }

        return UploadRelocatedFirmware(state, source, destination, count);
    }

    static bool RecordZeroLengthUpload(
        AsicRomFirmwareUploadState state,
        int source,
        int destination)
    {
        if (source != 0 || destination == 0 || state.UploadedBytes <= 0)
        {
            return true;
        }
        if (destination >= state.Length)
        {
            return false;
        }

        // This destination is not known to be a CPU entry; no observed
        // control transfer follows from this final zero-length argument.
        state.ZeroLengthDestination = destination;
        return true;
    }

    static bool UploadRelocatedFirmware(
        AsicRomFirmwareUploadState state,
        int source,
        int destination,
        int count)
    {
        var relocationOffset = source - destination;
        if (relocationOffset < 0)
        {
            return false;
        }

        if (state.RelocationOffset is int existingOffset && existingOffset != relocationOffset)
        {
            return false;
        }
        state.RelocationOffset = relocationOffset;
        if (!CopyFirmwareSpan(state, source, destination, count))
        {
            return false;
        }

        return UploadPendingFirmwareSpans(state, relocationOffset);
    }

    static bool UploadPendingFirmwareSpans(
        AsicRomFirmwareUploadState state,
        int relocationOffset)
    {
        var index = 0;
        var succeeded = true;
        while (index < state.PendingSpans.Count && succeeded)
        {
            var pending = state.PendingSpans[index++];
            var destination = pending.Source - relocationOffset;
            succeeded = destination >= 0 &&
                CopyFirmwareSpan(state, pending.Source, destination, pending.Count);
        }
        if (succeeded)
        {
            state.PendingSpans.Clear();
        }
        return succeeded;
    }

    static bool UploadImplicitFirmwareSpan(Cpu cpu)
    {
        if (!FirmwareUploadStates.TryGetValue(cpu, out var state) || !state.MemoryPrepared)
        {
            return false;
        }

        var source = GetUploadPointer(cpu);
        var count = GetDataRegisterUInt24(cpu, 16);
        if (count == 0)
        {
            return true;
        }
        if (!TryGetUploadStagingRange(state, source, count, out _))
        {
            return false;
        }

        return StageOrUploadImplicitFirmwareSpan(state, source, count);
    }

    static bool StageOrUploadImplicitFirmwareSpan(
        AsicRomFirmwareUploadState state,
        int source,
        int count)
    {
        if (state.RelocationOffset is not int relocationOffset)
        {
            state.PendingSpans.Add(new AsicRomFirmwareSpan(source, count));
            return true;
        }

        var destination = source - relocationOffset;
        return destination >= 0 && CopyFirmwareSpan(state, source, destination, count);
    }

    static bool OpenFirmwareUploadSession(Cpu cpu)
    {
        // The sole firmware caller explicitly clears Z and r16:r17 before
        // entering 0x3f0078. Treat only those written registers as this
        // service's ABI; r18:r19 remain live caller scratch values.
        if (cpu.GetUint16(Z) != 0 || GetUInt16(cpu, 16) != 0)
        {
            return false;
        }

        var state = FirmwareUploadStates.GetValue(
            cpu,
            static _ => new AsicRomFirmwareUploadState());
        state.Reset();
        state.SessionOpened = true;
        return true;
    }

    static bool PrepareFirmwareUploadMemory(Cpu cpu)
    {
        // The adjacent sole caller explicitly supplies zero in r19:Z,
        // r16:r17, and r20:r21 before any 0x3f0072/0x3f0076 transfers.
        if (GetUploadPointer(cpu) != 0 || GetUInt16(cpu, 16) != 0 || GetUInt16(cpu, 20) != 0 ||
            !FirmwareUploadStates.TryGetValue(cpu, out var state) || !state.SessionOpened)
        {
            return false;
        }

        if ((long)LowSbnImageSource + UploadInitializedImageLength > cpu.ProgBytes.Length ||
            (long)MainDataImageDestination + AsicDataAllocationLength > cpu.Data.Length)
        {
            return false;
        }

        state.ResetPayload();
        // The low image has two views with the same linker displacement. Its
        // data/descriptor prefix ends where the first vector target begins at
        // word 0x1095 (byte 0x212a); the main CPU sees that prefix followed by
        // zero-backed runtime data. ROM retains the complete low SBN payload
        // in a distinct staging domain consumed by 0x0072/0x0076. Leaving the
        // executable suffix in main data turns GSM clock fields into opcodes.
        cpu.Data.AsSpan(MainDataImageDestination, AsicDataAllocationLength).Clear();
        cpu.ProgBytes.AsSpan(LowSbnImageSource, MainInitializedDataLength)
            .CopyTo(cpu.Data.AsSpan(MainDataImageDestination, MainInitializedDataLength));

        state.UploadStagingImage = new byte[AsicDataAllocationLength];
        cpu.ProgBytes.AsSpan(LowSbnImageSource, UploadInitializedImageLength)
            .CopyTo(state.UploadStagingImage);
        state.MemoryPrepared = true;
        return true;
    }

    static bool CopyMemoryBytes(Cpu cpu)
    {
        var encodedSource = GetUploadPointer(cpu);
        var destination = GetDataRegisterUInt24(cpu, 16);
        var count = cpu.GetUint16(20);
        var destinationEnd = (long)destination + count;
        // D6/D7 is banked external data RAM even though those addresses also
        // have the logical-program marker bit set. This is the same pointer
        // distinction observed by the string helpers below; in particular,
        // the firmware's object loader copies a temporary D6 stack image into
        // low extended RAM through this entry.
        var logicalSource = (encodedSource & 0x800000) != 0 &&
            encodedSource is < ExtendedDataBankBase or >= ExtendedDataBankEnd;
        var source = logicalSource ? encodedSource & 0x7fffff : encodedSource;
        var sourceEnd = (long)source + count;
        var sourceMemory = logicalSource ? cpu.ProgBytes : cpu.Data;
        if (sourceEnd > sourceMemory.Length || destinationEnd > 0x1000000)
        {
            return false;
        }

        var transfer = new AsicRomMemoryTransfer(
            encodedSource,
            destination,
            count,
            source);
        return logicalSource
            ? CopyLogicalMemory(cpu, transfer)
            : CopyDataMemory(cpu, transfer);
    }

    static bool CopyLogicalMemory(Cpu cpu, AsicRomMemoryTransfer transfer)
    {
        var offset = 0;
        for (; offset < transfer.Count &&
            CopyLogicalMemoryByte(cpu, transfer, offset); offset++)
        {
        }
        return offset == transfer.Count;
    }

    static bool CopyLogicalMemoryByte(
        Cpu cpu,
        AsicRomMemoryTransfer transfer,
        int offset)
    {
        var logicalAddress = transfer.Source + offset;
        var value = logicalAddress < cpu.ReadHooks.Length &&
            cpu.HasReadHook(logicalAddress)
            ? cpu.ReadData(logicalAddress)
            : cpu.ProgBytes[transfer.PhysicalSource + offset];
        return TryWriteDataByte(cpu, transfer.Destination + offset, value);
    }

    static bool CopyDataMemory(Cpu cpu, AsicRomMemoryTransfer transfer)
    {
        var offset = 0;
        for (; offset < transfer.Count &&
            CopyDataMemoryByte(cpu, transfer, offset); offset++)
        {
        }
        return offset == transfer.Count;
    }

    static bool CopyDataMemoryByte(
        Cpu cpu,
        AsicRomMemoryTransfer transfer,
        int offset) =>
        TryReadDataByte(cpu, transfer.Source + offset, out var value) &&
        TryWriteDataByte(cpu, transfer.Destination + offset, value);

    static bool PushDataBytes(Cpu cpu)
    {
        var source = GetUploadPointer(cpu);
        var count = cpu.GetUint16(16);
        var stack = GetY(cpu);
        var destination = stack - count;
        if (destination < 0 || (long)source + count > 0x1000000)
        {
            return false;
        }

        var transfer = new AsicRomMemoryTransfer(source, destination, count);
        if (!PushDataBytes(cpu, transfer))
        {
            return false;
        }
        SetY(cpu, destination);
        return true;
    }

    static bool PushDataBytes(Cpu cpu, AsicRomMemoryTransfer transfer) =>
        transfer.Destination > transfer.Source &&
        transfer.Destination < transfer.Source + transfer.Count
            ? CopyDataMemoryBackward(cpu, transfer)
            : CopyDataMemory(cpu, transfer);

    static bool CopyDataMemoryBackward(Cpu cpu, AsicRomMemoryTransfer transfer)
    {
        var offset = transfer.Count - 1;
        for (; offset >= 0 && CopyDataMemoryByte(cpu, transfer, offset); offset--)
        {
        }
        return offset < 0;
    }

    static bool FillDataBytes(Cpu cpu)
    {
        var destination = GetDataRegisterUInt24(cpu, 16);
        var value = cpu.ReadData(20);
        var stackPointer = GetY(cpu);
        if (stackPointer < 0 || stackPointer + 1 >= cpu.Data.Length)
        {
            return false;
        }

        var count = GetDataUInt16(cpu, stackPointer);
        if ((long)destination + count > 0x1000000)
        {
            return false;
        }

        for (var offset = 0; offset < count; offset++)
        {
            if (!TryWriteDataByte(cpu, destination + offset, value))
            {
                return false;
            }
        }
        SetY(cpu, stackPointer + 2);
        return true;
    }

    static bool CopyFirmwareSpan(AsicRomFirmwareUploadState state, int source, int destination, int count)
    {
        var destinationEnd = (long)destination + count;
        if (!TryGetUploadStagingRange(state, source, count, out var sourceOffset) ||
            destination < 0 || destinationEnd > 0x1000000)
        {
            return false;
        }

        if (state.Image.Length < destinationEnd)
        {
            Array.Resize(ref state.Image, (int)destinationEnd);
        }
        Array.Copy(state.UploadStagingImage, sourceOffset, state.Image, destination, count);
        state.Length = Math.Max(state.Length, (int)destinationEnd);
        state.UploadedBytes += count;
        return true;
    }

    static bool TryGetUploadStagingRange(
        AsicRomFirmwareUploadState state,
        int source,
        int count,
        out int sourceOffset)
    {
        sourceOffset = source - MainDataImageDestination;
        return sourceOffset >= 0 && count >= 0 &&
               (long)sourceOffset + count <= state.UploadStagingImage.Length;
    }

    static bool ExecuteArithmeticHelper(Cpu cpu, int entry)
    {
        var shift = cpu.Data[20];
        switch (entry)
        {
            case 0x3f0044: // low 8 bits of 8 x 8 multiply
                cpu.Data[16] = (byte)(cpu.Data[16] * cpu.Data[20]);
                return true;
            case 0x3f0046: // 8-bit logical left shift
                cpu.Data[16] = shift < 8 ? (byte)(cpu.Data[16] << shift) : (byte)0;
                return true;
            case 0x3f0048: // 8-bit logical right shift
                cpu.Data[16] = shift < 8 ? (byte)(cpu.Data[16] >> shift) : (byte)0;
                return true;
            case 0x3f004a: // low 16 bits of 16 x 16 multiply
                SetUInt16(cpu, 16, GetUInt16(cpu, 16) * GetUInt16(cpu, 20));
                return true;
            case 0x3f004c: // unsigned 8-bit quotient and remainder
                {
                    var dividend = cpu.Data[16];
                    var divisor = cpu.Data[20];
                    if (divisor == 0)
                    {
                        return false;
                    }
                    cpu.Data[16] = (byte)(dividend / divisor);
                    cpu.Data[20] = (byte)(dividend % divisor);
                    return true;
                }
            case 0x3f004e: // signed 8-bit quotient and remainder
                {
                    var dividend = unchecked((sbyte)cpu.Data[16]);
                    var divisor = unchecked((sbyte)cpu.Data[20]);
                    if (divisor == 0 || dividend == sbyte.MinValue && divisor == -1)
                    {
                        return false;
                    }
                    cpu.Data[16] = unchecked((byte)(sbyte)(dividend / divisor));
                    cpu.Data[20] = unchecked((byte)(sbyte)(dividend % divisor));
                    return true;
                }
            case 0x3f0050: // 16-bit logical left shift
                SetUInt16(cpu, 16, shift < 16 ? GetUInt16(cpu, 16) << shift : 0);
                return true;
            case 0x3f0052: // 16-bit logical right shift
                SetUInt16(cpu, 16, shift < 16 ? GetUInt16(cpu, 16) >> shift : 0);
                return true;
            case 0x3f0054: // unsigned 16-bit quotient and remainder
                {
                    var dividend = GetUInt16(cpu, 16);
                    var divisor = GetUInt16(cpu, 20);
                    if (divisor == 0)
                    {
                        // The mask-ROM restoring divider has defined machine
                        // behavior for this C-undefined input: every quotient
                        // bit is set and no subtraction changes the remainder.
                        // Firmware call site 0x0b8568 intentionally exercises it.
                        SetUInt16(cpu, 16, ushort.MaxValue);
                        SetUInt16(cpu, 20, dividend);
                        return true;
                    }
                    SetUInt16(cpu, 16, dividend / divisor);
                    SetUInt16(cpu, 20, dividend % divisor);
                    return true;
                }
            case 0x3f0056: // signed 16-bit quotient and remainder
                {
                    var dividend = unchecked((short)GetUInt16(cpu, 16));
                    var divisor = unchecked((short)GetUInt16(cpu, 20));
                    if (divisor == 0 || dividend == short.MinValue && divisor == -1)
                    {
                        return false;
                    }
                    SetUInt16(cpu, 16, dividend / divisor);
                    SetUInt16(cpu, 20, dividend % divisor);
                    return true;
                }
            case 0x3f005c: // low 32 bits of 32 x 32 multiply
                SetUInt32(cpu, 16, GetUInt32(cpu, 16) * GetUInt32(cpu, 20));
                return true;
            case 0x3f005e: // 32-bit logical left shift
                SetUInt32(cpu, 16, shift < 32 ? GetUInt32(cpu, 16) << shift : 0);
                return true;
            case 0x3f0060: // 32-bit logical right shift
                SetUInt32(cpu, 16, shift < 32 ? GetUInt32(cpu, 16) >> shift : 0);
                return true;
            case 0x3f0062: // unsigned 32-bit quotient and remainder
                {
                    var dividend = GetUInt32(cpu, 16);
                    var divisor = GetUInt32(cpu, 20);
                    if (divisor == 0)
                    {
                        // The mask-ROM restoring divider has defined machine
                        // behavior for this C-undefined input: every quotient
                        // bit is set and no subtraction changes the remainder.
                        SetUInt32(cpu, 16, uint.MaxValue);
                        SetUInt32(cpu, 20, dividend);
                        return true;
                    }
                    SetUInt32(cpu, 16, dividend / divisor);
                    SetUInt32(cpu, 20, dividend % divisor);
                    return true;
                }
            case 0x3f0064: // signed 32-bit quotient and remainder
                {
                    var dividend = unchecked((int)GetUInt32(cpu, 16));
                    var divisor = unchecked((int)GetUInt32(cpu, 20));
                    if (divisor == 0 || dividend == int.MinValue && divisor == -1)
                    {
                        return false;
                    }
                    SetUInt32(cpu, 16, unchecked((uint)(dividend / divisor)));
                    SetUInt32(cpu, 20, unchecked((uint)(dividend % divisor)));
                    return true;
                }
            case 0x3f0088: // signed 32-bit integer to IEEE-754 single
                SetSingle(cpu, 16, unchecked((int)GetUInt32(cpu, 16)));
                return true;
            case 0x3f008c: // unsigned 32-bit integer to IEEE-754 single
                SetSingle(cpu, 16, GetUInt32(cpu, 16));
                return true;
            case 0x3f008e: // IEEE-754 single to signed 32-bit integer
                {
                    var value = GetSingle(cpu, 16);
                    // All recovered callers use ordinary finite, in-range C casts.
                    // Fail closed rather than guess this ROM's exceptional result.
                    if (!float.IsFinite(value) || value < -2147483648f || value >= 2147483648f)
                    {
                        return false;
                    }
                    SetUInt32(cpu, 16, unchecked((uint)(int)value));
                    return true;
                }
            case 0x3f0090: // IEEE-754 single addition
                SetSingle(cpu, 16, GetSingle(cpu, 16) + GetSingle(cpu, 20));
                return true;
            case 0x3f0092: // IEEE-754 single subtraction
                SetSingle(cpu, 16, GetSingle(cpu, 16) - GetSingle(cpu, 20));
                return true;
            case 0x3f0094: // IEEE-754 single multiplication
                SetSingle(cpu, 16, GetSingle(cpu, 16) * GetSingle(cpu, 20));
                return true;
            case 0x3f0096: // IEEE-754 single division
                SetSingle(cpu, 16, GetSingle(cpu, 16) / GetSingle(cpu, 20));
                return true;
            case 0x3f0098: // IEEE-754 single comparison, left < right in carry
                {
                    var left = GetSingle(cpu, 16);
                    var right = GetSingle(cpu, 20);
                    if (float.IsNaN(left) || float.IsNaN(right))
                    {
                        return false;
                    }
                    cpu.Data[Sreg] = (byte)((cpu.Data[Sreg] & ~1) | (left < right ? 1 : 0));
                    return true;
                }
            case 0x3f00e4: // signed 16-bit absolute value
                {
                    var value = unchecked((short)GetUInt16(cpu, 16));
                    SetUInt16(cpu, 16, value < 0 ? -value : value);
                    return true;
                }
            default:
                return false;
        }
    }

    static bool CompareStrings(Cpu cpu)
    {
        var left = GetUInt24(cpu, 16);
        var right = GetUInt24(cpu, 20);
        while (true)
        {
            if (!TryReadByte(cpu, left++, out var leftByte) ||
                !TryReadByte(cpu, right++, out var rightByte))
            {
                return false;
            }
            if (leftByte != rightByte)
            {
                SetUInt16(cpu, 16, leftByte < rightByte ? -1 : 1);
                return true;
            }
            if (leftByte == 0)
            {
                SetUInt16(cpu, 16, 0);
                return true;
            }
        }
    }

    static bool CompareBoundedStrings(Cpu cpu)
    {
        var left = GetUInt24(cpu, 16);
        var right = GetUInt24(cpu, 20);
        var stackPointer = GetY(cpu);
        if (stackPointer < 0 || stackPointer + 1 >= cpu.Data.Length)
        {
            return false;
        }

        var count = GetDataUInt16(cpu, stackPointer);
        var status = AsicRomComparisonStatus.Continue;
        for (var offset = 0;
             offset < count && status == AsicRomComparisonStatus.Continue;
             offset++)
        {
            status = CompareStringByte(cpu, left + offset, right + offset);
        }

        if (status == AsicRomComparisonStatus.Invalid)
        {
            return false;
        }

        SetUInt16(cpu, 16, status switch
        {
            AsicRomComparisonStatus.Less => -1,
            AsicRomComparisonStatus.Greater => 1,
            _ => 0,
        });
        SetY(cpu, stackPointer + 2);
        return true;
    }

    static AsicRomComparisonStatus CompareStringByte(
        Cpu cpu,
        int leftAddress,
        int rightAddress)
    {
        if (leftAddress > 0xffffff || rightAddress > 0xffffff ||
            !TryReadByte(cpu, leftAddress, out var left) ||
            !TryReadByte(cpu, rightAddress, out var right))
        {
            return AsicRomComparisonStatus.Invalid;
        }
        if (left != right)
        {
            return left < right
                ? AsicRomComparisonStatus.Less
                : AsicRomComparisonStatus.Greater;
        }
        return left == 0
            ? AsicRomComparisonStatus.Terminated
            : AsicRomComparisonStatus.Continue;
    }

    static bool CompareBytes(Cpu cpu)
    {
        var left = GetUInt24(cpu, 16);
        var right = GetUInt24(cpu, 20);
        var countAddress = GetY(cpu);
        if (countAddress < 0 || countAddress + 1 >= cpu.Data.Length)
        {
            return false;
        }

        var count = GetDataUInt16(cpu, countAddress);
        SetY(cpu, countAddress + 2);
        for (var offset = 0; offset < count; offset++)
        {
            if (!TryReadByte(cpu, left + offset, out var leftByte) ||
                !TryReadByte(cpu, right + offset, out var rightByte))
            {
                return false;
            }
            if (leftByte != rightByte)
            {
                SetUInt16(cpu, 16, leftByte < rightByte ? -1 : 1);
                return true;
            }
        }

        SetUInt16(cpu, 16, 0);
        return true;
    }

    static bool CopyDataBytes(Cpu cpu)
    {
        var destination = GetDataRegisterUInt24(cpu, 16);
        var source = GetDataRegisterUInt24(cpu, 20);
        var stackPointer = GetY(cpu);
        if (stackPointer < 0 || stackPointer + 1 >= cpu.Data.Length)
        {
            return false;
        }

        var count = GetDataUInt16(cpu, stackPointer);
        if ((long)destination + count > 0x1000000 ||
            (long)source + count > 0x1000000)
        {
            return false;
        }

        // ROM 0x009c is the compiler's far-data memcpy helper. Its recovered
        // ABI is destination in r18:r16, source in r22:r20, and a 16-bit count
        // on the banked software stack. Use the data bus for both sides so
        // external-memory aliases and MMIO observe the same reads and writes
        // as firmware instructions; overlapping copies use ROM 0x00a2 instead.
        for (var offset = 0; offset < count; offset++)
        {
            if (!TryReadByte(cpu, source + offset, out var value) ||
                !TryWriteDataByte(cpu, destination + offset, value))
            {
                return false;
            }
        }
        SetY(cpu, stackPointer + 2);
        return true;
    }

    static bool MoveDataBytes(Cpu cpu)
    {
        var destination = GetDataRegisterUInt24(cpu, 16);
        var source = GetDataRegisterUInt24(cpu, 20);
        var stackPointer = GetY(cpu);
        if (stackPointer < 0 || stackPointer + 1 >= cpu.Data.Length)
        {
            return false;
        }

        var count = GetDataUInt16(cpu, stackPointer);
        if ((long)destination + count > 0x1000000 ||
            (long)source + count > 0x1000000)
        {
            return false;
        }

        if (!TryMoveBytes(cpu, source, destination, count))
        {
            return false;
        }
        SetY(cpu, stackPointer + 2);
        return true;
    }

    static bool TryMoveBytes(Cpu cpu, int source, int destination, int count)
    {
        var bytes = new byte[count];
        if (!TryReadBytes(cpu, source, bytes))
        {
            return false;
        }
        return TryWriteBytes(cpu, destination, bytes);
    }

    static bool TryReadBytes(Cpu cpu, int source, Span<byte> destination)
    {
        var offset = 0;
        for (; offset < destination.Length &&
            TryReadByte(cpu, source + offset, out destination[offset]); offset++)
        {
        }
        return offset == destination.Length;
    }

    static bool TryWriteBytes(Cpu cpu, int destination, ReadOnlySpan<byte> source)
    {
        var offset = 0;
        for (; offset < source.Length &&
            TryWriteDataByte(cpu, destination + offset, source[offset]); offset++)
        {
        }
        return offset == source.Length;
    }

    static bool FindByte(Cpu cpu)
    {
        var start = GetUInt24(cpu, 16);
        var sought = cpu.Data[20];
        var stackPointer = GetY(cpu);
        if (stackPointer < 0 || stackPointer + 1 >= cpu.Data.Length)
        {
            return false;
        }

        var count = GetDataUInt16(cpu, stackPointer);
        for (var offset = 0; offset < count; offset++)
        {
            var address = start + offset;
            if (address > 0xffffff || !TryReadByte(cpu, address, out var value))
            {
                return false;
            }
            if (value == sought)
            {
                SetUInt24(cpu, 16, address);
                SetY(cpu, stackPointer + 2);
                return true;
            }
        }

        SetUInt24(cpu, 16, 0);
        SetY(cpu, stackPointer + 2);
        return true;
    }

    static bool TryReadByte(Cpu cpu, int address, out byte value)
    {
        // This firmware uses D6/D7 as banked external data RAM while logical
        // application-flash objects and strings live in lower Cx/Dx banks.
        // Bit 23 alone therefore cannot classify a pointer: a D6 software-stack
        // address also has it set. Prefer external RAM before applying the
        // logical-flash physical alias used by the firmware's read-only pointers.
        if (address >= ExtendedDataBankBase && address < ExtendedDataBankEnd)
        {
            value = cpu.ReadData(address);
            return true;
        }
        if ((address & 0x800000) != 0)
        {
            return TryReadProgramByte(cpu, address, out value);
        }

        return TryReadDataByte(cpu, address, out value);
    }

    static bool TryReadProgramByte(Cpu cpu, int address, out byte value)
    {
        var physical = address & 0x7fffff;
        if (physical >= cpu.ProgBytes.Length)
        {
            value = 0;
            return false;
        }

        value = cpu.ProgBytes[physical];
        return true;
    }

    static bool TryReadDataByte(Cpu cpu, int address, out byte value)
    {
        if (address < 0 || address > 0xffffff)
        {
            value = 0;
            return false;
        }

        var physicalAddress = cpu.TranslateDataAddress(address);
        if ((uint)physicalAddress >= (uint)cpu.Data.Length)
        {
            value = 0;
            return false;
        }

        value = cpu.ReadData(physicalAddress);
        return true;
    }

    static bool TryWriteDataByte(Cpu cpu, int address, byte value)
    {
        if (address < 0 || address > 0xffffff)
        {
            return false;
        }

        var physicalAddress = cpu.TranslateDataAddress(address);
        if ((uint)physicalAddress >= (uint)cpu.Data.Length)
        {
            return false;
        }

        cpu.WriteData(physicalAddress, value);
        return true;
    }

    static bool GetStringLength(Cpu cpu)
    {
        var address = GetUInt24(cpu, 16);
        for (var length = 0; length <= ushort.MaxValue; length++)
        {
            if (address + length > 0xffffff ||
                !TryReadByte(cpu, address + length, out var value))
            {
                return false;
            }
            if (value == 0)
            {
                SetUInt16(cpu, 16, length);
                return true;
            }
        }
        return false;
    }

    static bool GetRejectedStringPrefixLength(Cpu cpu)
        => GetStringPrefixLength(cpu, stopWhenMember: true);

    static bool GetAcceptedStringPrefixLength(Cpu cpu)
        => GetStringPrefixLength(cpu, stopWhenMember: false);

    static bool GetStringPrefixLength(Cpu cpu, bool stopWhenMember)
    {
        var valueAddress = GetUInt24(cpu, 16);
        var setAddress = GetUInt24(cpu, 20);
        var status = AsicRomStringReadStatus.Continue;
        var length = 0;
        for (; length <= ushort.MaxValue &&
            status == AsicRomStringReadStatus.Continue;
            length += status == AsicRomStringReadStatus.Continue ? 1 : 0)
        {
            status = ClassifyPrefixCharacter(
                cpu,
                valueAddress + length,
                setAddress,
                stopWhenMember);
        }

        if (status != AsicRomStringReadStatus.Complete)
        {
            return false;
        }
        SetUInt16(cpu, 16, length);
        return true;
    }

    static AsicRomStringReadStatus ClassifyPrefixCharacter(
        Cpu cpu,
        int valueAddress,
        int setAddress,
        bool stopWhenMember)
    {
        if (valueAddress > 0xffffff ||
            !TryReadByte(cpu, valueAddress, out var value))
        {
            return AsicRomStringReadStatus.Invalid;
        }
        if (value == 0)
        {
            return AsicRomStringReadStatus.Complete;
        }
        if (!TryStringContains(cpu, setAddress, value, out var member))
        {
            return AsicRomStringReadStatus.Invalid;
        }
        return member == stopWhenMember
            ? AsicRomStringReadStatus.Complete
            : AsicRomStringReadStatus.Continue;
    }

    static bool TryStringContains(Cpu cpu, int address, byte value, out bool contains)
    {
        contains = false;
        var status = AsicRomStringReadStatus.Continue;
        for (var offset = 0;
             offset <= ushort.MaxValue && status == AsicRomStringReadStatus.Continue;
             offset++)
        {
            status = CompareSetCharacter(cpu, address + offset, value, out contains);
        }
        return status != AsicRomStringReadStatus.Invalid;
    }

    static AsicRomStringReadStatus CompareSetCharacter(
        Cpu cpu,
        int address,
        byte value,
        out bool contains)
    {
        contains = false;
        if (address > 0xffffff || !TryReadByte(cpu, address, out var candidate))
        {
            return AsicRomStringReadStatus.Invalid;
        }
        if (candidate == 0)
        {
            return AsicRomStringReadStatus.Complete;
        }

        contains = candidate == value;
        return contains
            ? AsicRomStringReadStatus.Complete
            : AsicRomStringReadStatus.Continue;
    }

    static bool ConcatenateString(Cpu cpu)
    {
        var destination = GetUInt24(cpu, 16);
        var source = GetUInt24(cpu, 20);
        if (!TryReadDataByte(cpu, destination, out _))
        {
            return false;
        }

        if (!TryFindDataStringEnd(cpu, destination, out var destinationEnd))
        {
            return false;
        }

        return AppendString(cpu, destinationEnd, source, ushort.MaxValue + 1);
    }

    static bool TryFindDataStringEnd(Cpu cpu, int start, out int end)
    {
        end = start;
        var valid = true;
        byte value = 0;
        while (end <= 0xffffff &&
            (valid = TryReadDataByte(cpu, end, out value)) &&
            value != 0)
        {
            end++;
        }
        return valid && end <= 0xffffff && value == 0;
    }

    static bool AppendString(Cpu cpu, int destination, int source, int maximumLength)
    {
        if (!TryReadStringBytes(cpu, source, maximumLength, out var value) ||
            !value.Terminated)
        {
            return false;
        }
        if ((long)destination + value.Bytes.Length > 0x1000000)
        {
            return false;
        }
        return TryWriteBytes(cpu, destination, value.Bytes);
    }

    static bool ConcatenateBoundedString(Cpu cpu)
    {
        var destination = GetUInt24(cpu, 16);
        var source = GetUInt24(cpu, 20);
        var stackPointer = GetY(cpu);
        if (!TryReadDataByte(cpu, destination, out _) ||
            stackPointer < 0 || stackPointer + 1 >= cpu.Data.Length)
        {
            return false;
        }

        var count = GetDataUInt16(cpu, stackPointer);
        if (!TryFindDataStringEnd(cpu, destination, out var destinationEnd))
        {
            return false;
        }
        if (!AppendBoundedString(cpu, destinationEnd, source, count))
        {
            return false;
        }

        SetY(cpu, stackPointer + 2);
        return true;
    }

    static bool AppendBoundedString(
        Cpu cpu,
        int destination,
        int source,
        int count)
    {
        if (!TryReadStringBytes(cpu, source, count, out var value))
        {
            return false;
        }

        var contentLength = value.Terminated
            ? value.Bytes.Length - 1
            : value.Bytes.Length;
        if ((long)destination + contentLength + 1 > 0x1000000)
        {
            return false;
        }
        if (!TryWriteBytes(cpu, destination, value.Bytes.AsSpan(0, contentLength)))
        {
            return false;
        }
        return TryWriteDataByte(cpu, destination + contentLength, 0);
    }

    static bool FindByteInString(Cpu cpu)
    {
        var start = GetUInt24(cpu, 16);
        var sought = cpu.Data[20];
        for (var offset = 0; offset <= ushort.MaxValue; offset++)
        {
            var address = start + offset;
            if (address > 0xffffff || !TryReadByte(cpu, address, out var value))
            {
                return false;
            }
            if (value == sought)
            {
                SetDataUInt24(cpu, 16, address);
                return true;
            }
            if (value == 0)
            {
                SetDataUInt24(cpu, 16, 0);
                return true;
            }
        }
        return false;
    }

    static bool FindLastByteInString(Cpu cpu)
    {
        var start = GetUInt24(cpu, 16);
        var sought = cpu.Data[20];
        var last = -1;
        for (var offset = 0; offset <= ushort.MaxValue; offset++)
        {
            var address = start + offset;
            if (address > 0xffffff || !TryReadByte(cpu, address, out var value))
            {
                return false;
            }
            if (value == sought)
            {
                last = address;
            }
            if (value == 0)
            {
                SetDataUInt24(cpu, 16, last < 0 ? 0 : last);
                return true;
            }
        }
        return false;
    }

    static bool FindString(Cpu cpu)
    {
        var haystack = GetUInt24(cpu, 16);
        var needle = GetUInt24(cpu, 20);
        if (!TryReadStringBytes(
                cpu,
                needle,
                ushort.MaxValue + 1,
                out var needleValue) ||
            !needleValue.Terminated)
        {
            return false;
        }

        var needleBytes = needleValue.Bytes.AsSpan(0, needleValue.Bytes.Length - 1);
        if (needleBytes.IsEmpty)
        {
            SetUInt24(cpu, 16, haystack);
            return true;
        }

        return FindStringAddress(cpu, haystack, needleBytes);
    }

    static bool FindStringAddress(Cpu cpu, int haystack, ReadOnlySpan<byte> needle)
    {
        var status = AsicRomFindStatus.Continue;
        var offset = 0;
        for (; offset <= ushort.MaxValue && status == AsicRomFindStatus.Continue;
            offset += status == AsicRomFindStatus.Continue ? 1 : 0)
        {
            status = ClassifyStringCandidate(cpu, haystack + offset, needle);
        }

        if (status == AsicRomFindStatus.Invalid ||
            status == AsicRomFindStatus.Continue)
        {
            return false;
        }
        SetUInt24(cpu, 16, status == AsicRomFindStatus.Found
            ? haystack + offset
            : 0);
        return true;
    }

    static AsicRomFindStatus ClassifyStringCandidate(
        Cpu cpu,
        int address,
        ReadOnlySpan<byte> needle)
    {
        if (address > 0xffffff || !TryReadByte(cpu, address, out var first))
        {
            return AsicRomFindStatus.Invalid;
        }
        if (first == 0)
        {
            return AsicRomFindStatus.NotFound;
        }
        if (!TryMatchString(cpu, address, needle, out var matches))
        {
            return AsicRomFindStatus.Invalid;
        }
        return matches ? AsicRomFindStatus.Found : AsicRomFindStatus.Continue;
    }

    static bool TryMatchString(
        Cpu cpu,
        int address,
        ReadOnlySpan<byte> needle,
        out bool matches)
    {
        var valid = true;
        matches = true;
        var offset = 0;
        byte value = 0;
        while (offset < needle.Length && valid && matches)
        {
            valid = address + offset <= 0xffffff &&
                TryReadByte(cpu, address + offset, out value);
            matches = valid && value == needle[offset];
            offset++;
        }
        return valid;
    }

    static bool ConvertStringToLong(Cpu cpu)
    {
        if (!TryConvertDecimalString(cpu, GetUInt24(cpu, 16), out var value))
        {
            return false;
        }
        SetUInt32(cpu, 16, value);
        return true;
    }

    static bool ConvertStringToInt(Cpu cpu)
    {
        if (!TryConvertDecimalString(cpu, GetUInt24(cpu, 16), out var value))
        {
            return false;
        }
        SetUInt16(cpu, 16, (ushort)value);
        return true;
    }

    static bool TryConvertDecimalString(Cpu cpu, int address, out uint value)
    {
        var state = new AsicRomDecimalParseState(address);
        if (!TryReadFirstDecimalCharacter(cpu, state, out var character))
        {
            value = 0;
            return false;
        }

        state.Negative = character == '-';
        if (state.Negative || character == '+')
        {
            state.Offset++;
        }

        var status = AsicRomStringReadStatus.Continue;
        while (status == AsicRomStringReadStatus.Continue)
        {
            status = ReadDecimalDigit(cpu, state);
        }

        value = state.Negative ? unchecked(0u - state.Value) : state.Value;
        return status == AsicRomStringReadStatus.Complete;
    }

    static bool TryReadFirstDecimalCharacter(
        Cpu cpu,
        AsicRomDecimalParseState state,
        out byte character)
    {
        character = 0;
        var valid = state.Address + state.Offset <= 0xffffff &&
            TryReadByte(cpu, state.Address + state.Offset, out character);
        while (valid && IsDecimalWhiteSpace(character))
        {
            state.Offset++;
            valid = state.Address + state.Offset <= 0xffffff &&
                TryReadByte(cpu, state.Address + state.Offset, out character);
        }
        return valid;
    }

    static bool IsDecimalWhiteSpace(byte character) => character is
        (byte)' ' or (byte)'\f' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'\v';

    static AsicRomStringReadStatus ReadDecimalDigit(
        Cpu cpu,
        AsicRomDecimalParseState state)
    {
        if (state.Address + state.Offset > 0xffffff ||
            !TryReadByte(cpu, state.Address + state.Offset, out var character))
        {
            return AsicRomStringReadStatus.Invalid;
        }
        if (character is < (byte)'0' or > (byte)'9')
        {
            return AsicRomStringReadStatus.Complete;
        }

        state.Value = unchecked(state.Value * 10 + character - (byte)'0');
        state.Offset++;
        return AsicRomStringReadStatus.Continue;
    }

    static bool CopyString(Cpu cpu)
    {
        var destination = GetUInt24(cpu, 16);
        var source = GetUInt24(cpu, 20);
        if (destination < 0 || destination > 0xffffff)
        {
            return false;
        }

        if (!TryReadStringBytes(
                cpu,
                source,
                ushort.MaxValue + 1,
                out var value) ||
            !value.Terminated ||
            (long)destination + value.Bytes.Length > 0x1000000)
        {
            return false;
        }

        return TryWriteBytes(cpu, destination, value.Bytes);
    }

    static bool TryReadStringBytes(
        Cpu cpu,
        int source,
        int maximumLength,
        out AsicRomStringBytes value)
    {
        var bytes = new List<byte>();
        var status = AsicRomStringReadStatus.Continue;
        for (var offset = 0;
             offset < maximumLength && status == AsicRomStringReadStatus.Continue;
             offset++)
        {
            status = ReadStringByte(cpu, source + offset, bytes);
        }

        value = new([.. bytes], status == AsicRomStringReadStatus.Complete);
        return status != AsicRomStringReadStatus.Invalid;
    }

    static AsicRomStringReadStatus ReadStringByte(
        Cpu cpu,
        int address,
        List<byte> bytes)
    {
        if (address > 0xffffff || !TryReadByte(cpu, address, out var value))
        {
            return AsicRomStringReadStatus.Invalid;
        }

        bytes.Add(value);
        return value == 0
            ? AsicRomStringReadStatus.Complete
            : AsicRomStringReadStatus.Continue;
    }

    static bool CopyBoundedString(Cpu cpu)
    {
        var destination = GetUInt24(cpu, 16);
        var source = GetUInt24(cpu, 20);
        var stackPointer = GetY(cpu);
        if (stackPointer < 0 || stackPointer + 1 >= cpu.Data.Length)
        {
            return false;
        }

        var count = GetDataUInt16(cpu, stackPointer);
        if ((long)destination + count > 0x1000000 ||
            !TryReadStringBytes(cpu, source, count, out var value))
        {
            return false;
        }
        var bytes = new byte[count];
        value.Bytes.CopyTo(bytes, 0);
        if (!TryWriteBytes(cpu, destination, bytes))
        {
            return false;
        }
        SetY(cpu, stackPointer + 2);
        return true;
    }

    static bool ReceivePeripheralBytes(Cpu cpu)
    {
        var source = GetUInt16(cpu, 16);
        var destination = GetUInt24(cpu, 20);
        var count = cpu.Data[24];
        if (source is not (0x0905 or 0x0915) ||
            (long)destination + count > 0x1000000)
        {
            return false;
        }

        for (var offset = 0; offset < count; offset++)
        {
            if (!TryWriteDataByte(cpu, destination + offset, cpu.ReadData(source)))
            {
                return false;
            }
        }
        return true;
    }

    static bool TransmitPeripheralBytes(Cpu cpu)
    {
        var destination = GetUInt16(cpu, 16);
        var source = GetUInt24(cpu, 20);
        var stack = GetY(cpu);
        if (destination is not (0x0905 or 0x0915) || stack >= cpu.Data.Length)
        {
            return false;
        }

        var count = cpu.ReadData(stack);
        if ((long)source + count > 0x1000000)
        {
            return false;
        }

        for (var offset = 0; offset < count; offset++)
        {
            if (!TryReadDataByte(cpu, source + offset, out var value))
            {
                return false;
            }
            cpu.WriteData(destination, value);
        }
        SetY(cpu, stack + 1);
        return true;
    }

    static bool StartStringWalk(Cpu cpu)
    {
        var source = GetUInt24(cpu, 16) & 0x7fffff;
        var callback = GetUInt24(cpu, 20);
        var stack = GetY(cpu);
        if (source >= cpu.ProgBytes.Length || callback <= 0 || callback >= cpu.ProgWords ||
            stack < 0 || stack + 5 >= cpu.Data.Length)
        {
            return false;
        }

        // The native wrapper at word 0x018d6a puts the callback context at Y
        // and a pointer to its variable-argument cursor at Y+3.
        var callbackContext = GetDataUInt24(cpu, stack);
        var argumentCursorAddress = GetDataUInt24(cpu, stack + 3);
        var argumentCursorPhysicalAddress = cpu.TranslateDataAddress(argumentCursorAddress);
        if ((uint)argumentCursorPhysicalAddress > (uint)(cpu.Data.Length - 3))
        {
            return false;
        }
        if (!TryExpandStringWalk(
                cpu,
                source,
                argumentCursorPhysicalAddress,
                out var output))
        {
            return false;
        }
        SetY(cpu, stack + 6);
        StringWalkStates.GetOrCreateValue(cpu).Push(
            new AsicRomStringWalkState(output, callback, callbackContext));
        return ContinueStringWalk(cpu);
    }

    static bool TryExpandStringWalk(
        Cpu cpu,
        int source,
        int argumentCursorAddress,
        out byte[] output)
    {
        var state = new AsicRomStringExpansionState(
            source,
            argumentCursorAddress,
            GetDataUInt24(cpu, argumentCursorAddress));
        var status = AsicRomStringReadStatus.Continue;
        while (state.Source < cpu.ProgBytes.Length &&
            status == AsicRomStringReadStatus.Continue)
        {
            status = ExpandStringWalkByte(cpu, state);
        }

        output = status == AsicRomStringReadStatus.Complete
            ? [.. state.Bytes]
            : [];
        if (status != AsicRomStringReadStatus.Complete)
        {
            return false;
        }

        SetDataUInt24(cpu, state.ArgumentCursorAddress, state.ArgumentCursor);
        return true;
    }

    static AsicRomStringReadStatus ExpandStringWalkByte(
        Cpu cpu,
        AsicRomStringExpansionState state)
    {
        var value = cpu.ProgBytes[state.Source++];
        if (value == 0)
        {
            return AsicRomStringReadStatus.Complete;
        }
        if (value != (byte)'%')
        {
            state.Bytes.Add(value);
            return AsicRomStringReadStatus.Continue;
        }
        if (state.Source >= cpu.ProgBytes.Length)
        {
            return AsicRomStringReadStatus.Invalid;
        }

        var conversion = cpu.ProgBytes[state.Source++];
        return conversion switch
        {
            (byte)'%' => AppendEscapedPercent(state),
            (byte)'d' => AppendDecimalArgument(cpu, state),
            (byte)'s' => AppendStringArgument(cpu, state),
            _ => AsicRomStringReadStatus.Invalid,
        };
    }

    static AsicRomStringReadStatus AppendEscapedPercent(
        AsicRomStringExpansionState state)
    {
        state.Bytes.Add((byte)'%');
        return AsicRomStringReadStatus.Continue;
    }

    static AsicRomStringReadStatus AppendDecimalArgument(
        Cpu cpu,
        AsicRomStringExpansionState state)
    {
        if (!TryReadDataByte(cpu, state.ArgumentCursor, out var low) ||
            !TryReadDataByte(cpu, state.ArgumentCursor + 1, out var high))
        {
            return AsicRomStringReadStatus.Invalid;
        }

        state.ArgumentCursor += 2;
        var text = unchecked((short)(low | high << 8)).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        foreach (var character in text)
        {
            state.Bytes.Add((byte)character);
        }
        return AsicRomStringReadStatus.Continue;
    }

    static AsicRomStringReadStatus AppendStringArgument(
        Cpu cpu,
        AsicRomStringExpansionState state)
    {
        if (!TryReadDataByte(cpu, state.ArgumentCursor, out var low) ||
            !TryReadDataByte(cpu, state.ArgumentCursor + 1, out var middle) ||
            !TryReadDataByte(cpu, state.ArgumentCursor + 2, out var high))
        {
            return AsicRomStringReadStatus.Invalid;
        }

        state.ArgumentCursor += 3;
        var pointer = low | middle << 8 | high << 16;
        if (!TryReadStringBytes(cpu, pointer, 0x1000000, out var value) ||
            !value.Terminated)
        {
            return AsicRomStringReadStatus.Invalid;
        }

        state.Bytes.AddRange(value.Bytes.AsSpan(0, value.Bytes.Length - 1).ToArray());
        return AsicRomStringReadStatus.Continue;
    }

    static bool ContinueStringWalk(Cpu cpu)
    {
        if (!StringWalkStates.TryGetValue(cpu, out var walks) || walks.Count == 0)
        {
            return false;
        }

        var walk = walks.Peek();
        if (walk.OutputIndex >= walk.Output.Length)
        {
            walks.Pop();
            SetUInt16(cpu, 16, walk.Output.Length);
            Return(cpu);
            return true;
        }

        var value = walk.Output[walk.OutputIndex++];
        cpu.Data[16] = value;
        SetUInt24(cpu, 20, walk.CallbackContext);
        PushReturn(cpu, StringWalkerContinuation);
        cpu.PC = walk.Callback;
        return true;
    }

    static void ExecutePrologue(Cpu cpu, int savedRegisterCount)
    {
        var frame = (GetY(cpu) - savedRegisterCount) & 0xffffff;
        SetY(cpu, frame);
        for (var index = 0; index < savedRegisterCount; index++)
        {
            cpu.WriteData(frame + index, cpu.Data[SoftwareStackRegisters[index]]);
        }
        Return(cpu);
    }

    static void ExecuteEpilogue(Cpu cpu, int savedRegisterCount)
    {
        var frame = GetY(cpu);
        Span<byte> savedRegisters = stackalloc byte[savedRegisterCount];
        for (var index = 0; index < savedRegisterCount; index++)
        {
            savedRegisters[index] = cpu.ReadData(frame + index);
        }

        var cleanupBytes = cpu.Data[30];
        SetY(cpu, (frame + cleanupBytes) & 0xffffff);
        for (var index = 0; index < savedRegisterCount; index++)
        {
            cpu.Data[SoftwareStackRegisters[index]] = savedRegisters[index];
        }
        Return(cpu);
    }

    static bool InitializeContext(Cpu cpu)
    {
        var contextAddress = GetZ(cpu);
        var state = SchedulerStates.GetValue(
            cpu,
            static _ => new AsicRomSchedulerState());
        if (!RetireDispatcherCapture(state))
        {
            return false;
        }

        var valid = state.Contexts.ContainsKey(contextAddress)
            ? ValidateExistingContext(cpu, state, contextAddress)
            : InitializeNewContext(cpu, state, contextAddress);
        if (!valid)
        {
            return false;
        }

        Return(cpu);
        return true;
    }

    static bool RetireDispatcherCapture(AsicRomSchedulerState state)
    {
        if (!state.InterruptCaptures.TryGetValue(
                InterruptDispatcherContext,
                out var capture))
        {
            return true;
        }
        if (capture.Phase != AsicRomInterruptCapturePhase.FrameFinalized)
        {
            return false;
        }

        // Context initialization is the retirement boundary when an interrupt
        // reschedules instead of unwinding through the dispatcher capture.
        state.InterruptCaptures.Remove(InterruptDispatcherContext);
        state.Contexts.Remove(InterruptDispatcherContext);
        return true;
    }

    static bool ValidateExistingContext(
        Cpu cpu,
        AsicRomSchedulerState state,
        int contextAddress) =>
        state.Metadata.TryGetValue(contextAddress, out var metadata) &&
        metadata.DescriptorAddress == contextAddress + DescriptorFromContextOffset &&
        cpu.ReadData(metadata.DescriptorAddress + 5) == metadata.Process &&
        cpu.ReadData(metadata.DescriptorAddress + DescriptorTypeOffset) is 0 or 2;

    static bool InitializeNewContext(
        Cpu cpu,
        AsicRomSchedulerState state,
        int contextAddress)
    {
        var descriptorAddress = ValidateInitialDescriptor(cpu, contextAddress);
        if (descriptorAddress is not int descriptor)
        {
            return false;
        }
        var software = ValidateInitialSoftwareStack(cpu, contextAddress, descriptor);
        if (software is not { } softwareStack)
        {
            return false;
        }
        var hardware = ValidateInitialHardwareStack(cpu, contextAddress, descriptor);
        if (hardware is not { } hardwareStack)
        {
            return false;
        }

        var context = MaterializeInitialContext(
            cpu,
            descriptor,
            softwareStack,
            hardwareStack);
        return PublishInitialContext(cpu, state, contextAddress, context);
    }

    static int? ValidateInitialDescriptor(Cpu cpu, int contextAddress)
    {
        if (contextAddress < 4 ||
            contextAddress + ContextInitialRampYOffset >= cpu.Data.Length)
        {
            return null;
        }

        var descriptorAddress = GetDataUInt16(cpu, contextAddress - 4);
        if (descriptorAddress != contextAddress + DescriptorFromContextOffset ||
            descriptorAddress + DescriptorSoftwareStackFloorOffset + 1 >= cpu.Data.Length)
        {
            return null;
        }
        if (cpu.ReadData(descriptorAddress + DescriptorTypeOffset) is not (0 or 2))
        {
            // The booted native image does not expose a recoverable type-1
            // context layout, so an inferred field mapping would be unsafe.
            return null;
        }

        return descriptorAddress;
    }

    static (byte Process, int StackPointer, byte RampY, int TaskEntry)?
        ValidateInitialSoftwareStack(Cpu cpu, int contextAddress, int descriptorAddress)
    {
        // The compiler-managed software stack is distinct from the descending
        // hardware stack used for saved registers and return addresses.
        var process = cpu.ReadData(descriptorAddress + 5);
        var softwareStackTop = GetDataUInt16(
            cpu,
            descriptorAddress + DescriptorSoftwareStackTopOffset);
        var stackPointer = GetDataUInt16(
            cpu,
            contextAddress + ContextInitialSoftwareStackPointerOffset);
        if (stackPointer != softwareStackTop)
        {
            return null;
        }

        var rampY = cpu.ReadData(contextAddress + ContextInitialRampYOffset);
        var taskEntry = GetDataUInt24(cpu, descriptorAddress + 22);
        if (taskEntry <= 0 || taskEntry >= cpu.ProgWords)
        {
            return null;
        }

        return (process, stackPointer, rampY, taskEntry);
    }

    static (int SavedStackPointer, int Floor, int Top, int ResumePc, int ResumeStackPointer)?
        ValidateInitialHardwareStack(Cpu cpu, int contextAddress, int descriptorAddress)
    {
        var savedStackPointer = GetDataUInt16(
            cpu,
            descriptorAddress + ContextHardwareStackPointerOffset);
        var allocationTop = GetDataUInt16(
            cpu,
            descriptorAddress + DescriptorHardwareStackTopOffset);
        var floor = GetDataUInt16(
            cpu,
            descriptorAddress + DescriptorHardwareStackFloorOffset);
        var top = contextAddress - 3;
        if (allocationTop != contextAddress - 2 || floor <= 0 ||
            savedStackPointer < floor || savedStackPointer + 9 > top ||
            top >= cpu.Data.Length)
        {
            return null;
        }

        var resumePc = cpu.ReadData(savedStackPointer + 9) |
            cpu.ReadData(savedStackPointer + 8) << 8 |
            cpu.ReadData(savedStackPointer + 7) << 16;
        if (resumePc <= 0 || resumePc >= cpu.ProgWords)
        {
            return null;
        }

        return (savedStackPointer, floor, top, resumePc, savedStackPointer + 9);
    }

    static (AsicRomCpuContext Context, AsicRomSchedulerContextMetadata Metadata,
        int SavedStackPointer, byte Process, int DescriptorAddress)
        MaterializeInitialContext(
            Cpu cpu,
            int descriptorAddress,
            (byte Process, int StackPointer, byte RampY, int TaskEntry) software,
            (int SavedStackPointer, int Floor, int Top, int ResumePc,
                int ResumeStackPointer) hardware)
    {
        var registers = new byte[32];
        registers[16] = cpu.ReadData(hardware.SavedStackPointer + 3);
        registers[17] = cpu.ReadData(hardware.SavedStackPointer + 4);
        // The task's visible entry prologue reserves its first software frame.
        registers[Y] = (byte)software.StackPointer;
        registers[Y + 1] = (byte)(software.StackPointer >> 8);
        registers[30] = cpu.ReadData(hardware.SavedStackPointer + 5);
        registers[31] = cpu.ReadData(hardware.SavedStackPointer + 6);
        var context = new AsicRomCpuContext(
            registers,
            hardware.ResumePc,
            hardware.ResumeStackPointer,
            0,
            0,
            software.RampY,
            cpu.ReadData(hardware.SavedStackPointer + 1),
            0,
            cpu.ReadData(hardware.SavedStackPointer + 2),
            SnapshotHardwareStack(cpu, hardware.ResumeStackPointer, hardware.Top),
            descriptorAddress);
        var metadata = new AsicRomSchedulerContextMetadata(
            descriptorAddress,
            software.Process,
            software.TaskEntry,
            hardware.Floor,
            hardware.Top);
        return (context, metadata, hardware.SavedStackPointer,
            software.Process, descriptorAddress);
    }

    static bool PublishInitialContext(
        Cpu cpu,
        AsicRomSchedulerState state,
        int contextAddress,
        (AsicRomCpuContext Context, AsicRomSchedulerContextMetadata Metadata,
            int SavedStackPointer, byte Process, int DescriptorAddress) initialized)
    {
        state.Contexts[contextAddress] = initialized.Context;
        state.Metadata[contextAddress] = initialized.Metadata;
        Array.Clear(cpu.Data, contextAddress, 26);
        SetDataUInt16(cpu, contextAddress, 0xfdfd);
        // Publish the scheduler priority used by the native wake/preempt test.
        cpu.WriteData(contextAddress + 11,
            cpu.ReadData(initialized.DescriptorAddress + 11));
        SetDataUInt16(cpu, contextAddress + 12, initialized.DescriptorAddress);
        SetDataUInt16(cpu, contextAddress + 16,
            GetDataUInt16(cpu, initialized.DescriptorAddress + 16));
        SetDataUInt16(cpu, contextAddress + ContextHardwareStackPointerOffset,
            initialized.SavedStackPointer);
        cpu.WriteData(contextAddress + 5, initialized.Process);
        cpu.WriteData(contextAddress + 25,
            cpu.ReadData(initialized.DescriptorAddress + 25));
        if (!TransferPendingSignals(cpu, contextAddress, initialized.DescriptorAddress))
        {
            state.Contexts.Remove(contextAddress);
            state.Metadata.Remove(contextAddress);
            return false;
        }

        return true;
    }

    static bool SaveContext(Cpu cpu)
    {
        if (!TryPrepareContextSave(cpu, out var save))
        {
            return false;
        }

        if (!PublishPendingReceiveWake(
                cpu,
                save.ContextAddress,
                save.Metadata.DescriptorAddress))
        {
            return false;
        }

        save.State.Contexts[save.ContextAddress] = save.Context;
        PublishTimerWheelState(
            cpu,
            save.ContextAddress,
            save.Metadata.DescriptorAddress);
        Return(cpu);
        return true;
    }

    static bool TryPrepareContextSave(Cpu cpu, out AsicRomContextSave save)
    {
        save = default;
        var contextAddress = GetZ(cpu);
        if (contextAddress < 0 ||
            contextAddress + ContextHardwareStackPointerOffset + 1 >= cpu.Data.Length)
        {
            return false;
        }

        var state = SchedulerStates.GetValue(
            cpu,
            static _ => new AsicRomSchedulerState());
        if (!state.Metadata.TryGetValue(contextAddress, out var metadata))
        {
            return false;
        }
        if (!TryCaptureSavedContext(cpu, contextAddress, metadata, out var context))
        {
            return false;
        }

        save = new(state, contextAddress, metadata, context);
        return true;
    }

    static bool TryCaptureSavedContext(
        Cpu cpu,
        int contextAddress,
        AsicRomSchedulerContextMetadata metadata,
        out AsicRomCpuContext context)
    {
        context = null!;
        var savedStackPointer = GetDataUInt16(
            cpu,
            contextAddress + ContextHardwareStackPointerOffset);
        if (savedStackPointer < metadata.HardwareStackFloor ||
            savedStackPointer + 9 > metadata.HardwareStackTop)
        {
            return false;
        }

        // The firmware wrapper has already pushed R31, R30, R17, R16, SREG, and
        // RAMPZ. The saved SP points at the slot subsequently used by CALL 0x3f0160.
        var registers = cpu.Data[..32].ToArray();
        var rampZ = cpu.ReadData(savedStackPointer + 1);
        var sreg = cpu.ReadData(savedStackPointer + 2);
        registers[16] = cpu.ReadData(savedStackPointer + 3);
        registers[17] = cpu.ReadData(savedStackPointer + 4);
        registers[30] = cpu.ReadData(savedStackPointer + 5);
        registers[31] = cpu.ReadData(savedStackPointer + 6);
        var resumePc = cpu.ReadData(savedStackPointer + 9) |
                       (cpu.ReadData(savedStackPointer + 8) << 8) |
                       (cpu.ReadData(savedStackPointer + 7) << 16);
        if (resumePc <= 0 || resumePc >= cpu.ProgWords)
        {
            return false;
        }

        var resumeStackPointer = savedStackPointer + 9;
        context = new AsicRomCpuContext(
            registers,
            resumePc,
            resumeStackPointer,
            cpu.Data[RampD],
            cpu.Data[RampX],
            cpu.Data[RampY],
            rampZ,
            cpu.Data[Eind],
            sreg,
            SnapshotHardwareStack(cpu, resumeStackPointer, metadata.HardwareStackTop),
            metadata.DescriptorAddress);
        return true;
    }

    static bool PublishPendingReceiveWake(
        Cpu cpu,
        int contextAddress,
        int descriptorAddress)
    {
        if (cpu.ReadData(contextAddress + ContextStateOffset) != 4)
        {
            return true;
        }

        // A sender always appends to the static descriptor queue, including
        // when the receiver is still active. If that arrival wins the race
        // with the receive wrapper's final scan, the wrapper clears its live
        // runnable bit and saves state 4 while the real signal is stranded on
        // the descriptor. The native sender has already cleared bit 2 in the
        // descriptor state; republish only its ordinary runnable bitmap bit so
        // the scheduler can restore the task and let native receive inspect
        // the queued record. Do not use timer-style state 1 here: restore must
        // leave state zero so the receive core rescans the transferred queue.
        // Nonmatching signals are rejected by that scan and the task can block
        // again without any fabricated delivery.
        var pendingHead = GetDataUInt16(cpu, descriptorAddress);
        if ((pendingHead & 0xff) == 0xfd)
        {
            return true;
        }

        if (!TryGetSignalQueueTail(cpu, descriptorAddress, pendingHead, out _))
        {
            return false;
        }

        return PublishRunnableBit(cpu, descriptorAddress);
    }

    static bool PublishRunnableBit(Cpu cpu, int descriptorAddress)
    {
        var bitmapAddress = GetDataUInt16(cpu, descriptorAddress + 16);
        var mask = cpu.ReadData(descriptorAddress + 25);
        if (bitmapAddress <= 0 || bitmapAddress >= cpu.Data.Length || mask == 0)
        {
            return false;
        }

        cpu.WriteData(bitmapAddress, (byte)(cpu.ReadData(bitmapAddress) | mask));
        return true;
    }

    static bool TryGetSignalQueueTail(
        Cpu cpu,
        int queueAddress,
        int head,
        out int tail)
    {
        tail = GetDataUInt16(cpu, queueAddress + 2);
        return head > 0 && head + 1 < cpu.Data.Length &&
            tail > 0 && tail + 1 < cpu.Data.Length &&
            (GetDataUInt16(cpu, tail) & 0xff) == 0xfd;
    }

    static void PublishTimerWheelState(
        Cpu cpu,
        int contextAddress,
        int descriptorAddress)
    {
        var contextState = cpu.ReadData(contextAddress + ContextStateOffset);
        if (contextState is not (4 or 8))
        {
            return;
        }

        // Pure delays use state 8, while a receive with a timeout changes to
        // state 4 after joining the same timer wheel. The insertion helper
        // writes both wheel links into the active context, while the timer and
        // other insert/remove operations address the static descriptor.
        if (contextState == 8)
        {
            cpu.WriteData(descriptorAddress + ContextStateOffset, 8);
        }
        cpu.WriteData(descriptorAddress + 14, cpu.ReadData(contextAddress + 14));
        cpu.WriteData(descriptorAddress + 15, cpu.ReadData(contextAddress + 15));
    }

    static bool FinalizeInterruptContextSave(Cpu cpu)
    {
        if (!TryGetInterruptFinalization(cpu, out var finalization))
        {
            return false;
        }

        cpu.Data[RampX] = 0;
        cpu.Data[RampY] = 0;
        cpu.Data[Eind] = 0;
        PublishFinalizedInterruptContext(cpu, finalization);
        Return(cpu);
        return true;
    }

    static bool TryGetInterruptFinalization(
        Cpu cpu,
        out AsicRomInterruptFinalization finalization)
    {
        finalization = default;
        var contextAddress = GetZ(cpu);
        if (!SchedulerStates.TryGetValue(cpu, out var state) ||
            !state.InterruptCaptures.TryGetValue(contextAddress, out var capture) ||
            capture.Phase != AsicRomInterruptCapturePhase.EdgeCaptured ||
            contextAddress < 0 ||
            contextAddress + ContextHardwareStackPointerOffset + 1 >= cpu.Data.Length)
        {
            return false;
        }

        var capturedContext = capture.Context;
        var savedStackPointer = GetDataUInt16(
            cpu,
            contextAddress + ContextHardwareStackPointerOffset);
        if (savedStackPointer < 0 || savedStackPointer + 9 >= cpu.Data.Length)
        {
            return false;
        }

        var resumeStackPointer = savedStackPointer + 9;
        if (capturedContext.StackPointer != resumeStackPointer ||
            !MatchesUnclobberedInterruptByte(cpu, savedStackPointer + 1, capturedContext.RampZ) ||
            !MatchesUnclobberedInterruptSreg(cpu, savedStackPointer + 2, capturedContext.Sreg) ||
            !MatchesUnclobberedInterruptByte(cpu, savedStackPointer + 3, capturedContext.Registers[16]) ||
            !MatchesUnclobberedInterruptByte(cpu, savedStackPointer + 4, capturedContext.Registers[17]) ||
            !MatchesUnclobberedInterruptByte(cpu, savedStackPointer + 5, capturedContext.Registers[30]) ||
            !MatchesUnclobberedInterruptByte(cpu, savedStackPointer + 6, capturedContext.Registers[31]) ||
            !MatchesUnclobberedInterruptByte(cpu, savedStackPointer + 7, (byte)(capturedContext.Pc >> 16)) ||
            !MatchesUnclobberedInterruptByte(cpu, savedStackPointer + 8, (byte)(capturedContext.Pc >> 8)) ||
            !MatchesUnclobberedInterruptByte(cpu, savedStackPointer + 9, (byte)capturedContext.Pc))
        {
            return false;
        }

        finalization = new(state, contextAddress, capture);
        return true;
    }

    static void PublishFinalizedInterruptContext(
        Cpu cpu,
        AsicRomInterruptFinalization finalization)
    {
        if (finalization.State.Metadata.TryGetValue(
                finalization.ContextAddress,
                out var metadata))
        {
            PublishTimerWheelState(
                cpu,
                finalization.ContextAddress,
                metadata.DescriptorAddress);
        }
        finalization.State.Contexts[finalization.ContextAddress] =
            finalization.Capture.Context;
        finalization.State.InterruptCaptures[finalization.ContextAddress] =
            finalization.Capture with
            {
                Phase = AsicRomInterruptCapturePhase.FrameFinalized,
            };
    }

    static bool MatchesUnclobberedInterruptByte(Cpu cpu, int address, byte expected) =>
        address is >= InterruptRomCallOverwriteFirst and <= InitialHardwareStackTop ||
        cpu.ReadData(address) == expected;

    static bool MatchesUnclobberedInterruptSreg(Cpu cpu, int address, byte expected) =>
        address is >= InterruptRomCallOverwriteFirst and <= InitialHardwareStackTop ||
        (cpu.ReadData(address) & 0x7f) == (expected & 0x7f);

    static bool RestoreInterruptedContext(Cpu cpu)
    {
        var contextAddress = GetZ(cpu);
        if (!TryGetFinalizedInterruptCapture(cpu, contextAddress, out var finalized))
        {
            return false;
        }

        var state = finalized.State;
        var context = finalized.Capture.Context;
        var metadata = state.Metadata.TryGetValue(contextAddress, out var foundMetadata)
            ? foundMetadata
            : null;
        if (!RestoreInterruptedEnvironment(cpu, state, context, metadata))
        {
            return false;
        }
        if (!MergeInterruptedSignalsIfPresent(cpu, contextAddress, metadata))
        {
            return false;
        }

        ConsumeInterruptedCapture(cpu, state, contextAddress, context);
        return true;
    }

    static bool TryGetFinalizedInterruptCapture(
        Cpu cpu,
        int contextAddress,
        out (AsicRomSchedulerState State, AsicRomInterruptCapture Capture) finalized)
    {
        finalized = default;
        if (!SchedulerStates.TryGetValue(cpu, out var state) ||
            !state.InterruptCaptures.TryGetValue(contextAddress, out var capture))
        {
            SchedulerStates.GetValue(
                cpu,
                static _ => new AsicRomSchedulerState())
                .RejectedUncapturedInterruptRestoreCount++;
            return false;
        }
        if (capture.Phase != AsicRomInterruptCapturePhase.FrameFinalized)
        {
            return false;
        }

        finalized = (state, capture);
        return true;
    }

    static bool RestoreInterruptedEnvironment(
        Cpu cpu,
        AsicRomSchedulerState state,
        AsicRomCpuContext context,
        AsicRomSchedulerContextMetadata? metadata)
    {
        // A dispatcher capture can retain a signal-provided address
        // environment even though it has no persistent task metadata.
        var descriptorAddress = metadata?.DescriptorAddress ??
            context.AddressEnvironmentDescriptorAddress;
        return descriptorAddress == 0 ||
            TryConfigureSoftwareStackWindows(cpu, state, descriptorAddress);
    }

    static bool MergeInterruptedSignalsIfPresent(
        Cpu cpu,
        int contextAddress,
        AsicRomSchedulerContextMetadata? metadata) =>
        metadata is null || MergeInterruptedSignals(cpu, contextAddress, metadata);

    static bool MergeInterruptedSignals(
        Cpu cpu,
        int contextAddress,
        AsicRomSchedulerContextMetadata metadata)
    {
        var hasPendingSignals =
            (GetDataUInt16(cpu, metadata.DescriptorAddress) & 0xff) != 0xfd;
        if (!TransferPendingSignals(cpu, contextAddress, metadata.DescriptorAddress))
        {
            return false;
        }
        if (hasPendingSignals)
        {
            // The receive core uses +39 to restart a live queue scan after a
            // concurrent signal append.
            cpu.WriteData(contextAddress + 39, 0xff);
        }

        var pendingState = cpu.ReadData(
            metadata.DescriptorAddress + ContextStateOffset);
        if (pendingState != 0)
        {
            cpu.WriteData(contextAddress + ContextStateOffset, pendingState);
            cpu.WriteData(metadata.DescriptorAddress + ContextStateOffset, 0);
        }
        return true;
    }

    static void ConsumeInterruptedCapture(
        Cpu cpu,
        AsicRomSchedulerState state,
        int contextAddress,
        AsicRomCpuContext context)
    {
        ApplyContext(cpu, context, enableInterrupts: false);
        state.InterruptCaptures.Remove(contextAddress);
        state.CapturedInterruptRestoreCount++;
        if (contextAddress == InterruptDispatcherContext)
        {
            // The dispatcher snapshot represents one interrupted scan or idle
            // loop, rather than a persistent task context.
            state.Contexts.Remove(contextAddress);
        }
    }

    static bool RestoreContext(Cpu cpu)
    {
        if (!TryPrepareContextRestore(cpu, out var restore))
        {
            return false;
        }

        if (!ValidateRestoreCapture(restore, out var consumesInterruptCapture))
        {
            return false;
        }
        if (!TransferPendingSignals(
                cpu,
                restore.ContextAddress,
                restore.Metadata.DescriptorAddress))
        {
            return false;
        }

        RestoreTimerLinks(cpu, restore);
        cpu.WriteData(
            restore.ContextAddress + ContextStateOffset,
            cpu.ReadData(restore.Metadata.DescriptorAddress + ContextStateOffset));
        cpu.WriteData(
            restore.Metadata.DescriptorAddress + ContextStateOffset,
            0);
        ApplyContext(cpu, restore.Context, enableInterrupts: true);
        ConsumeRestoreCapture(restore, consumesInterruptCapture);
        return true;
    }

    static bool TryPrepareContextRestore(
        Cpu cpu,
        out AsicRomContextRestore restore)
    {
        restore = default;
        var contextAddress = GetZ(cpu);
        if (!SchedulerStates.TryGetValue(cpu, out var state) ||
            !state.Contexts.TryGetValue(contextAddress, out var context))
        {
            return false;
        }
        if (!state.Metadata.TryGetValue(contextAddress, out var metadata) ||
            !TryConfigureSoftwareStackWindows(
                cpu,
                state,
                metadata.DescriptorAddress))
        {
            return false;
        }

        restore = new(state, contextAddress, context, metadata);
        return true;
    }

    static bool ValidateRestoreCapture(
        AsicRomContextRestore restore,
        out bool consumesInterruptCapture)
    {
        consumesInterruptCapture = false;
        if (!restore.State.InterruptCaptures.TryGetValue(
                restore.ContextAddress,
                out var capture))
        {
            return true;
        }
        if (capture.Phase != AsicRomInterruptCapturePhase.FrameFinalized ||
            !ReferenceEquals(capture.Context, restore.Context))
        {
            return false;
        }

        consumesInterruptCapture = true;
        return true;
    }

    static void RestoreTimerLinks(Cpu cpu, AsicRomContextRestore restore)
    {
        var savedContextState = cpu.ReadData(
            restore.ContextAddress + ContextStateOffset);
        if (savedContextState is 4 or 8)
        {
            cpu.WriteData(
                restore.ContextAddress + 14,
                cpu.ReadData(restore.Metadata.DescriptorAddress + 14));
            cpu.WriteData(
                restore.ContextAddress + 15,
                cpu.ReadData(restore.Metadata.DescriptorAddress + 15));
        }
    }

    static void ConsumeRestoreCapture(
        AsicRomContextRestore restore,
        bool consumesInterruptCapture)
    {
        if (consumesInterruptCapture)
        {
            restore.State.InterruptCaptures.Remove(restore.ContextAddress);
        }
    }

    static void ApplyContext(Cpu cpu, AsicRomCpuContext context, bool enableInterrupts)
    {
        if (context.HardwareStack is { } hardwareStack)
        {
            hardwareStack.Bytes.CopyTo(cpu.Data, hardwareStack.StartAddress);
        }
        context.Registers.CopyTo(cpu.Data, 0);
        cpu.Data[RampD] = context.RampD;
        cpu.Data[RampX] = context.RampX;
        cpu.Data[RampY] = context.RampY;
        cpu.Data[RampZ] = context.RampZ;
        cpu.Data[Eind] = context.Eind;
        cpu.SP = context.StackPointer;
        cpu.PC = context.Pc;
        cpu.SetStatusRegister(enableInterrupts
            ? (byte)(context.Sreg | 0x80)
            : context.Sreg);
    }

    static bool TransferPendingSignals(Cpu cpu, int contextAddress, int descriptorAddress)
    {
        var pendingHead = GetDataUInt16(cpu, descriptorAddress);
        if ((pendingHead & 0xff) == 0xfd)
        {
            return true;
        }

        if (!TryGetSignalQueueTail(
                cpu,
                descriptorAddress,
                pendingHead,
                out var pendingTail))
        {
            return false;
        }

        var contextHead = GetDataUInt16(cpu, contextAddress);
        var pending = new AsicRomSignalQueue(pendingHead, pendingTail);
        if (!InstallPendingSignals(cpu, contextAddress, contextHead, pending))
        {
            return false;
        }

        SetDataUInt16(cpu, descriptorAddress, 0xfdfd);
        SetDataUInt16(cpu, descriptorAddress + 2, descriptorAddress);
        return true;
    }

    static bool InstallPendingSignals(
        Cpu cpu,
        int contextAddress,
        int contextHead,
        AsicRomSignalQueue pending)
    {
        if ((contextHead & 0xff) != 0xfd)
        {
            return AppendPendingSignals(cpu, contextAddress, contextHead, pending);
        }

        SetDataUInt16(cpu, contextAddress, pending.Head);
        SetDataUInt16(cpu, contextAddress + 2, pending.Tail);
        return true;
    }

    static bool AppendPendingSignals(
        Cpu cpu,
        int contextAddress,
        int contextHead,
        AsicRomSignalQueue pending)
    {
        if (!TryGetSignalQueueTail(cpu, contextAddress, contextHead, out var contextTail))
        {
            return false;
        }

        SetDataUInt16(cpu, contextTail, pending.Head);
        SetDataUInt16(cpu, contextAddress + 2, pending.Tail);
        return true;
    }

    static int GetY(Cpu cpu) => cpu.TranslateDataAddress(
        cpu.GetUint16(Y) | (cpu.Data[RampY] << 16));

    static bool TryConfigureSoftwareStackWindows(
        Cpu cpu,
        AsicRomSchedulerState state,
        int activeDescriptorAddress)
    {
        cpu.ClearDataAddressWindow();
        if (!TryAddSoftwareStackWindow(cpu, activeDescriptorAddress))
        {
            return false;
        }

        // Context initialization is the mask-ROM ABI boundary that publishes
        // a task's non-overlapping bank-02 software-stack aperture. Keep every
        // initialized aperture installed so a signal can legally retain a
        // pointer into its sender's stack without inferring ownership from
        // queue contents or fabricating a per-signal mapping lifetime.
        foreach (var metadata in state.Metadata.Values)
        {
            var descriptorAddress = metadata.DescriptorAddress;
            if (descriptorAddress != activeDescriptorAddress &&
                !TryAddSoftwareStackWindow(cpu, descriptorAddress))
            {
                return false;
            }
        }
        return true;
    }

    static bool TryAddSoftwareStackWindow(Cpu cpu, int descriptorAddress)
    {
        if (!TryGetSoftwareStackWindow(cpu, descriptorAddress, out var window))
        {
            return false;
        }

        cpu.AddDataAddressWindow(
            window.LogicalFirst,
            window.LogicalLast,
            window.PhysicalFirst);
        return true;
    }

    static bool TryGetSoftwareStackWindow(
        Cpu cpu,
        int descriptorAddress,
        out AsicRomSoftwareStackWindow window)
    {
        window = default;
        if (descriptorAddress < 0 ||
            descriptorAddress + DescriptorSoftwareStackFloorOffset + 1 >= cpu.Data.Length)
        {
            return false;
        }

        var memoryBank = cpu.ReadData(
            descriptorAddress + DescriptorMemoryBankOffset) << 16;
        var physicalFirst = memoryBank | GetDataUInt16(
            cpu,
            descriptorAddress + DescriptorSoftwareStackFloorOffset);
        var physicalLast = memoryBank | GetDataUInt16(
            cpu,
            descriptorAddress + DescriptorSoftwareStackTopOffset);
        if (physicalLast < physicalFirst || physicalLast >= cpu.Data.Length)
        {
            return false;
        }

        window = new(
            0x020000 | (physicalFirst & 0xffff),
            0x020000 | (physicalLast & 0xffff),
            physicalFirst);
        return true;
    }

    static AsicRomHardwareStackSnapshot? SnapshotHardwareStack(
        Cpu cpu,
        int stackPointer,
        int stackTop)
    {
        var stackStart = stackPointer + 1;
        return stackStart <= stackTop && stackTop < cpu.Data.Length
            ? new AsicRomHardwareStackSnapshot(stackStart, cpu.Data[stackStart..(stackTop + 1)].ToArray())
            : null;
    }

    static int GetZ(Cpu cpu) => cpu.GetUint16(Z) | (cpu.Data[RampZ] << 16);


    static int GetDataUInt16(Cpu cpu, int address) =>
        cpu.ReadData(address) | (cpu.ReadData(address + 1) << 8);

    static int GetDataUInt24(Cpu cpu, int address) =>
        GetDataUInt16(cpu, address) | (cpu.ReadData(address + 2) << 16);

    static int GetDataRegisterUInt24(Cpu cpu, int register) =>
        cpu.Data[register] | (cpu.Data[register + 1] << 8) | (cpu.Data[register + 2] << 16);

    static int GetUploadPointer(Cpu cpu) => cpu.GetUint16(Z) | (cpu.Data[19] << 16);

    static void SetDataUInt16(Cpu cpu, int address, int value)
    {
        cpu.WriteData(address, (byte)value);
        cpu.WriteData(address + 1, (byte)(value >> 8));
    }

    static void SetDataUInt24(Cpu cpu, int address, int value)
    {
        SetDataUInt16(cpu, address, value);
        cpu.WriteData(address + 2, (byte)(value >> 16));
    }

    static int GetUInt16(Cpu cpu, int register) => cpu.Data[register] | (cpu.Data[register + 1] << 8);

    static int GetUInt24(Cpu cpu, int register) =>
        GetUInt16(cpu, register) | (cpu.Data[register + 2] << 16);

    static void SetUInt16(Cpu cpu, int register, int value)
    {
        cpu.Data[register] = (byte)value;
        cpu.Data[register + 1] = (byte)(value >> 8);
    }

    static void SetUInt24(Cpu cpu, int register, int value)
    {
        SetUInt16(cpu, register, value);
        cpu.Data[register + 2] = (byte)(value >> 16);
    }

    static uint GetUInt32(Cpu cpu, int register) =>
        (uint)(cpu.Data[register] |
               (cpu.Data[register + 1] << 8) |
               (cpu.Data[register + 2] << 16) |
               (cpu.Data[register + 3] << 24));

    static void SetUInt32(Cpu cpu, int register, uint value)
    {
        cpu.Data[register] = (byte)value;
        cpu.Data[register + 1] = (byte)(value >> 8);
        cpu.Data[register + 2] = (byte)(value >> 16);
        cpu.Data[register + 3] = (byte)(value >> 24);
    }

    static float GetSingle(Cpu cpu, int register) =>
        BitConverter.Int32BitsToSingle(unchecked((int)GetUInt32(cpu, register)));

    static void SetSingle(Cpu cpu, int register, float value) =>
        SetUInt32(cpu, register, unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    static void SetY(Cpu cpu, int address)
    {
        address = cpu.UntranslateDataAddress(address);
        cpu.SetUint16(Y, address);
        cpu.Data[RampY] = (byte)(address >> 16);
    }

    static void Return(Cpu cpu)
    {
        var stackTop = cpu.SP + (cpu.Pc22Bits ? 3 : 2);
        cpu.SP = stackTop;
        cpu.PC = cpu.Data[stackTop] | (cpu.Data[stackTop - 1] << 8);
        if (cpu.Pc22Bits)
        {
            cpu.PC |= cpu.Data[stackTop - 2] << 16;
        }
        cpu.Cycles += cpu.Pc22Bits ? 5 : 4;
    }

    static void PushReturn(Cpu cpu, int returnAddress)
    {
        var stackTop = cpu.SP;
        cpu.Data[stackTop] = (byte)returnAddress;
        cpu.Data[stackTop - 1] = (byte)(returnAddress >> 8);
        if (cpu.Pc22Bits)
        {
            cpu.Data[stackTop - 2] = (byte)(returnAddress >> 16);
        }
        cpu.SP = stackTop - (cpu.Pc22Bits ? 3 : 2);
        cpu.Cycles += cpu.Pc22Bits ? 5 : 4;
    }
}
