// SPDX-License-Identifier: MIT

namespace AvrCore.Tests;

internal readonly record struct SoftwareStackBounds(
    int Context,
    int Descriptor,
    int Top,
    int Floor);
