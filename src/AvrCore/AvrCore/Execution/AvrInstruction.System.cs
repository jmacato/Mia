// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js instruction.ts)

namespace AvrCore.Execution;

public static partial class AvrInstruction
{
    static void ExecuteNop(Cpu cpu, int opcode)
    {
        /* NOP, 0000 0000 0000 0000 */
        /* NOP */
    }

    static void ExecuteSleep(Cpu cpu, int opcode)
    {
        /* SLEEP, 1001 0101 1000 1000 */
        /* not implemented */
    }

    static void ExecuteSpm(Cpu cpu, int opcode)
    {
        /* SPM, 1001 0101 1110 1000 */
        /* not implemented */
    }

    static void ExecuteSpmPostIncrement(Cpu cpu, int opcode)
    {
        /* SPM(INC), 1001 0101 1111 1000 */
        /* not implemented */
    }

    static void ExecuteWdr(Cpu cpu, int opcode)
    {
        /* WDR, 1001 0101 1010 1000 */
        cpu.OnWatchdogReset();
    }
}
