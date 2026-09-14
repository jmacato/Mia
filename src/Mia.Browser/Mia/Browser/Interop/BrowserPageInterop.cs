// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices.JavaScript;

namespace Mia.Browser.Interop;

internal static partial class BrowserPageInterop
{
    public const string ModuleName = "mia-page";

    [JSImport("reloadForEmulatorRestart", ModuleName)]
    internal static partial void ReloadForEmulatorRestart();

    [JSImport("fetchImage", ModuleName)]
    private static partial Task<JSObject> FetchImageAsync(string url);

    [JSImport("copyImage", ModuleName)]
    private static partial void CopyImage(
        JSObject image,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

    internal static async Task<byte[]> LoadImageAsync(string url)
    {
        using JSObject image = await FetchImageAsync(url);
        var bytes = new byte[image.GetPropertyAsInt32("byteLength")];
        CopyImage(image, bytes);
        return bytes;
    }
}
