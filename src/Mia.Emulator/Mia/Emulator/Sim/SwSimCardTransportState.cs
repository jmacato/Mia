// SPDX-License-Identifier: BSD-3-Clause

namespace Mia.Emulator.Sim;

internal enum SwSimCardTransportState
{
    AwaitingInitialByte,
    Pps,
    TpduHeader,
    CommandData,
}
