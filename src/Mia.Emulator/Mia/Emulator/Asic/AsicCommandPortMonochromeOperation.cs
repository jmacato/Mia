// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicCommandPortMonochromeOperation(
    AsicCommandPortMonochromeBitmap Bitmap,
    AsicCommandPortSurface Destination,
    int DestinationX,
    int DestinationY);
