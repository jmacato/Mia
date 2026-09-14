// SPDX-License-Identifier: MIT
//
// C# port of SkyEmu src/arm7.h.
// Copyright (c) 2021 Skyler "Sky" Saleh.

namespace Arm7Core;

public sealed partial class Arm7Tdmi
{
    void ExecuteArmSwap(uint instruction)
    {
        bool byteTransfer = Bits(instruction, 22, 1) != 0;
        int rn = (int)Bits(instruction, 16, 4);
        int rd = (int)Bits(instruction, 12, 4);
        int rm = (int)Bits(instruction, 0, 4);
        uint address = ReadRegister(rn) + (rn == ProgramCounter ? 4u : 0u);

        BreakSequentialFetch();
        uint value;
        if (byteTransfer)
        {
            value = ReadByte(address);
            WriteByte(address, (byte)ReadStoreRegister(rm), ArmAccess.Lock);
        }
        else
        {
            value = ReadRotatedWord(address);
            WriteWord(address, ReadStoreRegister(rm), ArmAccess.Lock);
        }
        Idle();
        WriteRegister(rd, value);
    }

    void ExecuteArmBranchExchange(uint instruction)
    {
        uint target = ReadRegister((int)Bits(instruction, 0, 4));
        IsThumb = (target & 1u) != 0;
        BranchTo(IsThumb ? target & ~1u : target);
    }

    void ExecuteArmHalfwordTransfer(uint instruction)
    {
        ArmHalfwordTransfer transfer = DecodeArmHalfwordTransfer(instruction);
        uint baseAddress = ReadRegister(transfer.BaseRegister);
        uint indexedAddress = transfer.AddOffset
            ? unchecked(baseAddress + transfer.Offset)
            : unchecked(baseAddress - transfer.Offset);
        uint address = transfer.PreIndex ? indexedAddress : baseAddress;
        BreakSequentialFetch();
        if (transfer.Load)
        {
            LoadArmHalfword(transfer, address, indexedAddress);
        }
        else
        {
            StoreArmHalfword(transfer, address, indexedAddress);
        }
    }

    ArmHalfwordTransfer DecodeArmHalfwordTransfer(uint instruction)
    {
        bool preIndex = Bits(instruction, 24, 1) != 0;
        bool immediate = Bits(instruction, 22, 1) != 0;
        return new()
        {
            PreIndex = preIndex,
            AddOffset = Bits(instruction, 23, 1) != 0,
            WriteBack = Bits(instruction, 21, 1) != 0 || !preIndex,
            Load = Bits(instruction, 20, 1) != 0,
            BaseRegister = (int)Bits(instruction, 16, 4),
            DestinationRegister = (int)Bits(instruction, 12, 4),
            Signed = Bits(instruction, 6, 1) != 0,
            Halfword = Bits(instruction, 5, 1) != 0,
            Offset = immediate
                ? (Bits(instruction, 8, 4) << 4) | Bits(instruction, 0, 4)
                : ReadRegister((int)Bits(instruction, 0, 4)),
        };
    }

    void LoadArmHalfword(
        ArmHalfwordTransfer transfer,
        uint address,
        uint indexedAddress)
    {
        WriteBackArmHalfword(transfer, indexedAddress);
        uint data = ReadArmHalfword(transfer, address);
        Idle();
        WriteRegister(transfer.DestinationRegister, data);
    }

    void StoreArmHalfword(
        ArmHalfwordTransfer transfer,
        uint address,
        uint indexedAddress)
    {
        uint value = ReadStoreRegister(transfer.DestinationRegister);
        if (transfer.Halfword)
        {
            WriteHalf(address, (ushort)value);
        }
        else
        {
            WriteByte(address, (byte)value);
        }
        WriteBackArmHalfword(transfer, indexedAddress);
    }

    void WriteBackArmHalfword(
        ArmHalfwordTransfer transfer,
        uint indexedAddress)
    {
        if (!transfer.WriteBack)
        {
            return;
        }

        uint pcOffset = transfer.BaseRegister == ProgramCounter ? 4u : 0u;
        WriteRegister(transfer.BaseRegister, indexedAddress + pcOffset);
    }

    uint ReadArmHalfword(ArmHalfwordTransfer transfer, uint address)
    {
        if (!transfer.Halfword)
        {
            uint data = ReadByte(address);
            return transfer.Signed ? ExtendSignBit(data, 0x80u) : data;
        }

        uint raw = ReadHalf(address);
        bool unaligned = (address & 1u) != 0;
        if (!transfer.Signed)
        {
            return unaligned ? RotateRight(raw, 8) : raw;
        }

        return unaligned
            ? ExtendSignBit((raw >> 8) & 0xffu, 0x80u)
            : ExtendSignBit(raw, 0x8000u);
    }

    void ExecuteArmSingleTransfer(uint instruction)
    {
        ArmSingleTransfer transfer = DecodeArmSingleTransfer(instruction);
        uint baseAddress = ReadRegister(transfer.BaseRegister);
        uint indexedAddress = transfer.AddOffset
            ? unchecked(baseAddress + transfer.Offset)
            : unchecked(baseAddress - transfer.Offset);
        uint address = transfer.PreIndex ? indexedAddress : baseAddress;
        BreakSequentialFetch();
        if (transfer.Load)
        {
            LoadArmSingle(transfer, address, indexedAddress);
        }
        else
        {
            StoreArmSingle(transfer, address, indexedAddress);
        }
    }

    ArmSingleTransfer DecodeArmSingleTransfer(uint instruction)
    {
        bool preIndex = Bits(instruction, 24, 1) != 0;
        return new()
        {
            PreIndex = preIndex,
            AddOffset = Bits(instruction, 23, 1) != 0,
            ByteTransfer = Bits(instruction, 22, 1) != 0,
            WriteBack = Bits(instruction, 21, 1) != 0 || !preIndex,
            Load = Bits(instruction, 20, 1) != 0,
            BaseRegister = (int)Bits(instruction, 16, 4),
            DestinationRegister = (int)Bits(instruction, 12, 4),
            Offset = ReadArmSingleTransferOffset(instruction),
        };
    }

    uint ReadArmSingleTransferOffset(uint instruction)
    {
        if (Bits(instruction, 25, 1) == 0)
        {
            return Bits(instruction, 0, 12);
        }

        uint value = ReadRegister((int)Bits(instruction, 0, 4));
        return Shift(new(
            value,
            (int)Bits(instruction, 5, 2),
            Bits(instruction, 7, 5),
            RegisterSpecified: false)).Value;
    }

    void LoadArmSingle(
        ArmSingleTransfer transfer,
        uint address,
        uint indexedAddress)
    {
        WriteBackArmSingle(transfer, indexedAddress);
        uint data = transfer.ByteTransfer
            ? ReadByte(address)
            : ReadRotatedWord(address);
        Idle();
        WriteRegister(transfer.DestinationRegister, data);
    }

    void StoreArmSingle(
        ArmSingleTransfer transfer,
        uint address,
        uint indexedAddress)
    {
        uint value = ReadStoreRegister(transfer.DestinationRegister);
        if (transfer.ByteTransfer)
        {
            WriteByte(address, (byte)value);
        }
        else
        {
            WriteWord(address, value);
        }
        WriteBackArmSingle(transfer, indexedAddress);
    }

    void WriteBackArmSingle(ArmSingleTransfer transfer, uint indexedAddress)
    {
        if (!transfer.WriteBack)
        {
            return;
        }

        uint pcOffset = transfer.BaseRegister == ProgramCounter ? 4u : 0u;
        WriteRegister(transfer.BaseRegister, indexedAddress + pcOffset);
    }

    void ExecuteArmBlockTransfer(uint instruction)
    {
        ArmBlockTransfer transfer = DecodeArmBlockTransfer(instruction);
        uint baseAddress = ReadRegister(transfer.BaseRegister);
        uint byteCount = (uint)transfer.TransferCount * 4u;
        uint finalAddress = transfer.Increment
            ? unchecked(baseAddress + byteCount)
            : unchecked(baseAddress - byteCount);
        uint startAddress = GetArmBlockStartAddress(
            transfer,
            baseAddress,
            byteCount);
        var state = new ArmBlockTransferState(
            startAddress,
            ArmAccess.None,
            FirstTransfer: true);
        BreakSequentialFetch();

        foreach (int register in EnumerateRegisters(transfer.RegisterList))
        {
            state = TransferArmBlockRegister(
                transfer,
                state,
                register,
                finalAddress);
        }

        FinishArmBlockTransfer(transfer);
    }

    static ArmBlockTransfer DecodeArmBlockTransfer(uint instruction)
    {
        bool load = Bits(instruction, 20, 1) != 0;
        bool userOrRestore = Bits(instruction, 22, 1) != 0;
        uint encodedList = Bits(instruction, 0, 16);
        bool emptyList = encodedList == 0;
        uint registerList = emptyList ? 1u << ProgramCounter : encodedList;
        bool pcInList = (registerList & (1u << ProgramCounter)) != 0;
        return new()
        {
            PreIndex = Bits(instruction, 24, 1) != 0,
            Increment = Bits(instruction, 23, 1) != 0,
            UserOrRestore = userOrRestore,
            WriteBack = Bits(instruction, 21, 1) != 0,
            Load = load,
            BaseRegister = (int)Bits(instruction, 16, 4),
            RegisterList = registerList,
            EmptyRegisterList = emptyList,
            ProgramCounterInList = pcInList,
            UseUserBank = userOrRestore && (!load || !pcInList),
            TransferCount = emptyList
                ? 16
                : System.Numerics.BitOperations.PopCount(registerList),
        };
    }

    static uint GetArmBlockStartAddress(
        ArmBlockTransfer transfer,
        uint baseAddress,
        uint byteCount) => (transfer.Increment, transfer.PreIndex) switch
        {
            (true, true) => baseAddress + 4u,
            (true, false) => baseAddress,
            (false, true) => baseAddress - byteCount,
            _ => baseAddress - byteCount + 4u,
        };

    static IEnumerable<int> EnumerateRegisters(uint registerList)
    {
        while (registerList != 0)
        {
            int register = System.Numerics.BitOperations.TrailingZeroCount(
                registerList);
            yield return register;
            registerList &= registerList - 1;
        }
    }

    ArmBlockTransferState TransferArmBlockRegister(
        ArmBlockTransfer transfer,
        ArmBlockTransferState state,
        int register,
        uint finalAddress)
    {
        if (!transfer.Load)
        {
            StoreArmBlockRegister(transfer, state, register);
        }
        if (state.FirstTransfer && transfer.WriteBack)
        {
            WriteBackArmBlock(transfer, finalAddress);
        }
        if (transfer.Load)
        {
            LoadArmBlockRegister(transfer, state, register);
        }

        return new(
            state.Address + 4u,
            ArmAccess.Sequential,
            FirstTransfer: false);
    }

    void StoreArmBlockRegister(
        ArmBlockTransfer transfer,
        ArmBlockTransferState state,
        int register)
    {
        uint value = transfer.UseUserBank
            ? ReadUserRegister(register)
            : ReadRegister(register);
        if (register == ProgramCounter)
        {
            value += IsThumb
                ? 2u
                : transfer.WriteBack && transfer.BaseRegister == ProgramCounter
                    ? 0u
                    : 4u;
        }
        WriteWord(state.Address, value, state.Access);
    }

    void WriteBackArmBlock(ArmBlockTransfer transfer, uint finalAddress)
    {
        if (transfer.UseUserBank)
        {
            WriteUserRegister(transfer.BaseRegister, finalAddress);
        }
        else
        {
            WriteRegister(transfer.BaseRegister, finalAddress);
        }
    }

    void LoadArmBlockRegister(
        ArmBlockTransfer transfer,
        ArmBlockTransferState state,
        int register)
    {
        uint value = ReadWord(state.Address, state.Access);
        if (transfer.UseUserBank)
        {
            WriteUserRegister(register, value);
            return;
        }
        if (transfer.EmptyRegisterList && register == ProgramCounter)
        {
            BranchTo(value);
            return;
        }
        WriteRegister(register, value);
    }

    void FinishArmBlockTransfer(ArmBlockTransfer transfer)
    {
        if (!transfer.Load)
        {
            return;
        }

        Idle();
        if (transfer.UserOrRestore && transfer.ProgramCounterInList)
        {
            _registers[Cpsr] = ReadCurrentSpsr();
            LatchInterruptDisable();
        }
    }

    void ExecuteArmBranch(uint instruction)
    {
        if (Bits(instruction, 24, 1) != 0)
        {
            WriteRegister(14, _registers[ProgramCounter] - 4u);
        }
        int offset = SignExtend(Bits(instruction, 0, 24), 24) << 2;
        BranchTo(unchecked(_registers[ProgramCounter] + (uint)offset));
    }

    void ExecuteArmUndefined(uint instruction)
    {
        ObserveUndefined(instruction);
        EnterException(ModeUnd, 0x04, ArmBank.Und);
        Idle();
    }

    void ExecuteSoftwareInterrupt(uint instruction)
    {
        EnterException(ModeSvc, 0x08, ArmBank.Svc);
    }

    void ExecuteArmMrs(uint instruction)
    {
        bool spsr = Bits(instruction, 22, 1) != 0;
        int rd = (int)Bits(instruction, 12, 4);
        // MRS with Rd=r15 is architecturally unpredictable. ARM7TDMI writes
        // the register file entry without taking the normal PC-write path.
        _registers[RegisterIndex(rd)] = spsr ? ReadCurrentSpsr() : _registers[Cpsr];
    }

    void ExecuteArmMsr(uint instruction)
    {
        bool spsr = Bits(instruction, 22, 1) != 0;
        uint mode = _registers[Cpsr] & 0x1fu;
        if (spsr && mode is ModeUsr or ModeSys)
        {
            return;
        }

        uint mask = GetArmPsrMask(instruction, spsr, mode);
        uint value = Bits(instruction, 25, 1) != 0
            ? RotateRight(
                Bits(instruction, 0, 8),
                (int)Bits(instruction, 8, 4) * 2)
            : ReadRegister((int)Bits(instruction, 0, 4));
        WriteArmPsr(spsr, mask, value);
    }

    static uint GetArmPsrMask(uint instruction, bool spsr, uint mode)
    {
        uint mask = (Bits(instruction, 19, 1) != 0 ? 0xff000000u : 0u) |
            (Bits(instruction, 18, 1) != 0 ? 0x00ff0000u : 0u) |
            (Bits(instruction, 17, 1) != 0 ? 0x0000ff00u : 0u) |
            (Bits(instruction, 16, 1) != 0 ? 0x000000ffu : 0u);
        return !spsr && mode == ModeUsr ? mask & 0xff000000u : mask;
    }

    void WriteArmPsr(bool spsr, uint mask, uint value)
    {
        if (spsr)
        {
            uint old = ReadCurrentSpsr();
            WriteCurrentSpsr((old & ~mask) | (value & mask));
        }
        else
        {
            _registers[Cpsr] = ((_registers[Cpsr] & ~mask) | (value & mask)) | ModeUsr;
            LatchInterruptDisable();
        }
    }

    void EnterException(uint mode, uint vector, ArmBank bank)
    {
        bool thumb = IsThumb;
        uint instructionAddress = _registers[ProgramCounter] - (thumb ? 4u : 8u);
        _registers[SpsrIndex(bank)] = _registers[Cpsr];
        _registers[BankedRegisterIndex(bank, 6)] = instructionAddress + (thumb ? 2u : 4u);
        _registers[Cpsr] = (_registers[Cpsr] & ~0x1fu & ~ThumbBit) | mode | IrqDisableBit;
        LatchInterruptDisable();
        BranchTo(vector);
    }
}
