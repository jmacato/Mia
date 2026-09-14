// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public readonly record struct MainViewHostFile(
    string Name,
    ReadOnlyMemory<byte> Data);
