// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicCommandPortBltRequest(
    int SourceX,
    int SourceY,
    int DestinationX,
    int DestinationY,
    int Width,
    int Height,
    byte RasterOperation);
