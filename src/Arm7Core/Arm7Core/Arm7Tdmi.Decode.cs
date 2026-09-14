// SPDX-License-Identifier: MIT
//
// C# port of SkyEmu src/arm7.h.
// Copyright (c) 2021 Skyler "Sky" Saleh.

using System.Runtime.CompilerServices;

namespace Arm7Core;

public sealed partial class Arm7Tdmi
{
    static readonly int[] ArmKeyBits = [4, 5, 6, 7, 20, 21, 22, 23, 24, 25, 26, 27];
    static readonly int[] ThumbKeyBits = [8, 9, 10, 11, 12, 13, 14, 15];

    static readonly Arm7TdmiArmPattern[] ArmPatterns =
    [
        new(Arm7TdmiArmOperation.DataProcessing, "cccc0010oooSnnnnddddrrrrOOOOOOOO"),
        new(Arm7TdmiArmOperation.DataProcessing, "cccc00111ooSnnnnddddrrrrOOOOOOOO"),
        new(Arm7TdmiArmOperation.DataProcessing, "cccc00110oo1nnnnddddrrrrOOOOOOOO"),
        new(Arm7TdmiArmOperation.DataProcessing, "cccc0000oooSnnnnddddsssssss0mmmm"),
        new(Arm7TdmiArmOperation.DataProcessing, "cccc0000oooSnnnnddddssss0tt1mmmm"),
        new(Arm7TdmiArmOperation.DataProcessing, "cccc00011ooSnnnnddddsssssss0mmmm"),
        new(Arm7TdmiArmOperation.DataProcessing, "cccc00011ooSnnnnddddssss0tt1mmmm"),
        new(Arm7TdmiArmOperation.DataProcessing, "cccc00010oo1nnnnddddssss0tt1mmmm"),
        new(Arm7TdmiArmOperation.DataProcessing, "cccc00010oo1nnnnddddsssssss0mmmm"),
        new(Arm7TdmiArmOperation.Multiply, "cccc000000ASddddnnnnssss1001mmmm"),
        new(Arm7TdmiArmOperation.MultiplyLong, "cccc00001UASddddnnnnssss1001mmmm"),
        new(Arm7TdmiArmOperation.Swap, "cccc00010B00nnnndddd00001001mmmm"),
        new(Arm7TdmiArmOperation.BranchExchange, "cccc000100101111111111110001nnnn"),
        new(Arm7TdmiArmOperation.Undefined, "cccc000PUIW0nnnnddddoooo11S1oooo"),
        new(Arm7TdmiArmOperation.HalfwordTransfer, "cccc000PUIWLnnnndddd00001011mmmm"),
        new(Arm7TdmiArmOperation.HalfwordTransfer, "cccc000PUIW1nnnnddddOOOO1101OOOO"),
        new(Arm7TdmiArmOperation.HalfwordTransfer, "cccc000PUIW1nnnnddddOOOO1111OOOO"),
        new(Arm7TdmiArmOperation.SingleTransfer, "cccc010PUBWLnnnnddddOOOOOOOOOOOO"),
        new(Arm7TdmiArmOperation.SingleTransfer, "cccc011PUBWLnnnnddddOOOOOOO0mmmm"),
        new(Arm7TdmiArmOperation.Undefined, "cccc011--------------------1----"),
        new(Arm7TdmiArmOperation.BlockTransfer, "cccc100PUSWLnnnnllllllllllllllll"),
        new(Arm7TdmiArmOperation.Branch, "cccc1010OOOOOOOOOOOOOOOOOOOOOOOO"),
        new(Arm7TdmiArmOperation.Branch, "cccc1011OOOOOOOOOOOOOOOOOOOOOOOO"),
        new(Arm7TdmiArmOperation.CoprocessorDataTransfer, "cccc110PUNWLnnnndddd####OOOOOOOO"),
        new(Arm7TdmiArmOperation.CoprocessorDataOperation, "cccc1110oooonnnndddd####ppp0mmmm"),
        new(Arm7TdmiArmOperation.CoprocessorRegisterTransfer, "cccc1110oooLnnnndddd####ppp1mmmm"),
        new(Arm7TdmiArmOperation.SoftwareInterrupt, "cccc1111------------------------"),
        new(Arm7TdmiArmOperation.Mrs, "cccc00010P001111dddd000000000000"),
        new(Arm7TdmiArmOperation.Msr, "cccc00010P10100F111100000000mmmm"),
        new(Arm7TdmiArmOperation.Msr, "cccc00110P10100F1111oooooooooooo"),
        new(Arm7TdmiArmOperation.Undefined, "cccc000001--------------1001----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00011---------------1001----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010-1-------------1001----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010-01------------1001----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010-00------------01-0----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010-00------------0010----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010oo0nnnndddd00000101mmmm"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010-00------------00-1----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010-00------------0111----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010oo0ddddnnnnssss1yx0mmmm"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010110------------01-0----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010110------------0010----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc000101101111DDDD11110001MMMM"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010110------------0111----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010110------------0011----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010010------------0111----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc000100101111111111110011nnnn"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010010------------01-0----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00010010------------0010----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00110-000000000000001-------"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00110-0000000000000001------"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00110-00000000000000001-----"),
        new(Arm7TdmiArmOperation.Undefined, "cccc00110-000000000000000001----"),
        new(Arm7TdmiArmOperation.Undefined, "----00110-00------------0000----"),
    ];

    static readonly Arm7TdmiThumbPattern[] ThumbPatterns =
    [
        new(Arm7TdmiThumbOperation.MoveShiftedRegister, "00000OOOOOsssddd"),
        new(Arm7TdmiThumbOperation.MoveShiftedRegister, "00001OOOOOsssddd"),
        new(Arm7TdmiThumbOperation.MoveShiftedRegister, "00010OOOOOsssddd"),
        new(Arm7TdmiThumbOperation.AddSubtract, "00011I0nnnsssddd"),
        new(Arm7TdmiThumbOperation.AddSubtract, "00011I1nnnsssddd"),
        new(Arm7TdmiThumbOperation.ImmediateAlu, "001oodddOOOOOOOO"),
        new(Arm7TdmiThumbOperation.Alu, "010000oooosssddd"),
        new(Arm7TdmiThumbOperation.HighRegister, "010001oohHsssddd"),
        new(Arm7TdmiThumbOperation.PcRelativeLoad, "01001dddOOOOOOOO"),
        new(Arm7TdmiThumbOperation.RegisterOffsetTransfer, "0101LB0ooobbbddd"),
        new(Arm7TdmiThumbOperation.SignedTransfer, "0101HS1ooobbbddd"),
        new(Arm7TdmiThumbOperation.ImmediateTransfer, "011BLOOOOObbbddd"),
        new(Arm7TdmiThumbOperation.ImmediateHalfwordTransfer, "1000LOOOOObbbddd"),
        new(Arm7TdmiThumbOperation.SpRelativeTransfer, "1001LdddOOOOOOOO"),
        new(Arm7TdmiThumbOperation.LoadAddress, "1010SdddOOOOOOOO"),
        new(Arm7TdmiThumbOperation.AddSpOffset, "10110000SOOOOOOO"),
        new(Arm7TdmiThumbOperation.PushPop, "1011L10Rllllllll"),
        new(Arm7TdmiThumbOperation.MultipleTransfer, "1100Lbbbllllllll"),
        new(Arm7TdmiThumbOperation.ConditionalBranch, "11010cccOOOOOOOO"),
        new(Arm7TdmiThumbOperation.ConditionalBranch, "110110ccOOOOOOOO"),
        new(Arm7TdmiThumbOperation.ConditionalBranch, "1101110cOOOOOOOO"),
        new(Arm7TdmiThumbOperation.ConditionalBranch, "11011110OOOOOOOO"),
        new(Arm7TdmiThumbOperation.SoftwareInterrupt, "11011111OOOOOOOO"),
        new(Arm7TdmiThumbOperation.Branch, "11100OOOOOOOOOOO"),
        new(Arm7TdmiThumbOperation.LongBranch, "1111HOOOOOOOOOOO"),
        new(Arm7TdmiThumbOperation.LongBranch, "11101OOOOOOOOOOO"),
        new(Arm7TdmiThumbOperation.Undefined, "1011--1---------"),
        new(Arm7TdmiThumbOperation.Undefined, "10110001--------"),
        new(Arm7TdmiThumbOperation.Undefined, "1011100---------"),
    ];

    static readonly Arm7TdmiArmOperation[] ArmDecodeTable = BuildArmDecodeTable();
    static readonly Arm7TdmiThumbOperation[] ThumbDecodeTable = BuildThumbDecodeTable();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ExecuteArm(uint instruction)
    {
        int key = (int)(((instruction >> 4) & 0x0f) | ((instruction >> 16) & 0xff0));
        switch (ArmDecodeTable[key])
        {
            case Arm7TdmiArmOperation.DataProcessing: ExecuteArmDataProcessing(instruction); break;
            case Arm7TdmiArmOperation.Multiply: ExecuteArmMultiply(instruction); break;
            case Arm7TdmiArmOperation.MultiplyLong: ExecuteArmMultiplyLong(instruction); break;
            case Arm7TdmiArmOperation.Swap: ExecuteArmSwap(instruction); break;
            case Arm7TdmiArmOperation.BranchExchange: ExecuteArmBranchExchange(instruction); break;
            case Arm7TdmiArmOperation.HalfwordTransfer: ExecuteArmHalfwordTransfer(instruction); break;
            case Arm7TdmiArmOperation.SingleTransfer: ExecuteArmSingleTransfer(instruction); break;
            case Arm7TdmiArmOperation.BlockTransfer: ExecuteArmBlockTransfer(instruction); break;
            case Arm7TdmiArmOperation.Branch: ExecuteArmBranch(instruction); break;
            case Arm7TdmiArmOperation.CoprocessorDataTransfer:
            case Arm7TdmiArmOperation.CoprocessorDataOperation:
            case Arm7TdmiArmOperation.CoprocessorRegisterTransfer:
                ExecuteArmUndefined(instruction);
                break;
            case Arm7TdmiArmOperation.SoftwareInterrupt: ExecuteSoftwareInterrupt(instruction); break;
            case Arm7TdmiArmOperation.Mrs: ExecuteArmMrs(instruction); break;
            case Arm7TdmiArmOperation.Msr: ExecuteArmMsr(instruction); break;
            default: ExecuteArmUndefined(instruction); break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ExecuteThumb(ushort instruction)
    {
        switch (ThumbDecodeTable[instruction >> 8])
        {
            case Arm7TdmiThumbOperation.MoveShiftedRegister: ExecuteThumbMoveShifted(instruction); break;
            case Arm7TdmiThumbOperation.AddSubtract: ExecuteThumbAddSubtract(instruction); break;
            case Arm7TdmiThumbOperation.ImmediateAlu: ExecuteThumbImmediateAlu(instruction); break;
            case Arm7TdmiThumbOperation.Alu: ExecuteThumbAlu(instruction); break;
            case Arm7TdmiThumbOperation.HighRegister: ExecuteThumbHighRegister(instruction); break;
            case Arm7TdmiThumbOperation.PcRelativeLoad: ExecuteThumbPcRelativeLoad(instruction); break;
            case Arm7TdmiThumbOperation.RegisterOffsetTransfer: ExecuteThumbRegisterTransfer(instruction); break;
            case Arm7TdmiThumbOperation.SignedTransfer: ExecuteThumbSignedTransfer(instruction); break;
            case Arm7TdmiThumbOperation.ImmediateTransfer: ExecuteThumbImmediateTransfer(instruction); break;
            case Arm7TdmiThumbOperation.ImmediateHalfwordTransfer: ExecuteThumbImmediateHalfwordTransfer(instruction); break;
            case Arm7TdmiThumbOperation.SpRelativeTransfer: ExecuteThumbSpRelativeTransfer(instruction); break;
            case Arm7TdmiThumbOperation.LoadAddress: ExecuteThumbLoadAddress(instruction); break;
            case Arm7TdmiThumbOperation.AddSpOffset: ExecuteThumbAddSpOffset(instruction); break;
            case Arm7TdmiThumbOperation.PushPop: ExecuteThumbPushPop(instruction); break;
            case Arm7TdmiThumbOperation.MultipleTransfer: ExecuteThumbMultipleTransfer(instruction); break;
            case Arm7TdmiThumbOperation.ConditionalBranch: ExecuteThumbConditionalBranch(instruction); break;
            case Arm7TdmiThumbOperation.SoftwareInterrupt: ExecuteSoftwareInterrupt(instruction); break;
            case Arm7TdmiThumbOperation.Branch: ExecuteThumbBranch(instruction); break;
            case Arm7TdmiThumbOperation.LongBranch: ExecuteThumbLongBranch(instruction); break;
            default: ExecuteThumbUndefined(instruction); break;
        }
    }

    static Arm7TdmiArmOperation[] BuildArmDecodeTable()
    {
        var table = new Arm7TdmiArmOperation[4096];
        for (int key = 0; key < table.Length; key++)
        {
            Arm7TdmiArmOperation operation = Arm7TdmiArmOperation.Undefined;
            foreach (Arm7TdmiArmPattern pattern in ArmPatterns)
            {
                if (Matches(pattern.Bits, key, ArmKeyBits, 31))
                {
                    operation = pattern.Operation;
                }
            }
            table[key] = operation;
        }
        return table;
    }

    static Arm7TdmiThumbOperation[] BuildThumbDecodeTable()
    {
        var table = new Arm7TdmiThumbOperation[256];
        for (int key = 0; key < table.Length; key++)
        {
            Arm7TdmiThumbOperation operation = Arm7TdmiThumbOperation.Undefined;
            foreach (Arm7TdmiThumbPattern pattern in ThumbPatterns)
            {
                if (Matches(pattern.Bits, key, ThumbKeyBits, 15))
                {
                    operation = pattern.Operation;
                }
            }
            table[key] = operation;
        }
        return table;
    }

    static bool Matches(string pattern, int key, int[] keyBits, int mostSignificantBit)
    {
        for (int keyBit = 0; keyBit < keyBits.Length; keyBit++)
        {
            char expected = pattern[mostSignificantBit - keyBits[keyBit]];
            bool actual = ((key >> keyBit) & 1) != 0;
            if ((expected == '1' && !actual) || (expected == '0' && actual))
            {
                return false;
            }
        }
        return true;
    }
}
