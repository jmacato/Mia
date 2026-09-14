// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Mia.Desktop.Camera;

/// <summary>
/// macOS camera source implemented directly over AVFoundation, CoreMedia, and
/// CoreVideo. Native frame memory is copied through <see cref="Marshal.Copy"/>
/// so the backend stays within safe managed code.
/// </summary>
internal sealed class MacAvFoundationCameraFrameSource : IMiaCameraFrameSource
{
    const long FramePublicationPeriodMilliseconds =
        1_000 / MiaCommuniCamImageProfile.MonitoringFramesPerSecond;

    const int GlobalBlockFlag = 1 << 28;

    static readonly ConcurrentDictionary<IntPtr, MacAvFoundationCameraFrameSource>
        Instances = new();
    static readonly MacSampleBufferCallback SampleBufferHandler = OnSampleBuffer;
    static readonly MacAuthorizationCallback AuthorizationHandler = OnAuthorization;
    static readonly Lazy<IntPtr> SampleBufferDelegateClass = new(
        CreateSampleBufferDelegateClass);
    static readonly Lazy<IntPtr> AuthorizationBlock = new(
        CreateAuthorizationBlock);
    static TaskCompletionSource<bool>? _authorizationCompletion;

    MiaCameraFrame? _latestFrame;
    TaskCompletionSource<MiaCameraFrame>? _firstFrame;
    IntPtr _captureSession;
    IntPtr _captureInput;
    IntPtr _captureOutput;
    IntPtr _sampleBufferDelegate;
    IntPtr _captureQueue;
    long _nextFramePublicationTick;
    int _running;
    int _starting;
    bool _disposed;

    public string DisplayName => "Mac camera";

    public bool IsAvailable => OperatingSystem.IsMacOS();

    public string? UnavailableReason => IsAvailable
        ? null
        : "AVFoundation is available only on macOS.";

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(UnavailableReason);
        }
        if (Volatile.Read(ref _running) != 0)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
        {
            throw new InvalidOperationException("The Mac camera is already starting.");
        }

        try
        {
            await EnsureAuthorizedAsync(cancellationToken).ConfigureAwait(false);
            CreateCaptureSession();
            _firstFrame = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _running, 1);
            await Task.Run(
                () => MacCameraNative.SendVoid(
                    _captureSession,
                    Selector("startRunning")),
                cancellationToken).ConfigureAwait(false);
            await _firstFrame.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            Volatile.Write(ref _starting, 0);
        }
    }

    public async ValueTask StopAsync()
    {
        bool wasRunning = Interlocked.Exchange(ref _running, 0) != 0;
        IntPtr session = _captureSession;
        if (wasRunning && session != IntPtr.Zero)
        {
            await Task.Run(() => MacCameraNative.SendVoid(
                session,
                Selector("stopRunning"))).ConfigureAwait(false);
        }
        ReleaseCaptureSession();
        Volatile.Write(ref _latestFrame, null);
        Volatile.Write(ref _nextFramePublicationTick, 0);
        _firstFrame = null;
    }

    public bool TryGetLatestFrame(out MiaCameraFrame? frame)
    {
        frame = Volatile.Read(ref _latestFrame);
        return frame is not null && Volatile.Read(ref _running) != 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
    }

    void CreateCaptureSession()
    {
        IntPtr mediaType = MacCameraNative.MediaTypeVideo;
        IntPtr deviceClass = GetClass("AVCaptureDevice");
        IntPtr device = RequireHandle(
            MacCameraNative.SendIntPtrIntPtr(
                deviceClass,
                Selector("defaultDeviceWithMediaType:"),
                mediaType),
            "macOS did not report a video capture device.");

        IntPtr inputClass = GetClass("AVCaptureDeviceInput");
        IntPtr inputAllocation = MacCameraNative.SendIntPtr(
            inputClass,
            Selector("alloc"));
        _captureInput = MacCameraNative.SendIntPtrIntPtrOutIntPtr(
            inputAllocation,
            Selector("initWithDevice:error:"),
            device,
            out IntPtr error);
        RequireCameraInput(_captureInput, error);

        _captureSession = AllocateAndInitialize("AVCaptureSession");
        ConfigureCaptureSession(_captureSession);
        _captureOutput = AllocateAndInitialize("AVCaptureVideoDataOutput");
        ConfigureVideoOutput(_captureOutput);
        AddCaptureComponent(_captureSession, _captureInput, "Input");
        AddCaptureComponent(_captureSession, _captureOutput, "Output");

        _captureQueue = RequireHandle(
            MacCameraNative.dispatch_queue_create(
                "dev.mia.communicam.camera",
                IntPtr.Zero),
            "Could not create the Mac camera queue.");
        _sampleBufferDelegate = RequireHandle(
            MacCameraNative.SendIntPtr(
                SampleBufferDelegateClass.Value,
                Selector("new")),
            "Could not create the Mac camera delegate.");
        Instances[_sampleBufferDelegate] = this;
        MacCameraNative.SendVoidIntPtrIntPtr(
            _captureOutput,
            Selector("setSampleBufferDelegate:queue:"),
            _sampleBufferDelegate,
            _captureQueue);
    }

    static IntPtr RequireHandle(IntPtr value, string failureMessage)
    {
        if (value == IntPtr.Zero)
        {
            throw new InvalidOperationException(failureMessage);
        }
        return value;
    }

    static void RequireCameraInput(IntPtr input, IntPtr error)
    {
        if (input == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Could not open the Mac camera: " + GetNativeError(error));
        }
    }

    static void ConfigureCaptureSession(IntPtr session)
    {
        IntPtr preset = MacCameraNative.CaptureSessionPreset640x480;
        if (MacCameraNative.SendBoolIntPtr(
                session,
                Selector("canSetSessionPreset:"),
                preset))
        {
            MacCameraNative.SendVoidIntPtr(
                session,
                Selector("setSessionPreset:"),
                preset);
        }
    }

    static void ConfigureVideoOutput(IntPtr output)
    {
        IntPtr numberClass = GetClass("NSNumber");
        IntPtr pixelFormat = MacCameraNative.SendIntPtrUInt32(
            numberClass,
            Selector("numberWithUnsignedInt:"),
            MacCameraNative.PixelFormat32Bgra);
        IntPtr width = MacCameraNative.SendIntPtrUInt32(
            numberClass,
            Selector("numberWithUnsignedInt:"),
            MiaCommuniCamImageProfile.SensorWidth);
        IntPtr height = MacCameraNative.SendIntPtrUInt32(
            numberClass,
            Selector("numberWithUnsignedInt:"),
            MiaCommuniCamImageProfile.SensorHeight);
        IntPtr settings = AllocateAndInitialize("NSMutableDictionary");
        try
        {
            IntPtr setObject = Selector("setObject:forKey:");
            MacCameraNative.SendVoidIntPtrIntPtr(
                settings,
                setObject,
                pixelFormat,
                MacCameraNative.PixelBufferPixelFormatTypeKey);
            MacCameraNative.SendVoidIntPtrIntPtr(
                settings,
                setObject,
                width,
                MacCameraNative.PixelBufferWidthKey);
            MacCameraNative.SendVoidIntPtrIntPtr(
                settings,
                setObject,
                height,
                MacCameraNative.PixelBufferHeightKey);
            MacCameraNative.SendVoidIntPtr(
                output,
                Selector("setVideoSettings:"),
                settings);
        }
        finally
        {
            MacCameraNative.objc_release(settings);
        }
        MacCameraNative.SendVoidByte(
            output,
            Selector("setAlwaysDiscardsLateVideoFrames:"),
            1);
    }

    static void AddCaptureComponent(IntPtr session, IntPtr component, string kind)
    {
        IntPtr canAddSelector = Selector($"canAdd{kind}:");
        if (!MacCameraNative.SendBoolIntPtr(session, canAddSelector, component))
        {
            throw new InvalidOperationException(
                $"AVFoundation refused the selected camera {kind}.");
        }
        MacCameraNative.SendVoidIntPtr(
            session,
            Selector($"add{kind}:"),
            component);
    }

    void ReleaseCaptureSession()
    {
        IntPtr output = _captureOutput;
        IntPtr sampleBufferDelegate = _sampleBufferDelegate;
        if (output != IntPtr.Zero && sampleBufferDelegate != IntPtr.Zero)
        {
            MacCameraNative.SendVoidIntPtrIntPtr(
                output,
                Selector("setSampleBufferDelegate:queue:"),
                IntPtr.Zero,
                IntPtr.Zero);
        }
        if (sampleBufferDelegate != IntPtr.Zero)
        {
            Instances.TryRemove(sampleBufferDelegate, out _);
            MacCameraNative.objc_release(sampleBufferDelegate);
        }
        if (_captureQueue != IntPtr.Zero)
        {
            MacCameraNative.dispatch_release(_captureQueue);
        }
        ReleaseNativeObject(_captureOutput);
        ReleaseNativeObject(_captureInput);
        ReleaseNativeObject(_captureSession);
        _sampleBufferDelegate = IntPtr.Zero;
        _captureQueue = IntPtr.Zero;
        _captureOutput = IntPtr.Zero;
        _captureInput = IntPtr.Zero;
        _captureSession = IntPtr.Zero;
    }

    void ProcessSampleBuffer(IntPtr sampleBuffer)
    {
        if (Volatile.Read(ref _running) == 0)
        {
            return;
        }
        long captureTick = Environment.TickCount64;
        if (captureTick < Volatile.Read(ref _nextFramePublicationTick))
        {
            return;
        }
        IntPtr pixelBuffer = MacCameraNative.CMSampleBufferGetImageBuffer(sampleBuffer);
        if (pixelBuffer == IntPtr.Zero ||
            MacCameraNative.CVPixelBufferGetPixelFormatType(pixelBuffer) !=
                MacCameraNative.PixelFormat32Bgra ||
            MacCameraNative.CVPixelBufferLockBaseAddress(pixelBuffer, 1) != 0)
        {
            return;
        }

        try
        {
            MiaCameraFrame frame = CopyFrame(pixelBuffer);
            Volatile.Write(ref _latestFrame, frame);
            Volatile.Write(
                ref _nextFramePublicationTick,
                captureTick + FramePublicationPeriodMilliseconds);
            _firstFrame?.TrySetResult(frame);
        }
        finally
        {
            _ = MacCameraNative.CVPixelBufferUnlockBaseAddress(pixelBuffer, 1);
        }
    }

    static MiaCameraFrame CopyFrame(IntPtr pixelBuffer)
    {
        int width = checked((int)MacCameraNative.CVPixelBufferGetWidth(pixelBuffer));
        int height = checked((int)MacCameraNative.CVPixelBufferGetHeight(pixelBuffer));
        int stride = checked((int)MacCameraNative.CVPixelBufferGetBytesPerRow(pixelBuffer));
        IntPtr baseAddress = MacCameraNative.CVPixelBufferGetBaseAddress(pixelBuffer);
        if (width <= 0 || height <= 0 || stride < width * 4 || baseAddress == IntPtr.Zero)
        {
            throw new InvalidOperationException("AVFoundation returned an invalid BGRA frame.");
        }

        var pixels = new byte[checked(stride * height)];
        Marshal.Copy(baseAddress, pixels, 0, pixels.Length);
        return MiaCameraFrame.Create(
            width,
            height,
            stride,
            MiaCameraPixelFormat.Bgra32,
            pixels);
    }

    static async Task EnsureAuthorizedAsync(CancellationToken cancellationToken)
    {
        IntPtr mediaType = MacCameraNative.MediaTypeVideo;
        IntPtr deviceClass = GetClass("AVCaptureDevice");
        nint status = MacCameraNative.SendNIntIntPtr(
            deviceClass,
            Selector("authorizationStatusForMediaType:"),
            mediaType);
        switch (status)
        {
            case 3:
                return;
            case 1 or 2:
                throw new UnauthorizedAccessException(
                    "Camera access is disabled in macOS Privacy & Security settings.");
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(
                ref _authorizationCompletion,
                completion,
                null) is not null)
        {
            throw new InvalidOperationException(
                "Another Mac camera authorization request is already active.");
        }
        try
        {
            MacCameraNative.SendVoidIntPtrIntPtr(
                deviceClass,
                Selector("requestAccessForMediaType:completionHandler:"),
                mediaType,
                AuthorizationBlock.Value);
            bool granted = await completion.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            ThrowIfAuthorizationDenied(granted);
        }
        finally
        {
            _ = Interlocked.CompareExchange(
                ref _authorizationCompletion,
                null,
                completion);
        }
    }

    static void ThrowIfAuthorizationDenied(bool granted)
    {
        if (!granted)
        {
            throw new UnauthorizedAccessException(
                "Camera access was not granted in macOS Privacy & Security settings.");
        }
    }

    static IntPtr CreateSampleBufferDelegateClass()
    {
        const string className = "MiaMacCameraSampleBufferDelegate";
        IntPtr existing = MacCameraNative.objc_getClass(className);
        if (existing != IntPtr.Zero)
        {
            return existing;
        }
        IntPtr value = MacCameraNative.objc_allocateClassPair(
            GetClass("NSObject"),
            className,
            0);
        if (value == IntPtr.Zero || !MacCameraNative.class_addMethod(
                value,
                Selector("captureOutput:didOutputSampleBuffer:fromConnection:"),
                Marshal.GetFunctionPointerForDelegate(SampleBufferHandler),
                "v@:@@@"))
        {
            throw new InvalidOperationException(
                "Could not register the AVFoundation sample-buffer delegate.");
        }
        MacCameraNative.objc_registerClassPair(value);
        return value;
    }

    static IntPtr CreateAuthorizationBlock()
    {
        IntPtr descriptor = Marshal.AllocHGlobal(2 * IntPtr.Size);
        Marshal.WriteIntPtr(descriptor, IntPtr.Zero);
        Marshal.WriteIntPtr(
            descriptor,
            IntPtr.Size,
            (IntPtr)Marshal.SizeOf<MacBlockLiteral>());
        var literal = new MacBlockLiteral
        {
            Isa = MacCameraNative.ConcreteGlobalBlock,
            Flags = GlobalBlockFlag,
            Invoke = Marshal.GetFunctionPointerForDelegate(AuthorizationHandler),
            Descriptor = descriptor,
        };
        IntPtr block = Marshal.AllocHGlobal(Marshal.SizeOf<MacBlockLiteral>());
        Marshal.StructureToPtr(literal, block, false);
        return block;
    }

    static void OnSampleBuffer(
        IntPtr receiver,
        IntPtr selector,
        IntPtr output,
        IntPtr sampleBuffer,
        IntPtr connection)
    {
        if (!Instances.TryGetValue(receiver, out var source))
        {
            return;
        }
        try
        {
            source.ProcessSampleBuffer(sampleBuffer);
        }
        catch (Exception error) when (
            error is InvalidOperationException or
                ArgumentException or
                OverflowException)
        {
            source._firstFrame?.TrySetException(error);
        }
    }

    static void OnAuthorization(IntPtr block, bool granted) =>
        Interlocked.Exchange(ref _authorizationCompletion, null)
            ?.TrySetResult(granted);

    static IntPtr AllocateAndInitialize(string className)
    {
        IntPtr allocation = MacCameraNative.SendIntPtr(
            GetClass(className),
            Selector("alloc"));
        IntPtr value = MacCameraNative.SendIntPtr(allocation, Selector("init"));
        if (value == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not initialize {className}.");
        }
        return value;
    }

    static IntPtr GetClass(string name)
    {
        IntPtr value = MacCameraNative.objc_getClass(name);
        return value != IntPtr.Zero
            ? value
            : throw new InvalidOperationException($"Objective-C class {name} is unavailable.");
    }

    static IntPtr Selector(string name) => MacCameraNative.sel_registerName(name);

    static string GetNativeError(IntPtr error)
    {
        if (error == IntPtr.Zero)
        {
            return "AVFoundation returned no error description.";
        }
        IntPtr description = MacCameraNative.SendIntPtr(
            error,
            Selector("localizedDescription"));
        IntPtr utf8 = MacCameraNative.SendIntPtr(
            description,
            Selector("UTF8String"));
        return Marshal.PtrToStringUTF8(utf8) ??
            "AVFoundation returned an unreadable error description.";
    }

    static void ReleaseNativeObject(IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            MacCameraNative.objc_release(value);
        }
    }
}
