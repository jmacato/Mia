// SPDX-License-Identifier: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mia.App.Presentation;

public sealed class GsmViewModel : ObservableObject, IDisposable
{
    readonly IMainViewSession _session;
    readonly IMainViewHost _host;
    string _incomingNumber = "+15551234";
    string _incomingSmsText = "hello";
    string _rssiRawSample = "32640";
    string _result = "GSM controls become available after boot";
    bool _autoAnswer;
    bool _isEnabled;
    int _disposeStarted;

    public GsmViewModel(IMainViewSession session, IMainViewHost host)
    {
        _session = session;
        _host = host;
        QueueIncomingCallCommand = new RelayCommand(QueueIncomingCall, () => IsEnabled);
        QueueIncomingSmsCommand = new RelayCommand(QueueIncomingSms, () => IsEnabled);
        SetRssiCommand = new RelayCommand(SetRssi, () => IsEnabled);
        _session.GsmMessageEmitted += OnMessageEmitted;
    }

    public string IncomingNumber
    {
        get => _incomingNumber;
        set => SetProperty(ref _incomingNumber, value ?? string.Empty);
    }

    public string IncomingSmsText
    {
        get => _incomingSmsText;
        set => SetProperty(ref _incomingSmsText, value ?? string.Empty);
    }

    public string RssiRawSample
    {
        get => _rssiRawSample;
        set => SetProperty(ref _rssiRawSample, value ?? string.Empty);
    }

    public bool AutoAnswer
    {
        get => _autoAnswer;
        set => SetProperty(ref _autoAnswer, value);
    }

    public string Result
    {
        get => _result;
        private set => SetProperty(ref _result, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                QueueIncomingCallCommand.NotifyCanExecuteChanged();
                QueueIncomingSmsCommand.NotifyCanExecuteChanged();
                SetRssiCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public IRelayCommand QueueIncomingCallCommand { get; }
    public IRelayCommand QueueIncomingSmsCommand { get; }
    public IRelayCommand SetRssiCommand { get; }

    public void MarkReady() => Result = "GSM ready · waiting for native registration";

    void QueueIncomingCall() => Run(
        "Queue incoming call",
        () =>
        {
            _session.TryQueueIncomingCall(
                IncomingNumber, AutoAnswer, out string result);
            return result;
        });

    void QueueIncomingSms() => Run(
        "Queue incoming SMS",
        () =>
        {
            _session.TryQueueIncomingSms(
                IncomingNumber, IncomingSmsText, out string result);
            return result;
        });

    void SetRssi()
    {
        if (!int.TryParse(RssiRawSample, out int sample))
        {
            Result = "RSSI raw sample must be a decimal integer.";
            return;
        }

        Run(
            "Set RSSI",
            () =>
            {
                _session.TrySetCarrierRawSample(sample, out string result);
                return result;
            });
    }

    void Run(string operation, Func<string> action)
    {
        try
        {
            Result = action();
        }
        catch (Exception error) when (MainViewExceptions.IsRecoverable(error))
        {
            Result = $"{operation} failed · {error.Message}";
            _host.ReportError(operation, error);
        }
    }

    void OnMessageEmitted(object? sender, MainViewTextEventArgs eventArgs) =>
        _host.Post(() => Result = eventArgs.Text);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            _session.GsmMessageEmitted -= OnMessageEmitted;
        }
    }
}
