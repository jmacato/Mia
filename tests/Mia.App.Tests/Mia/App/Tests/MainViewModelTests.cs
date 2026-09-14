// SPDX-License-Identifier: MIT

using Mia.App.Presentation;
using Xunit;

namespace Mia.App.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task InitialStateMatchesTheSharedControls()
    {
        var session = new RecordingMainViewSession();
        await using var sessionLease = session.ConfigureAwait(true);
        await using MainViewModel viewModel = CreateViewModel(session);

        Assert.Equal("Ready", viewModel.Status);
        Assert.Equal("+15551234", viewModel.Gsm.IncomingNumber);
        Assert.Equal("hello", viewModel.Gsm.IncomingSmsText);
        Assert.Equal("32640", viewModel.Gsm.RssiRawSample);
        Assert.True(viewModel.Bluetooth.EmulatePeer);
        Assert.True(viewModel.Bluetooth.ReturnObject);
        Assert.True(viewModel.BootCommand.CanExecute(null));
        Assert.False(viewModel.StopCommand.CanExecute(null));
        Assert.False(viewModel.Gsm.QueueIncomingCallCommand.CanExecute(null));
        Assert.False(viewModel.CommuniCam.ToggleCommand.CanExecute(null));
        Assert.Equal(
            "Start the emulator to connect CommuniCam",
            viewModel.CommuniCam.ToolTip);
    }

    [Fact]
    public async Task BootAndStopUpdateInputAndCommandState()
    {
        var session = new RecordingMainViewSession();
        await using var sessionLease = session.ConfigureAwait(true);
        await using MainViewModel viewModel = CreateViewModel(session);

        await viewModel.BootCommand.ExecuteAsync(null).ConfigureAwait(true);

        Assert.Equal(1, session.StartCount);
        Assert.True(viewModel.IsInputEnabled);
        Assert.True(viewModel.Gsm.IsEnabled);
        Assert.False(viewModel.BootCommand.CanExecute(null));
        Assert.True(viewModel.StopCommand.CanExecute(null));
        Assert.True(viewModel.CommuniCam.ToggleCommand.CanExecute(null));

        await viewModel.CommuniCam.ToggleCommand.ExecuteAsync(null)
            .ConfigureAwait(true);
        Assert.True(viewModel.CommuniCam.IsConnected);
        Assert.Equal("Disconnect CommuniCam", viewModel.CommuniCam.ToolTip);

        viewModel.Gsm.IncomingNumber = "+12025550123";
        viewModel.Gsm.AutoAnswer = true;
        viewModel.Gsm.QueueIncomingCallCommand.Execute(null);
        Assert.Equal("queued call", viewModel.Gsm.Result);
        Assert.Equal(("+12025550123", true), session.LastIncomingCall);

        await viewModel.StopCommand.ExecuteAsync(null).ConfigureAwait(true);

        Assert.Equal(1, session.StopCount);
        Assert.False(viewModel.IsInputEnabled);
        Assert.False(viewModel.Gsm.IsEnabled);
        Assert.True(viewModel.BootCommand.CanExecute(null));
        Assert.False(viewModel.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task FileCommandsUseTheInjectedHostService()
    {
        var session = new RecordingMainViewSession();
        await using var sessionLease = session.ConfigureAwait(true);
        var host = new RecordingMainViewHost
        {
            SelectedFile = new("person.vcf", new byte[] { 1, 2, 3 }),
            SaveResult = true,
        };
        await using MainViewModel viewModel = CreateViewModel(session, host);

        await viewModel.Bluetooth.StageFileCommand.ExecuteAsync(null)
            .ConfigureAwait(true);
        await viewModel.Bluetooth.SaveFileCommand.ExecuteAsync(null)
            .ConfigureAwait(true);
        await viewModel.Infrared.StageFileCommand.ExecuteAsync(null)
            .ConfigureAwait(true);
        await viewModel.Infrared.SaveFileCommand.ExecuteAsync(null)
            .ConfigureAwait(true);

        Assert.Equal(2, host.PickTitles.Count);
        Assert.Equal(2, host.SavedFiles.Count);
        Assert.Equal("TEXT/X-VCARD", session.StagedBluetoothObject?.MediaType);
        Assert.Equal("TEXT/X-VCARD", session.StagedInfraredObject?.MediaType);
        Assert.Equal("TEXT/X-VCARD", host.SavedFiles[0].MediaType);
        Assert.Equal("TEXT/X-VCARD", host.SavedFiles[1].MediaType);
        Assert.EndsWith(
            "Saved without byte conversion.",
            viewModel.Bluetooth.Result,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "Saved without byte conversion.",
            viewModel.Infrared.Result,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FrameBurstsPresentOnlyTheLatestFrameAndContinueAfterDispatch()
    {
        var session = new RecordingMainViewSession();
        await using var sessionLease = session.ConfigureAwait(true);
        var host = new RecordingMainViewHost(queuePosts: true);
        await using MainViewModel viewModel = CreateViewModel(session, host);
        var presented = new List<byte>();
        viewModel.FrameReady += (_, frame) => presented.Add(frame.Frame.Span[0]);

        for (var index = 0; index < 100; index++)
        {
            session.PublishFrame(new byte[] { (byte)index });
        }
        Assert.Equal(1, host.Count);
        host.Drain();
        Assert.Equal(new byte[] { 99 }, presented);

        session.PublishFrame(new byte[] { 100 });
        host.Drain();
        Assert.Equal(new byte[] { 99, 100 }, presented);
    }

    [Fact]
    public async Task SessionEventsKeepDispatcherOrderThroughHostDisposal()
    {
        var session = new RecordingMainViewSession();
        await using var sessionLease = session.ConfigureAwait(true);
        var host = new RecordingMainViewHost(queuePosts: true);
        MainViewModel viewModel = CreateViewModel(session, host);

        session.PublishOutput("first");
        session.PublishStatus("second");
        Assert.Equal("", viewModel.Diagnostics.Log);
        Assert.Equal("Ready", viewModel.Status);

        await viewModel.DisposeAsync().ConfigureAwait(true);
        await viewModel.DisposeAsync().ConfigureAwait(true);
        host.Drain();

        Assert.Equal("first", viewModel.Diagnostics.Log);
        Assert.Equal("Ready", viewModel.Status);
        Assert.Equal(1, session.DisposeCount);

        int queuedAfterDisposal = host.Count;
        session.PublishOutput("late");
        Assert.Equal(queuedAfterDisposal, host.Count);
    }

    [Fact]
    public async Task ClosePreparationDisablesControlsAndReportsHostFailures()
    {
        var session = new RecordingMainViewSession();
        await using var sessionLease = session.ConfigureAwait(true);
        var host = new RecordingMainViewHost();
        await using MainViewModel viewModel = CreateViewModel(session, host);
        await viewModel.BootCommand.ExecuteAsync(null).ConfigureAwait(true);

        viewModel.ReportHostFailure(
            "Automation API",
            new InvalidOperationException("listener unavailable"));
        viewModel.ReportHostFailure(
            "Shutdown",
            new IOException("overlay unavailable"));
        viewModel.BeginClose();

        Assert.Equal("Saving emulator state…", viewModel.Status);
        Assert.Equal(
            "Automation API failed: listener unavailable" +
            Environment.NewLine +
            "Shutdown failed: overlay unavailable",
            viewModel.Diagnostics.Log);
        Assert.Equal(2, host.Errors.Count);
        Assert.False(viewModel.IsInputEnabled);
        Assert.False(viewModel.Gsm.IsEnabled);
        Assert.False(viewModel.BootCommand.CanExecute(null));
        Assert.False(viewModel.StopCommand.CanExecute(null));
        Assert.False(viewModel.CommuniCam.ToggleCommand.CanExecute(null));
    }

    [Fact]
    public async Task PhoneInputUsesTheSessionBoundary()
    {
        var session = new RecordingMainViewSession();
        await using var sessionLease = session.ConfigureAwait(true);
        await using MainViewModel viewModel = CreateViewModel(session);

        viewModel.SetKey(0x12, 0x34, 0x56, pressed: true);
        viewModel.SetPowerKey(pressed: false);

        Assert.Equal(
            new RecordedMainViewKey(0x12, 0x34, 0x56, Pressed: true),
            session.LastKey);
        Assert.False(session.LastPowerKeyPressed);
    }

    [Fact]
    public async Task SessionEventArgsCrossTheHostBoundaryWithoutEngineTypes()
    {
        var session = new RecordingMainViewSession();
        await using var sessionLease = session.ConfigureAwait(true);
        var host = new RecordingMainViewHost();
        await using MainViewModel viewModel = CreateViewModel(session, host);
        MainViewFrameEventArgs? frame = null;
        MainViewPhoneLedEventArgs? phoneLeds = null;
        viewModel.FrameReady += (_, value) => frame = value;
        viewModel.PhoneLedsChanged += (_, value) => phoneLeds = value;

        await viewModel.BootCommand.ExecuteAsync(null).ConfigureAwait(true);
        session.PublishStatusLeds(new(
            bluetoothBlue: true,
            networkGreen: true));
        session.PublishPhoneLeds(new(
            leftRed: true,
            leftGreen: false,
            rightBlue: true,
            backlightsOn: true));
        session.PublishFrame(new byte[] { 1, 2, 3 });
        session.PublishAudio(new short[] { 10, -20 });

        Assert.True(viewModel.IsBluetoothStatusActive);
        Assert.True(viewModel.IsNetworkStatusActive);
        Assert.NotNull(phoneLeds);
        Assert.True(phoneLeds.LeftRed);
        Assert.True(phoneLeds.RightBlue);
        Assert.NotNull(frame);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Frame.ToArray());
        Assert.Equal(48_000, host.AudioOutput?.SampleRate);
        Assert.Equal(new short[] { 10, -20 }, host.AudioOutput?.Samples);
    }

    [Fact]
    public async Task StalledUiRetainsOnlyRecentAudioAndOneDispatcherCallback()
    {
        var session = new RecordingMainViewSession();
        await using var sessionLease = session.ConfigureAwait(true);
        var host = new RecordingMainViewHost(queuePosts: true);
        await using MainViewModel viewModel = CreateViewModel(session, host);
        await viewModel.BootCommand.ExecuteAsync(null).ConfigureAwait(true);
        host.Drain();
        for (int i = 0; i < 10000; i++)
            session.PublishAudio(Enumerable.Repeat((short)i, 960).ToArray());
        Assert.Equal(1, host.Count);
        host.Drain();
        Assert.Equal(9600, host.AudioOutput!.Samples.Count);
        Assert.Equal(9990, host.AudioOutput.Samples[0]);
        Assert.Equal(9999, host.AudioOutput.Samples[^1]);
    }

    [Fact]
    public async Task WorkerAudioOutputDoesNotWaitForTheUiDispatcher()
    {
        var session = new RecordingMainViewSession();
        await using var sessionLease = session.ConfigureAwait(true);
        var host = new RecordingMainViewHost(queuePosts: true);
        await using MainViewModel viewModel = CreateViewModel(session, host);
        await viewModel.BootCommand.ExecuteAsync(null).ConfigureAwait(true);
        host.Drain();
        host.AudioOutput!.AcceptsWorkerThread = true;
        int writer = await Task.Run(() =>
        {
            session.PublishAudio(new short[] { 8192, -8192 });
            return Environment.CurrentManagedThreadId;
        }).ConfigureAwait(true);
        Assert.Equal(0, host.Count);
        Assert.Equal(writer, host.AudioOutput.LastWriteThread);
        Assert.Equal(new short[] { 8192, -8192 }, host.AudioOutput.Samples);
    }

    static MainViewModel CreateViewModel(
        RecordingMainViewSession session,
        RecordingMainViewHost? host = null) =>
        new(
            session,
            host ?? new RecordingMainViewHost(),
            new MainViewOptions(
                "Test host",
                MainViewCapabilities.Diagnostics |
                    MainViewCapabilities.LiveAudio |
                    MainViewCapabilities.CommuniCam,
                AutoStart: false,
                PaceToRealTime: true));
}
