// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicCommandPortRectangle(
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    public bool IsEmpty => Left >= Right || Top >= Bottom;
}
