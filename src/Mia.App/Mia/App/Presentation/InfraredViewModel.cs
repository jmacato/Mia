// SPDX-License-Identifier: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mia.App.Presentation;

public sealed class InfraredViewModel : ObservableObject, IDisposable
{
    readonly IMainViewSession _session;
    readonly IMainViewHost _host;
    readonly MainViewAsyncGate _gate;
    string _result =
        "To send to the phone, open Connect > Receive item. To receive " +
        "from the phone, choose infrared when sending any native item.";
    bool _canSaveFile;
    int _disposeStarted;

    public InfraredViewModel(
        IMainViewSession session,
        IMainViewHost host,
        MainViewAsyncGate gate)
    {
        _session = session;
        _host = host;
        _gate = gate;
        StageFileCommand = CreateCommand(StageFileAsync, () => true);
        SaveFileCommand = CreateCommand(SaveFileAsync, () => CanSaveFile);
        _session.InfraredStatusChanged += OnStatusChanged;
        _gate.AvailabilityChanged += NotifyCommandState;
    }

    public string Result
    {
        get => _result;
        private set => SetProperty(ref _result, value);
    }

    public bool CanSaveFile
    {
        get => _canSaveFile;
        private set
        {
            if (SetProperty(ref _canSaveFile, value))
            {
                SaveFileCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public IAsyncRelayCommand StageFileCommand { get; }
    public IAsyncRelayCommand SaveFileCommand { get; }

    AsyncRelayCommand CreateCommand(Func<Task> operation, Func<bool> canExecute) =>
        new AsyncRelayCommand(
            () => RunAsync(operation),
            () => _gate.IsAvailable && canExecute(),
            AsyncRelayCommandOptions.None);

    async Task RunAsync(Func<Task> operation)
    {
        try
        {
            if (!await _gate.TryRunAsync(operation).ConfigureAwait(true))
            {
                Result = "Another emulator operation is in progress.";
            }
        }
        catch (Exception error) when (MainViewExceptions.IsRecoverable(error))
        {
            Result = $"Infrared file transfer failed · {error.Message}";
            _host.ReportError("Infrared file transfer", error);
        }
    }

    async Task StageFileAsync()
    {
        MainViewHostFile? selected = await _host.PickFileAsync(
            "Send a file to the handset via IrDA").ConfigureAwait(true);
        if (selected is not { } file)
        {
            return;
        }

        _session.TryStageInfraredObject(
            new MainViewTransferObject(
                file.Name,
                MainViewMediaTypes.Guess(file.Name),
                file.Data),
            out string result);
        Result = result;
    }

    async Task SaveFileAsync()
    {
        if (!_session.TryGetInfraredReceivedObject(
                out MainViewTransferObject transferObject,
                out string result))
        {
            Result = result;
            CanSaveFile = false;
            return;
        }

        bool saved = await _host.SaveFileAsync(
            transferObject.Name,
            transferObject.MediaType,
            transferObject.Data).ConfigureAwait(true);
        Result = saved ? $"{result} Saved without byte conversion." : result;
    }

    void OnStatusChanged(object? sender, MainViewInfraredStatusEventArgs status) => _host.Post(() =>
    {
        Result = status.Message;
        CanSaveFile = status.HasReceivedObject;
    });

    void NotifyCommandState(object? sender, EventArgs eventArgs)
    {
        StageFileCommand.NotifyCanExecuteChanged();
        SaveFileCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _session.InfraredStatusChanged -= OnStatusChanged;
        _gate.AvailabilityChanged -= NotifyCommandState;
    }
}
