// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace Mia.Desktop.Camera;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void MacAuthorizationCallback(
    IntPtr block,
    [MarshalAs(UnmanagedType.I1)] bool granted);
