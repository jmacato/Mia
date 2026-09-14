// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Mia.App.Controls;
using Mia.App.Presentation;

namespace Mia.App.Views;

public sealed partial class MainView : UserControl, IAsyncDisposable
{
    readonly MiaPhoneFace _phoneFace;
    MainViewModel? _viewModel;
    bool _isAttached;
    bool _started;
    int _disposeStarted;

    public MainView()
    {
        InitializeComponent();
        _phoneFace = this.FindControl<MiaPhoneFace>("PhoneFace")!;

        _phoneFace.ContactChanged += SetContact;
        DataContextChanged += HandleDataContextChanged;
        SizeChanged += HandleSizeChanged;
        AddHandler(
            InputElement.KeyDownEvent,
            HandleKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            InputElement.KeyUpEvent,
            HandleKeyUp,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AttachedToVisualTree += HandleAttachedToVisualTree;
        DetachedFromVisualTree += HandleDetachedFromVisualTree;
        LostFocus += HandleLostFocus;
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    void HandleDataContextChanged(object? sender, EventArgs eventArgs)
    {
        DetachViewModel();
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;
        _viewModel.FrameReady += PresentFrame;
        _viewModel.PhoneLedsChanged += PresentPhoneLeds;
        _viewModel.FocusRequested += OnFocusRequested;
        _phoneFace.InputEnabled = _viewModel.IsInputEnabled;
        StartIfReady();
    }

    void HandleSizeChanged(object? sender, SizeChangedEventArgs eventArgs) =>
        SelectResponsiveLayout(eventArgs.NewSize.Width, eventArgs.NewSize.Height);

    void HandleAttachedToVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs eventArgs)
    {
        _isAttached = true;
        StartIfReady();

        FocusView();
        SelectResponsiveLayout(Bounds.Width, Bounds.Height);
    }

    async void HandleDetachedFromVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs eventArgs)
    {
        _isAttached = false;
        MainViewModel? viewModel = _viewModel;
        _phoneFace.ReleaseAllInput();
        try
        {
            await DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception error) when (MainViewExceptions.IsRecoverable(error))
        {
            viewModel?.ReportHostFailure("View shutdown", error);
        }
    }

    void HandleLostFocus(object? sender, RoutedEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsKeyboardFocusWithin)
            {
                _phoneFace.ReleaseAllInput();
            }
        });

    void StartIfReady()
    {
        if (!_isAttached || _started || _viewModel is not { AutoStart: true } viewModel)
        {
            return;
        }

        _started = true;
        _ = DispatcherTimer.RunOnce(
            () => _ = viewModel.BootCommand.ExecuteAsync(null),
            TimeSpan.FromMilliseconds(100),
            DispatcherPriority.Background);
    }

    void SelectResponsiveLayout(double width, double height)
    {
        width = width > 0 ? width : 390;
        height = height > 0 ? height : 844;
        PseudoClasses.Set(":compact", width < 600 || height < 720);
    }

    void PresentFrame(object? sender, MainViewFrameEventArgs eventArgs) =>
        _phoneFace.Lcd.UpdateFrame(eventArgs.Frame.Span);

    void PresentPhoneLeds(object? sender, MainViewPhoneLedEventArgs eventArgs) =>
        _phoneFace.SetLeds(eventArgs);

    void FocusView() => Focus();

    void OnFocusRequested(object? sender, EventArgs eventArgs) => FocusView();

    void HandleKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Source is TextBox || !_phoneFace.InputEnabled ||
            !MiaPhoneKeys.TryMapKeyboard(
                eventArgs.Key,
                eventArgs.KeyModifiers,
                out MiaPhoneKey phoneKey))
        {
            return;
        }

        eventArgs.Handled = true;
        _phoneFace.PressKeyboard((int)eventArgs.Key, phoneKey);
    }

    void HandleKeyUp(object? sender, KeyEventArgs eventArgs)
    {
        if (_phoneFace.ReleaseKeyboard((int)eventArgs.Key))
        {
            eventArgs.Handled = true;
        }
    }

    void SetContact(MiaPhoneKeypadContact contact, bool pressed)
    {
        if (contact.Power)
        {
            _viewModel?.SetPowerKey(pressed);
            return;
        }

        _viewModel?.SetKey(
            contact.ScanMask,
            contact.RowMask,
            contact.SecondaryScanMask,
            pressed);
    }

    void DetachViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.FrameReady -= PresentFrame;
        _viewModel.PhoneLedsChanged -= PresentPhoneLeds;
        _viewModel.FocusRequested -= OnFocusRequested;
        _viewModel = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        MainViewModel? viewModel = _viewModel;
        DetachViewModel();
        _phoneFace.ContactChanged -= SetContact;
        if (viewModel is not null)
        {
            await viewModel.DisposeAsync().ConfigureAwait(true);
        }
    }
}
