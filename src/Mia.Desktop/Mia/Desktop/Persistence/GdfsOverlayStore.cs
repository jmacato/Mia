// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace Mia.Desktop.Persistence;

/// <summary>
/// Keeps firmware-produced NOR state in a separate atomic overlay while the
/// user-supplied service image remains immutable.
/// </summary>
internal sealed class GdfsOverlayStore
{
    readonly string _pristinePath;
    readonly string _overlayPath;
    byte[]? _pristineRaw;

    public GdfsOverlayStore(string pristinePath, string overlayPath)
    {
        _pristinePath = pristinePath;
        _overlayPath = overlayPath;
    }

    public async Task<GdfsOverlayLoad> LoadAsync(
        bool ignoreOverlay,
        CancellationToken cancellationToken = default)
    {
        var source = await File.ReadAllBytesAsync(
            _pristinePath,
            cancellationToken).ConfigureAwait(false);
        var pristineRaw = GdfsImage.DecodeRaw(source);
        _pristineRaw = pristineRaw;
        if (ignoreOverlay || !File.Exists(_overlayPath))
        {
            return new(pristineRaw.ToArray(), 0, FromOverlay: false);
        }

        var overlay = await File.ReadAllBytesAsync(
            _overlayPath,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var restored = GdfsOverlay.Apply(pristineRaw, overlay);
            return new(
                restored.RawImage,
                restored.ChangedBlockCount,
                FromOverlay: true);
        }
        catch (InvalidDataException error)
        {
            return new(
                pristineRaw.ToArray(),
                0,
                FromOverlay: false,
                $"GDFS overlay: ignored invalid persisted state · {error.Message}");
        }
    }

    public async Task<GdfsOverlaySave> SaveAsync(
        ReadOnlyMemory<byte> currentRaw,
        CancellationToken cancellationToken = default)
    {
        var pristineRaw = _pristineRaw ?? throw new InvalidOperationException(
            "The pristine GDFS image must be loaded before saving its overlay.");
        var overlay = GdfsOverlay.Create(pristineRaw, currentRaw.Span);
        await WriteAtomicAsync(_overlayPath, overlay.Image, cancellationToken).ConfigureAwait(false);
        return new(overlay.ChangedBlockCount, overlay.Image.Length);
    }

    static async Task WriteAtomicAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
