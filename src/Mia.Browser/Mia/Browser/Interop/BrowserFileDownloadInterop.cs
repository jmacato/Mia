// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices.JavaScript;

namespace Mia.Browser.Interop;

internal static partial class BrowserFileDownloadInterop
{
    public const string ModuleName = "mia-file-download";

    [JSImport("downloadTransferFile", ModuleName)]
    internal static partial void DownloadTransferFile(
        string name,
        string mediaType,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> data);
}
