// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Models the NOR flash's data-space aliases and Intel-style command set.
/// Application flash byte N is visible at data address 0x800000 + N until the
/// external-RAM aperture at 0xd60000; GDFS resumes the flash alias at 0xd80000.
/// Firmware executes its flash driver from RAM while the NOR is outside
/// read-array mode.
/// </summary>
internal sealed class AsicFlashMemory
{
    public const int CommandBase = 0x800000;
    public const int ApplicationAliasLength = 0x560000;
    public const int ExternalDataRamBase = CommandBase + ApplicationAliasLength;
    public const int PhysicalGdfsBase = 0x580000;
    public const int LogicalGdfsBase = CommandBase + PhysicalGdfsBase;
    public const int UserProtectionRegisterLength = 8;

    const byte IntelManufacturerId = 0x89;
    const ushort Intel64MbitDeviceId = 0x886c;
    const int UserProtectionRegisterOffset = 0x10a;

    readonly Cpu cpu;
    readonly byte[] userProtectionRegister;
    readonly MiaWorker? worker;
    readonly List<AsicFlashMemoryPendingWord> pendingWords = new(8);
    readonly ConcurrentDictionary<int, AsicFlashMemoryPendingProgramWord> visibleProgramWords = new();
    // The byte-pair latch belongs to the AVR-facing bus adapter. The NOR owner
    // only receives complete 16-bit command words, matching the physical bus
    // transaction while avoiding two host roundtrips per word.
    int pendingWordAddress = -1;
    byte pendingLowByte;
    int visibleMode;
    int pendingOwnerTransactions;
    int pendingEraseTransactions;
    long nextProgramVersion;
    bool programPending;
    bool lockPending;
    bool erasePending;
    bool programPendingOnBus;
    bool lockPendingOnBus;
    bool erasePendingOnBus;

    public AsicFlashMemory(
        Cpu cpu,
        ReadOnlySpan<byte> userProtectionRegister = default,
        MiaWorker? worker = null)
    {
        if (userProtectionRegister.Length is not (0 or UserProtectionRegisterLength))
        {
            throw new ArgumentException(
                $"The flash user protection register must be exactly {UserProtectionRegisterLength} bytes.",
                nameof(userProtectionRegister));
        }

        this.cpu = cpu;
        this.worker = worker;
        this.userProtectionRegister = new byte[UserProtectionRegisterLength];
        Array.Fill(this.userProtectionRegister, (byte)0xff);
        userProtectionRegister.CopyTo(this.userProtectionRegister);
        if (cpu.ProgBytes.Length < PhysicalGdfsBase ||
            cpu.DataAddressSpaceSize < CommandBase + cpu.ProgBytes.Length)
        {
            throw new ArgumentException("CPU data memory cannot contain the logical flash window.", nameof(cpu));
        }

        CpuMemoryReadHook read = Read;
        CpuMemoryWriteHook write = Write;
        // The ASIC maps application NOR into data space until the D6/D7
        // external-RAM aperture. Keep the two NOR windows as ranges rather
        // than allocating one hook slot for every mapped byte.
        cpu.AddReadHookRange(
            CommandBase,
            CommandBase + Math.Min(ApplicationAliasLength, cpu.ProgBytes.Length),
            read);
        cpu.AddWriteHookRange(
            CommandBase,
            CommandBase + Math.Min(ApplicationAliasLength, cpu.ProgBytes.Length),
            write);
        cpu.AddReadHookRange(
            LogicalGdfsBase,
            CommandBase + cpu.ProgBytes.Length,
            read);
        cpu.AddWriteHookRange(
            LogicalGdfsBase,
            CommandBase + cpu.ProgBytes.Length,
            write);
    }

    public long ReadCount { get; private set; }
    public long WriteCount { get; private set; }
    public long ReadArrayCommandCount { get; private set; }
    public long IdentifierCommandCount { get; private set; }
    public long ClearStatusCommandCount { get; private set; }
    public long ProgramSetupCommandCount { get; private set; }
    public long ProgramWordCount { get; private set; }
    public long LockSetupCommandCount { get; private set; }
    public long LockConfirmCount { get; private set; }
    public long EraseSetupCommandCount { get; private set; }
    public long EraseConfirmCount { get; private set; }
    public long OtherWordCount { get; private set; }

    byte Read(int address)
    {
        ReadCount++;
        var physical = address - CommandBase;
        var mode = (AsicFlashMemoryMode)Volatile.Read(ref visibleMode);
        if (mode == AsicFlashMemoryMode.Identifiers)
        {
            return ReadIdentifier(physical);
        }
        if (mode == AsicFlashMemoryMode.Status)
        {
            return (physical & 1) == 0 ? (byte)0x80 : (byte)0;
        }

        return ReadArray(physical);
    }

    byte ReadIdentifier(int physical)
    {
        if (physical >= UserProtectionRegisterOffset &&
            physical < UserProtectionRegisterOffset + userProtectionRegister.Length)
        {
            return userProtectionRegister[physical - UserProtectionRegisterOffset];
        }

        return physical switch
        {
            0 => IntelManufacturerId,
            1 => 0,
            2 => (byte)(Intel64MbitDeviceId & 0xff),
            3 => (byte)(Intel64MbitDeviceId >> 8),
            _ => 0xff,
        };
    }

    byte ReadArray(int physical)
    {
        if (Volatile.Read(ref pendingEraseTransactions) != 0)
        {
            Synchronize();
        }
        if (visibleProgramWords.TryGetValue(
                physical & ~1,
                out var pendingProgram))
        {
            return (physical & 1) == 0
                ? (byte)pendingProgram.Word
                : (byte)(pendingProgram.Word >> 8);
        }
        return cpu.ProgBytes[physical];
    }

    bool Write(byte value, byte oldValue, int address, byte mask)
    {
        WriteCount++;
        if (mask != 0xff)
        {
            return true;
        }

        if ((address & 1) == 0)
        {
            pendingWordAddress = address;
            pendingLowByte = value;
            return true;
        }
        if (pendingWordAddress != address - 1)
        {
            return true;
        }

        var wordAddress = pendingWordAddress;
        pendingWordAddress = -1;
        var word = (ushort)(pendingLowByte | value << 8);
        QueueWord(wordAddress, word);
        return true;
    }

    void QueueWord(int wordAddress, ushort word)
    {
        var completesTransaction = programPendingOnBus ||
            lockPendingOnBus || erasePendingOnBus;
        var completesProgram = programPendingOnBus;
        var completesErase = erasePendingOnBus;

        UpdateVisibleMode(word, completesTransaction);
        var programVersion = StageVisibleTransaction(
            wordAddress,
            word,
            completesProgram,
            completesErase);
        pendingWords.Add(new(
            wordAddress,
            word,
            programVersion,
            completesErase));
        if (completesTransaction)
        {
            CompleteBusTransaction();
            return;
        }

        if (!BeginBusTransaction(word))
        {
            return;
        }
        if (pendingWords.Count == pendingWords.Capacity)
        {
            PostPendingWords();
        }
    }

    void UpdateVisibleMode(ushort word, bool completesTransaction)
    {
        switch (word)
        {
            case 0x0090:
                Volatile.Write(ref visibleMode, (int)AsicFlashMemoryMode.Identifiers);
                break;
            case 0x00ff:
                Volatile.Write(ref visibleMode, (int)AsicFlashMemoryMode.ReadArray);
                break;
            case 0x0050:
                Volatile.Write(ref visibleMode, (int)AsicFlashMemoryMode.Status);
                break;
        }
        if (completesTransaction)
        {
            Volatile.Write(ref visibleMode, (int)AsicFlashMemoryMode.Status);
        }
    }

    long StageVisibleTransaction(
        int wordAddress,
        ushort word,
        bool completesProgram,
        bool completesErase)
    {
        long programVersion = 0;
        if (completesProgram)
        {
            programVersion = ++nextProgramVersion;
            visibleProgramWords[wordAddress - CommandBase] = new(
                word,
                programVersion);
        }
        if (completesErase)
        {
            Interlocked.Increment(ref pendingEraseTransactions);
        }
        return programVersion;
    }

    void CompleteBusTransaction()
    {
        programPendingOnBus = false;
        lockPendingOnBus = false;
        erasePendingOnBus = false;
        PostPendingWords();
    }

    bool BeginBusTransaction(ushort word)
    {
        var commandAccepted = true;
        switch (word)
        {
            case 0x0040:
                programPendingOnBus = true;
                break;
            case 0x0060:
                lockPendingOnBus = true;
                break;
            case 0x0020:
                erasePendingOnBus = true;
                break;
            case 0x0090:
            case 0x00ff:
            case 0x0050:
                // These commands only become observable through a later NOR
                // read or a terminal command sequence, so they can travel in
                // the same owner message without changing firmware timing.
                break;
            default:
                Synchronize();
                commandAccepted = false;
                break;
        }
        return commandAccepted;
    }

    void PostPendingWords()
    {
        var words = TakePendingWords();
        if (words is null)
        {
            return;
        }
        DispatchPendingWords(words);
    }

    void DispatchPendingWords(AsicFlashMemoryPendingWord[] words)
    {
        if (worker is not null && !worker.IsCurrentThread)
        {
            PostPendingWordsToOwner(words);
            return;
        }

        ApplyPendingWords(words);
    }

    void PostPendingWordsToOwner(AsicFlashMemoryPendingWord[] words)
    {
        Interlocked.Increment(ref pendingOwnerTransactions);
        worker!.Post(() =>
        {
            try
            {
                ApplyPendingWords(words);
            }
            finally
            {
                Interlocked.Decrement(ref pendingOwnerTransactions);
            }
        });
    }

    internal void Synchronize()
    {
        var words = TakePendingWords();
        if (worker is null || worker.IsCurrentThread)
        {
            ApplyPendingWordsIfPresent(words);
            return;
        }

        SynchronizeWithOwner(words);
    }

    void ApplyPendingWordsIfPresent(AsicFlashMemoryPendingWord[]? words)
    {
        if (words is not null)
        {
            ApplyPendingWords(words);
        }
    }

    void SynchronizeWithOwner(AsicFlashMemoryPendingWord[]? words)
    {
        if (words is not null)
        {
            worker!.Invoke(() => ApplyPendingWords(words));
            return;
        }

        if (Volatile.Read(ref pendingOwnerTransactions) != 0)
        {
            worker!.Invoke(() => { });
        }
    }

    AsicFlashMemoryPendingWord[]? TakePendingWords()
    {
        if (pendingWords.Count == 0)
        {
            return null;
        }
        var words = pendingWords.ToArray();
        pendingWords.Clear();
        return words;
    }

    void ApplyPendingWords(AsicFlashMemoryPendingWord[] words)
    {
        foreach (var pendingWord in words)
        {
            try
            {
                ApplyWord(pendingWord);
            }
            finally
            {
                if (pendingWord.CompletesErase)
                {
                    Interlocked.Decrement(ref pendingEraseTransactions);
                }
            }
        }
    }

    void ApplyWord(AsicFlashMemoryPendingWord pendingWord)
    {
        if (TryApplyProgramWord(pendingWord))
        {
            return;
        }
        if (TryApplyLockConfirmation())
        {
            return;
        }
        if (TryApplyEraseConfirmation(pendingWord))
        {
            return;
        }

        ApplyCommand(pendingWord.Word);
    }

    bool TryApplyProgramWord(AsicFlashMemoryPendingWord pendingWord)
    {
        if (!programPending)
        {
            return false;
        }

        ProgramWordCount++;
        var physical = pendingWord.Address - CommandBase;
        cpu.ProgBytes[physical] = (byte)pendingWord.Word;
        cpu.ProgBytes[physical + 1] = (byte)(pendingWord.Word >> 8);
        RemoveVisibleProgramWord(physical, pendingWord.ProgramVersion);
        programPending = false;
        return true;
    }

    void RemoveVisibleProgramWord(int physical, long programVersion)
    {
        if (!visibleProgramWords.TryGetValue(physical, out var visibleProgram) ||
            visibleProgram.Version != programVersion)
        {
            return;
        }

        // Atomic compare-and-remove: only clears the entry this word itself
        // staged, not a newer one that superseded it at the same address.
        visibleProgramWords.TryRemove(
            new KeyValuePair<int, AsicFlashMemoryPendingProgramWord>(
                physical,
                visibleProgram));
    }

    bool TryApplyLockConfirmation()
    {
        if (!lockPending)
        {
            return false;
        }

        LockConfirmCount++;
        lockPending = false;
        return true;
    }

    bool TryApplyEraseConfirmation(AsicFlashMemoryPendingWord pendingWord)
    {
        if (!erasePending)
        {
            return false;
        }

        EraseConfirmCount++;
        erasePending = false;
        EraseBlockIfConfirmed(pendingWord);
        return true;
    }

    void EraseBlockIfConfirmed(AsicFlashMemoryPendingWord pendingWord)
    {
        if (pendingWord.Word != 0x00d0)
        {
            return;
        }

        const int MainBlockLength = 0x10000;
        var physical = pendingWord.Address - CommandBase;
        var blockStart = physical & -MainBlockLength;
        Array.Fill(cpu.ProgBytes, (byte)0xff, blockStart,
            Math.Min(MainBlockLength, cpu.ProgBytes.Length - blockStart));
    }

    void ApplyCommand(ushort word)
    {
        switch (word)
        {
            case 0x0090:
                IdentifierCommandCount++;
                break;
            case 0x00ff:
                ReadArrayCommandCount++;
                break;
            case 0x0050:
                ClearStatusCommandCount++;
                break;
            case 0x0040:
                ProgramSetupCommandCount++;
                programPending = true;
                break;
            case 0x0060:
                LockSetupCommandCount++;
                lockPending = true;
                break;
            case 0x0020:
                EraseSetupCommandCount++;
                erasePending = true;
                break;
            default:
                OtherWordCount++;
                break;
        }
    }
}
