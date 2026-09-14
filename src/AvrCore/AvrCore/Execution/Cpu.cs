// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js cpu.ts)

using System.Runtime.CompilerServices;

namespace AvrCore.Execution;

public class Cpu
{
    const int RegisterSpace = 0x100;
    const int MaxInterrupts = 128; // Enough for ATmega2560
    const int DataAddressMask = 0xffffff;

    internal const int StackPointerRegister = 93;
    internal const int StatusRegister = 95;

    // Internal (see AvrCore.csproj's InternalsVisibleTo comment): these back a
    // per-instruction memory/hook access path, so they stay plain indexable
    // fields rather than public properties wrapping a copy or collection type.
    internal readonly byte[] Data;
    internal readonly byte[] ProgBytes;
    internal readonly CpuMemoryHookTable<CpuMemoryReadHook> ReadHooks;
    internal readonly CpuMemoryHookTable<CpuMemoryWriteHook> WriteHooks;
    public int DataAddressSpaceSize { get; }
    public CpuProgramWordReadHook? ProgramWordReadHook { get; set; }
    public int ProgramWordReadHookStart { get; set; }
    public int ProgramWordReadHookEnd { get; set; } = int.MaxValue;
    readonly AvrInterruptConfig?[] _pendingInterrupts = new AvrInterruptConfig?[MaxInterrupts];
    Action? _afterTick;
    int _dataWindowLogicalFirst = 1;
    int _dataWindowLogicalLast;
    int _dataWindowPhysicalFirst;
    int[] _extraDataWindowLogicalFirst = new int[2];
    int[] _extraDataWindowLogicalLast = new int[2];
    int[] _extraDataWindowPhysicalFirst = new int[2];
    int _extraDataWindowCount;
    readonly List<CpuMemoryReadHookRange> _readHookRanges = [];
    readonly List<CpuMemoryWriteHookRange> _writeHookRanges = [];

    /// <summary>Whether the program counter (PC) can address 22 bits (the default is 16)</summary>
    public bool Pc22Bits { get; }

    /// <summary>Whether indirect/direct data addresses use the RAMP registers</summary>
    public bool Data24Bits { get; }

    /// <summary>Data register whose high byte extends direct LDS/STS addresses.</summary>
    public int DirectDataRampRegister { get; }

    /// <summary>
    /// Called by the WDR instruction. The Watchdog peripheral attaches to it to listen for
    /// WDR (watchdog reset).
    /// </summary>
    public Action OnWatchdogReset { get; set; } = () => { };

    /// <summary>
    /// Raised only when an instruction or interrupt transition changes SREG.I.
    /// Peripherals can react to interrupt eligibility without being scanned on
    /// every instruction boundary.
    /// </summary>
    public event EventHandler<CpuGlobalInterruptEnableChangedEventArgs>? GlobalInterruptEnableChanged;

    /// <summary>
    /// Observes each successful access through <see cref="ReadData"/>.
    /// The callback is diagnostic-only and is not called for direct register-array reads.
    /// </summary>
    public CpuDataReadObserver? DataReadObserver { get; set; }

    /// <summary>
    /// Observes each successful access through <see cref="WriteData"/>,
    /// including writes consumed by a device hook.
    /// </summary>
    public CpuDataWriteObserver? DataWriteObserver { get; set; }

    /// <summary>Program counter (in words)</summary>
    public int PC { get; set; }

    /// <summary>Clock cycle counter</summary>
    public long Cycles { get; set; }

    public int NextInterrupt { get; set; } = -1;
    public int MaxInterrupt { get; set; }

    public Cpu(
        byte[] progBytes,
        int sramBytes = 8192,
        int directDataRampRegister = 0x58,
        int dataAddressSpaceSize = 0)
    {
        ArgumentNullException.ThrowIfNull(progBytes);
        if (directDataRampRegister is < 0 or >= RegisterSpace)
        {
            throw new ArgumentOutOfRangeException(nameof(directDataRampRegister));
        }

        ProgBytes = progBytes;
        var dataBackingSize = sramBytes + RegisterSpace;
        DataAddressSpaceSize = dataAddressSpaceSize == 0
            ? dataBackingSize
            : dataAddressSpaceSize;
        if (DataAddressSpaceSize < dataBackingSize)
        {
            throw new ArgumentOutOfRangeException(nameof(dataAddressSpaceSize));
        }
        Data = new byte[dataBackingSize];
        ReadHooks = new(DataAddressSpaceSize);
        WriteHooks = new(DataAddressSpaceSize);
        Pc22Bits = progBytes.Length > 0x20000;
        Data24Bits = DataAddressSpaceSize > 0x10000;
        DirectDataRampRegister = directDataRampRegister;
        Reset();
    }

    /// <summary>Size of the program memory, in words</summary>
    public int ProgWords => ProgBytes.Length >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetProgWord(int wordIndex)
    {
        if (TryReadHookedProgramWord(wordIndex, out int value))
        {
            return value;
        }
        return ProgBytes[wordIndex * 2] | (ProgBytes[wordIndex * 2 + 1] << 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool TryReadHookedProgramWord(int wordIndex, out int value)
    {
        value = 0;
        var hook = ProgramWordReadHook;
        if (hook is null ||
            (uint)(wordIndex - ProgramWordReadHookStart) >=
            (uint)(ProgramWordReadHookEnd - ProgramWordReadHookStart))
        {
            return false;
        }

        int? hookedValue = hook(wordIndex);
        if (!hookedValue.HasValue)
        {
            return false;
        }

        value = hookedValue.Value;
        return true;
    }

    public void SetProgWord(int wordIndex, int value)
    {
        ProgBytes[wordIndex * 2] = (byte)value;
        ProgBytes[wordIndex * 2 + 1] = (byte)(value >> 8);
    }

    public int GetUint16(int addr) => Data[addr] | (Data[addr + 1] << 8);

    public void SetUint16(int addr, int value)
    {
        Data[addr] = (byte)value;
        Data[addr + 1] = (byte)(value >> 8);
    }

    public short GetInt16(int addr) => (short)GetUint16(addr);

    public sbyte GetInt8(int addr) => (sbyte)Data[addr];

    internal int GetDataAddress(int pointerRegister, int rampRegister, int displacement = 0)
    {
        var address = GetUint16(pointerRegister);
        if (Data24Bits)
        {
            address |= Data[rampRegister] << 16;
        }
        address = (address + displacement) & DataAddressMask;
        return TranslateDataAddress(address);
    }

    internal void SetDataAddress(int pointerRegister, int rampRegister, int address)
    {
        address &= DataAddressMask;
        address = UntranslateDataAddress(address);
        SetUint16(pointerRegister, address);
        if (Data24Bits)
        {
            Data[rampRegister] = (byte)(address >> 16);
        }
    }

    internal int GetDirectDataAddress(int address) => TranslateDataAddress(
        Data24Bits ? address | (Data[DirectDataRampRegister] << 16) : address);

    /// <summary>
    /// Configures the ASIC-style logical data window used by the current
    /// firmware context. AVR pointer registers retain logical addresses while
    /// data-bus accesses inside the window reach its physical backing range.
    /// </summary>
    public void ConfigureDataAddressWindow(
        int logicalFirst,
        int logicalLast,
        int physicalFirst)
    {
        ClearDataAddressWindow();
        AddDataAddressWindow(logicalFirst, logicalLast, physicalFirst);
    }

    /// <summary>
    /// Adds a lower-priority logical window without replacing the primary
    /// context window. This represents a task-owned range loaned to another
    /// firmware context through the scheduler ABI.
    /// </summary>
    public void AddDataAddressWindow(
        int logicalFirst,
        int logicalLast,
        int physicalFirst)
    {
        ValidateDataAddressWindow(logicalFirst, logicalLast, physicalFirst);
        if (TrySetPrimaryDataAddressWindow(
                logicalFirst,
                logicalLast,
                physicalFirst))
        {
            return;
        }
        if (MatchesPrimaryDataAddressWindow(
                logicalFirst,
                logicalLast,
                physicalFirst))
        {
            return;
        }
        if (ContainsExtraDataAddressWindow(
                logicalFirst,
                logicalLast,
                physicalFirst))
        {
            return;
        }

        EnsureExtraDataAddressWindowCapacity();
        AppendExtraDataAddressWindow(logicalFirst, logicalLast, physicalFirst);
    }

    void ValidateDataAddressWindow(
        int logicalFirst,
        int logicalLast,
        int physicalFirst)
    {
        int length = logicalLast - logicalFirst + 1;
        if (logicalFirst < 0 || logicalLast < logicalFirst ||
            logicalLast > DataAddressMask || physicalFirst < 0 ||
            (long)physicalFirst + length > Data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalFirst));
        }
    }

    bool TrySetPrimaryDataAddressWindow(
        int logicalFirst,
        int logicalLast,
        int physicalFirst)
    {
        if (_dataWindowLogicalFirst <= _dataWindowLogicalLast)
        {
            return false;
        }

        _dataWindowLogicalFirst = logicalFirst;
        _dataWindowLogicalLast = logicalLast;
        _dataWindowPhysicalFirst = physicalFirst;
        return true;
    }

    bool MatchesPrimaryDataAddressWindow(
        int logicalFirst,
        int logicalLast,
        int physicalFirst) =>
        _dataWindowLogicalFirst == logicalFirst &&
        _dataWindowLogicalLast == logicalLast &&
        _dataWindowPhysicalFirst == physicalFirst;

    bool ContainsExtraDataAddressWindow(
        int logicalFirst,
        int logicalLast,
        int physicalFirst)
    {
        for (var index = 0; index < _extraDataWindowCount; index++)
        {
            if (_extraDataWindowLogicalFirst[index] == logicalFirst &&
                _extraDataWindowLogicalLast[index] == logicalLast &&
                _extraDataWindowPhysicalFirst[index] == physicalFirst)
            {
                return true;
            }
        }

        return false;
    }

    void EnsureExtraDataAddressWindowCapacity()
    {
        if (_extraDataWindowCount == _extraDataWindowLogicalFirst.Length)
        {
            var newLength = _extraDataWindowCount * 2;
            Array.Resize(ref _extraDataWindowLogicalFirst, newLength);
            Array.Resize(ref _extraDataWindowLogicalLast, newLength);
            Array.Resize(ref _extraDataWindowPhysicalFirst, newLength);
        }
    }

    void AppendExtraDataAddressWindow(
        int logicalFirst,
        int logicalLast,
        int physicalFirst)
    {
        _extraDataWindowLogicalFirst[_extraDataWindowCount] = logicalFirst;
        _extraDataWindowLogicalLast[_extraDataWindowCount] = logicalLast;
        _extraDataWindowPhysicalFirst[_extraDataWindowCount] = physicalFirst;
        _extraDataWindowCount++;
    }

    public void ClearDataAddressWindow()
    {
        _dataWindowLogicalFirst = 1;
        _dataWindowLogicalLast = 0;
        _dataWindowPhysicalFirst = 0;
        _extraDataWindowCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int TranslateDataAddress(int address)
    {
        if (_dataWindowLogicalFirst <= _dataWindowLogicalLast &&
            ContainsAddress(address, _dataWindowLogicalFirst, _dataWindowLogicalLast))
        {
            return _dataWindowPhysicalFirst + address - _dataWindowLogicalFirst;
        }
        for (var index = 0; index < _extraDataWindowCount; index++)
        {
            var logicalFirst = _extraDataWindowLogicalFirst[index];
            var logicalLast = _extraDataWindowLogicalLast[index];
            if (ContainsAddress(address, logicalFirst, logicalLast))
            {
                return _extraDataWindowPhysicalFirst[index] + address - logicalFirst;
            }
        }
        return address;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int UntranslateDataAddress(int address)
    {
        if (_dataWindowLogicalFirst > _dataWindowLogicalLast)
        {
            return address;
        }

        var physicalLast =
            _dataWindowPhysicalFirst + _dataWindowLogicalLast - _dataWindowLogicalFirst;
        if (ContainsAddress(address, _dataWindowPhysicalFirst, physicalLast))
        {
            return _dataWindowLogicalFirst + address - _dataWindowPhysicalFirst;
        }
        for (var index = 0; index < _extraDataWindowCount; index++)
        {
            var physicalFirst = _extraDataWindowPhysicalFirst[index];
            var extraPhysicalLast = physicalFirst +
                _extraDataWindowLogicalLast[index] -
                _extraDataWindowLogicalFirst[index];
            if (ContainsAddress(address, physicalFirst, extraPhysicalLast))
            {
                return _extraDataWindowLogicalFirst[index] + address - physicalFirst;
            }
        }
        return address;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ContainsAddress(int address, int first, int last) =>
        (uint)(address - first) <= (uint)(last - first);

    public void Reset()
    {
        ClearDataAddressWindow();
        SP = Data.Length - 1;
        PC = 0;
        Array.Clear(_pendingInterrupts, 0, _pendingInterrupts.Length);
        NextInterrupt = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadData(int addr)
    {
        byte value;
        if (addr >= 32 && GetReadHook(addr) is { } hook)
        {
            value = hook(addr);
        }
        else
        {
            value = Data[addr];
        }
        DataReadObserver?.Invoke(addr, value);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteData(int addr, byte value, byte mask = 0xff)
    {
        var hook = GetWriteHook(addr);
        var observer = DataWriteObserver;
        if (hook is null && observer is null && mask == 0xff)
        {
            CommitDataWrite(addr, value);
            return;
        }

        var oldValue = (uint)addr < (uint)Data.Length ? Data[addr] : (byte)0;
        if (hook?.Invoke(value, oldValue, addr, mask) == true)
        {
            observer?.Invoke(
                addr,
                oldValue,
                value,
                mask,
                hookConsumed: true);
            return;
        }

        byte effectiveValue = (byte)((oldValue & ~mask) | (value & mask));
        CommitDataWrite(addr, effectiveValue);
        observer?.Invoke(
            addr,
            oldValue,
            value,
            mask,
            hookConsumed: false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void CommitDataWrite(int address, byte value)
    {
        if (address == StatusRegister)
        {
            SetStatusRegister(value);
        }
        else
        {
            Data[address] = value;
        }
    }

    internal void AddReadHookRange(
        int firstAddress,
        int endAddressExclusive,
        CpuMemoryReadHook hook)
    {
        ValidateHookRange(firstAddress, endAddressExclusive);
        ArgumentNullException.ThrowIfNull(hook);
        _readHookRanges.Add(new(firstAddress, endAddressExclusive, hook));
    }

    internal void AddWriteHookRange(
        int firstAddress,
        int endAddressExclusive,
        CpuMemoryWriteHook hook)
    {
        ValidateHookRange(firstAddress, endAddressExclusive);
        ArgumentNullException.ThrowIfNull(hook);
        _writeHookRanges.Add(new(firstAddress, endAddressExclusive, hook));
    }

    internal bool HasReadHook(int address) => GetReadHook(address) is not null;

    CpuMemoryReadHook? GetReadHook(int address)
    {
        var direct = ReadHooks[address];
        if (direct is not null)
        {
            return direct;
        }
        for (var index = _readHookRanges.Count - 1; index >= 0; index--)
        {
            var range = _readHookRanges[index];
            if (range.Contains(address))
            {
                return range.Hook;
            }
        }
        return null;
    }

    CpuMemoryWriteHook? GetWriteHook(int address)
    {
        var direct = WriteHooks[address];
        if (direct is not null)
        {
            return direct;
        }
        for (var index = _writeHookRanges.Count - 1; index >= 0; index--)
        {
            var range = _writeHookRanges[index];
            if (range.Contains(address))
            {
                return range.Hook;
            }
        }
        return null;
    }

    void ValidateHookRange(int firstAddress, int endAddressExclusive)
    {
        if (firstAddress < 0 || endAddressExclusive <= firstAddress ||
            endAddressExclusive > DataAddressSpaceSize)
        {
            throw new ArgumentOutOfRangeException(nameof(firstAddress));
        }
    }

    readonly record struct CpuMemoryReadHookRange(
        int FirstAddress,
        int EndAddressExclusive,
        CpuMemoryReadHook Hook)
    {
        public bool Contains(int address) =>
            (uint)(address - FirstAddress) <
            (uint)(EndAddressExclusive - FirstAddress);
    }

    readonly record struct CpuMemoryWriteHookRange(
        int FirstAddress,
        int EndAddressExclusive,
        CpuMemoryWriteHook Hook)
    {
        public bool Contains(int address) =>
            (uint)(address - FirstAddress) <
            (uint)(EndAddressExclusive - FirstAddress);
    }

    public int SP
    {
        get => GetUint16(StackPointerRegister);
        set => SetUint16(StackPointerRegister, value);
    }

    public byte SREG => Data[StatusRegister];

    public bool InterruptsEnabled => (Data[StatusRegister] & 0x80) != 0;

    /// <summary>
    /// Whether a device has deferred work until the next interrupt sample.
    /// Machine-level execution accelerators must remain disabled while this is
    /// true so the callback observes the same instruction boundary.
    /// </summary>
    public bool HasScheduledAfterTick => _afterTick is not null;

    public void SetStatusRegister(byte value)
    {
        var wasEnabled = InterruptsEnabled;
        Data[StatusRegister] = value;
        var isEnabled = (value & 0x80) != 0;
        if (wasEnabled != isEnabled)
        {
            GlobalInterruptEnableChanged?.Invoke(
                this,
                new CpuGlobalInterruptEnableChangedEventArgs(isEnabled));
        }
    }

    /// <summary>
    /// Schedules an effect after the current interrupt sample. Work scheduled
    /// by that effect is therefore first eligible at the following tick.
    /// </summary>
    public void ScheduleAfterTick(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _afterTick += callback;
    }

    public void SetInterruptFlag(AvrInterruptConfig interrupt)
    {
        ArgumentNullException.ThrowIfNull(interrupt);
        if (interrupt.InverseFlag)
        {
            Data[interrupt.FlagRegister] &= (byte)~interrupt.FlagMask;
        }
        else
        {
            Data[interrupt.FlagRegister] |= interrupt.FlagMask;
        }
        if ((Data[interrupt.EnableRegister] & interrupt.EnableMask) != 0)
        {
            QueueInterrupt(interrupt);
        }
    }

    public void UpdateInterruptEnable(AvrInterruptConfig interrupt, byte registerValue)
    {
        ArgumentNullException.ThrowIfNull(interrupt);
        if ((registerValue & interrupt.EnableMask) == 0)
        {
            ClearInterrupt(interrupt, false);
            return;
        }

        bool bitSet = (Data[interrupt.FlagRegister] & interrupt.FlagMask) != 0;
        if (interrupt.InverseFlag ? !bitSet : bitSet)
        {
            QueueInterrupt(interrupt);
        }
    }

    public void QueueInterrupt(AvrInterruptConfig interrupt)
    {
        ArgumentNullException.ThrowIfNull(interrupt);
        var address = interrupt.Address;
        _pendingInterrupts[address] = interrupt;
        if (NextInterrupt == -1 || NextInterrupt > address)
        {
            NextInterrupt = address;
        }
        if (address > MaxInterrupt)
        {
            MaxInterrupt = address;
        }
    }

    public void ClearInterrupt(AvrInterruptConfig interrupt, bool clearFlag = true)
    {
        ArgumentNullException.ThrowIfNull(interrupt);
        if (clearFlag)
        {
            Data[interrupt.FlagRegister] &= (byte)~interrupt.FlagMask;
        }
        var address = interrupt.Address;
        if (_pendingInterrupts[address] == null)
        {
            return;
        }
        _pendingInterrupts[address] = null;
        if (NextInterrupt == address)
        {
            NextInterrupt = FindNextPendingInterrupt(address + 1);
        }
    }

    int FindNextPendingInterrupt(int firstAddress)
    {
        for (var address = firstAddress; address <= MaxInterrupt; address++)
        {
            if (_pendingInterrupts[address] is not null)
            {
                return address;
            }
        }

        return -1;
    }

    public void ClearInterruptByFlag(AvrInterruptConfig interrupt, byte registerValue)
    {
        ArgumentNullException.ThrowIfNull(interrupt);
        if ((registerValue & interrupt.FlagMask) != 0)
        {
            Data[interrupt.FlagRegister] &= (byte)~interrupt.FlagMask;
            ClearInterrupt(interrupt);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Tick()
    {
        var nextInterrupt = NextInterrupt;
        var afterTick = _afterTick;
        if ((nextInterrupt < 0 || (Data[StatusRegister] & 0x80) == 0) &&
            afterTick is null)
        {
            return -1;
        }
        return TickSlow(nextInterrupt, afterTick);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    int TickSlow(int nextInterrupt, Action? afterTick)
    {
        int dispatchedInterrupt = DispatchPendingInterrupt(nextInterrupt);
        _afterTick = null;
        afterTick?.Invoke();
        return dispatchedInterrupt;
    }

    int DispatchPendingInterrupt(int nextInterrupt)
    {
        if (!InterruptsEnabled || nextInterrupt < 0)
        {
            return -1;
        }

        var interrupt = _pendingInterrupts[nextInterrupt]!;
        AvrInterrupt.Execute(this, interrupt.Address);
        if (!interrupt.Constant)
        {
            ClearInterrupt(interrupt);
        }
        return interrupt.Address;
    }
}
