// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace Mia.Desktop.Camera;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void MacSampleBufferCallback(
    IntPtr receiver,
    IntPtr selector,
    IntPtr output,
    IntPtr sampleBuffer,
    IntPtr connection);
