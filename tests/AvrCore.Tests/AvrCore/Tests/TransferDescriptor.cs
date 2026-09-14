// SPDX-License-Identifier: MIT

namespace AvrCore.Tests;

internal readonly record struct TransferDescriptor(
    ushort Source,
    ushort Destination,
    ushort Length);
