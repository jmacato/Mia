// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public readonly record struct MainViewTransferObject(
    string Name,
    string MediaType,
    ReadOnlyMemory<byte> Data);
