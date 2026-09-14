// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

/// <summary>
/// Backend-neutral source of live camera frames. Desktop frontends can supply
/// platform implementations without adding platform dependencies to the
/// emulator or to the CommuniCam protocol implementation.
/// </summary>
internal interface IMiaCameraFrameSource : IAsyncDisposable
{
    string DisplayName { get; }

    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync();

    bool TryGetLatestFrame(out MiaCameraFrame? frame);
}
