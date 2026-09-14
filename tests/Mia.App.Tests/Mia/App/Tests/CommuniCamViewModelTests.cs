// SPDX-License-Identifier: MIT

using Mia.App.Presentation;
using Xunit;

namespace Mia.App.Tests;

public sealed class CommuniCamViewModelTests
{
    [Theory]
    [InlineData(false, false, "Start the emulator to connect CommuniCam")]
    [InlineData(true, false, "Connect CommuniCam to host camera")]
    [InlineData(true, true, "Disconnect CommuniCam")]
    public async Task TooltipExplainsAvailableConnectionState(
        bool running,
        bool connected,
        string expected)
    {
        var session = new RecordingMainViewSession
        {
            IsRunning = running,
            IsCommuniCamConnected = connected,
        };
        await using var sessionLease = session.ConfigureAwait(true);
        var viewModel = new CommuniCamViewModel(
            session,
            new RecordingMainViewHost(),
            new MainViewAsyncGate(),
            MainViewCapabilities.CommuniCam);
        using var viewModelLease = viewModel;

        Assert.Equal(expected, viewModel.ToolTip);
    }

    [Fact]
    public async Task UnavailableBackendReasonTakesPrecedence()
    {
        var session = new RecordingMainViewSession
        {
            IsHostCameraAvailable = false,
            HostCameraUnavailableReason = "No backend",
        };
        await using var sessionLease = session.ConfigureAwait(true);
        var viewModel = new CommuniCamViewModel(
            session,
            new RecordingMainViewHost(),
            new MainViewAsyncGate(),
            MainViewCapabilities.CommuniCam);
        using var viewModelLease = viewModel;

        Assert.Equal("No backend", viewModel.ToolTip);
    }
}
