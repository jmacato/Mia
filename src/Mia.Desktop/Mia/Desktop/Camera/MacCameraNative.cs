// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace Mia.Desktop.Camera;

internal static class MacCameraNative
{
    internal const uint PixelFormat32Bgra = 0x42475241;

    const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    const string AVFoundation =
        "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
    const string CoreMedia =
        "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    const string CoreVideo =
        "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    const string Dispatch = "/usr/lib/system/libdispatch.dylib";

    static readonly Lazy<IntPtr> AVFoundationHandle = new(() =>
        NativeLibrary.Load(AVFoundation));
    static readonly Lazy<IntPtr> CoreVideoHandle = new(() =>
        NativeLibrary.Load(CoreVideo));

    internal static IntPtr MediaTypeVideo => ReadObjectExport(
        AVFoundationHandle.Value,
        "AVMediaTypeVideo");

    internal static IntPtr CaptureSessionPreset640x480 => ReadObjectExport(
        AVFoundationHandle.Value,
        "AVCaptureSessionPreset640x480");

    internal static IntPtr PixelBufferPixelFormatTypeKey => ReadObjectExport(
        CoreVideoHandle.Value,
        "kCVPixelBufferPixelFormatTypeKey");

    internal static IntPtr PixelBufferWidthKey => ReadObjectExport(
        CoreVideoHandle.Value,
        "kCVPixelBufferWidthKey");

    internal static IntPtr PixelBufferHeightKey => ReadObjectExport(
        CoreVideoHandle.Value,
        "kCVPixelBufferHeightKey");

    internal static IntPtr ConcreteGlobalBlock => NativeLibrary.GetExport(
        NativeLibrary.Load(ObjectiveC),
        "_NSConcreteGlobalBlock");

    [DllImport(ObjectiveC, CallingConvention = CallingConvention.Cdecl,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern IntPtr objc_getClass(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveC, CallingConvention = CallingConvention.Cdecl,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern IntPtr sel_registerName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveC, CallingConvention = CallingConvention.Cdecl,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern IntPtr objc_allocateClassPair(
        IntPtr superclass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        nuint extraBytes);

    [DllImport(ObjectiveC, CallingConvention = CallingConvention.Cdecl,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern void objc_registerClassPair(IntPtr value);

    [DllImport(ObjectiveC, CallingConvention = CallingConvention.Cdecl,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool class_addMethod(
        IntPtr value,
        IntPtr selector,
        IntPtr implementation,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport(ObjectiveC, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void objc_release(IntPtr value);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SendIntPtrIntPtr(
        IntPtr receiver,
        IntPtr selector,
        IntPtr argument);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SendIntPtrIntPtrOutIntPtr(
        IntPtr receiver,
        IntPtr selector,
        IntPtr argument,
        out IntPtr error);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SendIntPtrUInt32(
        IntPtr receiver,
        IntPtr selector,
        uint argument);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SendIntPtrIntPtrIntPtr(
        IntPtr receiver,
        IntPtr selector,
        IntPtr firstArgument,
        IntPtr secondArgument);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint SendNIntIntPtr(
        IntPtr receiver,
        IntPtr selector,
        IntPtr argument);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SendBoolIntPtr(
        IntPtr receiver,
        IntPtr selector,
        IntPtr argument);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SendVoid(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SendVoidIntPtr(
        IntPtr receiver,
        IntPtr selector,
        IntPtr argument);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SendVoidByte(
        IntPtr receiver,
        IntPtr selector,
        byte argument);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SendVoidIntPtrIntPtr(
        IntPtr receiver,
        IntPtr selector,
        IntPtr firstArgument,
        IntPtr secondArgument);

    [DllImport(CoreMedia, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr CMSampleBufferGetImageBuffer(
        IntPtr sampleBuffer);

    [DllImport(CoreVideo, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CVPixelBufferLockBaseAddress(
        IntPtr pixelBuffer,
        ulong lockFlags);

    [DllImport(CoreVideo, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CVPixelBufferUnlockBaseAddress(
        IntPtr pixelBuffer,
        ulong unlockFlags);

    [DllImport(CoreVideo, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr CVPixelBufferGetBaseAddress(IntPtr pixelBuffer);

    [DllImport(CoreVideo, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nuint CVPixelBufferGetWidth(IntPtr pixelBuffer);

    [DllImport(CoreVideo, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nuint CVPixelBufferGetHeight(IntPtr pixelBuffer);

    [DllImport(CoreVideo, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nuint CVPixelBufferGetBytesPerRow(IntPtr pixelBuffer);

    [DllImport(CoreVideo, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint CVPixelBufferGetPixelFormatType(IntPtr pixelBuffer);

    [DllImport(Dispatch, CallingConvention = CallingConvention.Cdecl,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern IntPtr dispatch_queue_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
        IntPtr attributes);

    [DllImport(Dispatch, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void dispatch_release(IntPtr queue);

    static IntPtr ReadObjectExport(IntPtr library, string name) =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
}
