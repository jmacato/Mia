// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicCommandPortSurface(
    int Address,
    int Width,
    int Left,
    int Top,
    int Right,
    int Bottom);
