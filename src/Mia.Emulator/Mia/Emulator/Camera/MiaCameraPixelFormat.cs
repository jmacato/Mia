// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

/// <summary>
/// Pixel layouts accepted from host camera backends. Backends can publish a
/// convenient native layout; the emulated accessory performs any conversion
/// required by the handset protocol.
/// </summary>
internal enum MiaCameraPixelFormat
{
    Rgb332,
    Bgra32,
}
