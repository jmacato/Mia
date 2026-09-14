// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Display;

/// <summary>
/// Atomically publishes host-visible LCD snapshots for live diagnostic tools.
/// This observes the completed S-4595 framebuffer and is not visible to either
/// emulated CPU.
/// </summary>
internal sealed class LiveFramebufferFile : IDisposable
{
    readonly string _path;
    readonly string _temporaryPath;

    public LiveFramebufferFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _temporaryPath = $"{_path}.{Environment.ProcessId}.tmp";
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public string FilePath => _path;

    public long PublishedFrameCount { get; private set; }

    public void Publish(ReadOnlySpan<byte> framebuffer)
    {
        using (var stream = new FileStream(
            _temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.SequentialScan))
        {
            stream.Write(framebuffer);
        }

        File.Move(_temporaryPath, _path, overwrite: true);
        PublishedFrameCount++;
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_temporaryPath);
        }
        catch (IOException)
        {
            // A completed rename leaves no temporary file. Cleanup failure on
            // an interrupted publish must not alter emulation state.
        }
    }
}
