// SPDX-License-Identifier: MIT

using Mia.Desktop.Persistence;
using Xunit;

namespace Mia.App.Tests;

public sealed class GdfsOverlayStoreTests
{
    const int RawImageLength = 0x280000;

    [Fact]
    public async Task InvalidOverlayFallsBackToPristineImage()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"mia-overlay-tests-{Guid.NewGuid():N}");
        string pristinePath = Path.Combine(directory, "pristine.raw");
        string overlayPath = Path.Combine(directory, "state.overlay");
        byte[] pristine = Enumerable.Repeat((byte)0xff, RawImageLength).ToArray();
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(pristinePath, pristine);
            await File.WriteAllBytesAsync(overlayPath, [0x00, 0x01, 0x02]);
            var store = new GdfsOverlayStore(pristinePath, overlayPath);

            GdfsOverlayLoad result = await store.LoadAsync(ignoreOverlay: false);

            Assert.False(result.FromOverlay);
            Assert.Equal(0, result.ChangedBlockCount);
            Assert.Equal(pristine, result.RawImage.ToArray());
            Assert.NotNull(result.RecoveryMessage);
            Assert.True(File.Exists(overlayPath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
