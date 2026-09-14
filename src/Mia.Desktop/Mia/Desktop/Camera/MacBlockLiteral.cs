// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace Mia.Desktop.Camera;

[StructLayout(LayoutKind.Sequential)]
internal struct MacBlockLiteral
{
    public IntPtr Isa;
    public int Flags;
    public int Reserved;
    public IntPtr Invoke;
    public IntPtr Descriptor;
}
