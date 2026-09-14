// SPDX-License-Identifier: MIT

using Mia.Desktop.Camera;
using Mia.Emulator.Camera;
using Xunit;

namespace Mia.App.Tests;

public sealed class CommuniCamLifecycleTests
{
    [Fact]
    public async Task AttachmentDoesNotStartBackendAndDisposalIsIdempotent()
    {
        var source = new RecordingCameraFrameSource();
        await using (source.ConfigureAwait(true))
        {
            var lease = await MiaCameraFrameSourceLease.CreateAsync(
                new RecordingCameraFrameSourceFactory(source),
                CancellationToken.None).ConfigureAwait(true);

            Assert.NotSame(source, lease.Source);
            Assert.Equal(0, source.StartCount);

            await lease.DisposeAsync().ConfigureAwait(true);
            await lease.DisposeAsync().ConfigureAwait(true);

            Assert.Equal(0, source.StopCount);
            Assert.Equal(1, source.DisposeCount);
            Assert.Throws<ObjectDisposedException>(() => lease.Source);
        }
    }

    [Fact]
    public async Task FrameDemandStartsBackendAndIdleTimeoutStopsIt()
    {
        var source = new RecordingCameraFrameSource();
        await using (source.ConfigureAwait(true))
        {
            var lease = await MiaCameraFrameSourceLease.CreateAsync(
                new RecordingCameraFrameSourceFactory(source),
                CancellationToken.None,
                TimeSpan.FromMilliseconds(30)).ConfigureAwait(true);
            await using (lease.ConfigureAwait(true))
            {
                Assert.False(lease.Source.TryGetLatestFrame(out _));
                await WaitUntilAsync(() => source.StartCount == 1)
                    .ConfigureAwait(true);
                await WaitUntilAsync(() => source.StopCount == 1)
                    .ConfigureAwait(true);

                Assert.Equal(1, source.StartCount);
                Assert.Equal(1, source.StopCount);
            }
        }
    }

    [Fact]
    public async Task RepeatedFrameDemandKeepsBackendActive()
    {
        var source = new RecordingCameraFrameSource();
        await using (source.ConfigureAwait(true))
        {
            var lease = await MiaCameraFrameSourceLease.CreateAsync(
                new RecordingCameraFrameSourceFactory(source),
                CancellationToken.None,
                TimeSpan.FromMilliseconds(80)).ConfigureAwait(true);
            await using (lease.ConfigureAwait(true))
            {
                Assert.False(lease.Source.TryGetLatestFrame(out _));
                await WaitUntilAsync(() => source.StartCount == 1)
                    .ConfigureAwait(true);
                await Task.Delay(50).ConfigureAwait(true);
                Assert.False(lease.Source.TryGetLatestFrame(out _));
                await Task.Delay(50).ConfigureAwait(true);

                Assert.Equal(1, source.StartCount);
                Assert.Equal(0, source.StopCount);
                await WaitUntilAsync(() => source.StopCount == 1)
                    .ConfigureAwait(true);
            }
        }
    }

    [Fact]
    public async Task FailedDemandActivationStopsAndLaterDisposesBackend()
    {
        var source = new RecordingCameraFrameSource
        {
            StartError = new InvalidOperationException("capture failed"),
        };
        await using (source.ConfigureAwait(true))
        {
            var lease = await MiaCameraFrameSourceLease.CreateAsync(
                new RecordingCameraFrameSourceFactory(source),
                CancellationToken.None).ConfigureAwait(true);
            var activationFailure = new TaskCompletionSource<Exception>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lease.BackendFailed += error =>
                activationFailure.TrySetResult(error);

            Assert.Equal(0, source.StartCount);
            Assert.False(lease.Source.TryGetLatestFrame(out _));
            Exception error = await activationFailure.Task.WaitAsync(
                TimeSpan.FromSeconds(1)).ConfigureAwait(true);

            Assert.Equal("capture failed", error.Message);
            Assert.Equal(1, source.StartCount);
            Assert.Equal(1, source.StopCount);
            Assert.False(lease.Source.IsAvailable);
            Assert.Equal("capture failed", lease.Source.UnavailableReason);

            await lease.DisposeAsync().ConfigureAwait(true);
            Assert.Equal(1, source.DisposeCount);
        }
    }

    static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token).ConfigureAwait(true);
        }
    }
}
