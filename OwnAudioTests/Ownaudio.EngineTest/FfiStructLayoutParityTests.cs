using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Native.RustAudio.Structs;

namespace Ownaudio.EngineTest;

/// <summary>
/// Verifies that every C# type marshalled across the Rust FFI boundary has the
/// exact same memory layout as its <c>#[repr(C)]</c> counterpart in
/// <c>ownaudio-ffi</c>.
/// </summary>
/// <remarks>
/// <para>
/// The hardcoded sizes, offsets and discriminant values here are deliberately
/// identical to the assertions in the Rust <c>tests/layout.rs</c> suite, which
/// is the authoritative source of truth. If the native struct layout ever
/// changes, one of the two suites fails the build immediately, before a layout
/// mismatch can corrupt memory at the boundary.
/// </para>
/// <para>
/// Pointer-width dependent layouts (<see cref="NativeDeviceInfo"/>) are derived
/// from <see cref="IntPtr.Size"/> so the assertions hold on both 64-bit and
/// 32-bit runtimes.
/// </para>
/// </remarks>
[TestClass]
public class FfiStructLayoutParityTests
{
    /// <summary>Native pointer width in bytes on the current runtime (8 on 64-bit, 4 on 32-bit).</summary>
    private static readonly int Ptr = IntPtr.Size;

    [TestMethod]
    public void NativeStreamConfig_SizeIs16()
    {
        Assert.AreEqual(16, Marshal.SizeOf<NativeStreamConfig>(), "NativeStreamConfig size");
    }

    [TestMethod]
    public void NativeStreamConfig_FieldOffsets()
    {
        Assert.AreEqual(0, (int)Marshal.OffsetOf<NativeStreamConfig>(nameof(NativeStreamConfig.SampleRate)), "SampleRate");
        Assert.AreEqual(4, (int)Marshal.OffsetOf<NativeStreamConfig>(nameof(NativeStreamConfig.Channels)), "Channels");
        Assert.AreEqual(8, (int)Marshal.OffsetOf<NativeStreamConfig>(nameof(NativeStreamConfig.SampleFormat)), "SampleFormat");
        Assert.AreEqual(12, (int)Marshal.OffsetOf<NativeStreamConfig>(nameof(NativeStreamConfig.BufferSizeFrames)), "BufferSizeFrames");
    }

    [TestMethod]
    public void NativeAudioStreamInfo_SizeIs24()
    {
        Assert.AreEqual(24, Marshal.SizeOf<NativeAudioStreamInfo>(), "NativeAudioStreamInfo size");
    }

    [TestMethod]
    public void NativeAudioStreamInfo_FieldOffsets()
    {
        Assert.AreEqual(0, (int)Marshal.OffsetOf<NativeAudioStreamInfo>(nameof(NativeAudioStreamInfo.Channels)), "Channels");
        Assert.AreEqual(4, (int)Marshal.OffsetOf<NativeAudioStreamInfo>(nameof(NativeAudioStreamInfo.SampleRate)), "SampleRate");
        Assert.AreEqual(8, (int)Marshal.OffsetOf<NativeAudioStreamInfo>(nameof(NativeAudioStreamInfo.DurationMs)), "DurationMs");
        Assert.AreEqual(16, (int)Marshal.OffsetOf<NativeAudioStreamInfo>(nameof(NativeAudioStreamInfo.BitDepth)), "BitDepth");
    }

    [TestMethod]
    public void NativeDeviceInfo_SizeMatchesPointerWidth()
    {
        int expected = Ptr == 8 ? 24 : 16;
        Assert.AreEqual(expected, Marshal.SizeOf<NativeDeviceInfo>(), "NativeDeviceInfo size");
    }

    [TestMethod]
    public void NativeDeviceInfo_FieldOffsets()
    {
        Assert.AreEqual(0, (int)Marshal.OffsetOf<NativeDeviceInfo>(nameof(NativeDeviceInfo.Name)), "Name");
        Assert.AreEqual(Ptr, (int)Marshal.OffsetOf<NativeDeviceInfo>(nameof(NativeDeviceInfo.IsDefaultInput)), "IsDefaultInput");
        Assert.AreEqual(Ptr + 1, (int)Marshal.OffsetOf<NativeDeviceInfo>(nameof(NativeDeviceInfo.IsDefaultOutput)), "IsDefaultOutput");
        Assert.AreEqual(Ptr + 2, (int)Marshal.OffsetOf<NativeDeviceInfo>(nameof(NativeDeviceInfo.MaxInputChannels)), "MaxInputChannels");
        Assert.AreEqual(Ptr + 4, (int)Marshal.OffsetOf<NativeDeviceInfo>(nameof(NativeDeviceInfo.MaxOutputChannels)), "MaxOutputChannels");
        Assert.AreEqual(Ptr + 8, (int)Marshal.OffsetOf<NativeDeviceInfo>(nameof(NativeDeviceInfo.DefaultSampleRate)), "DefaultSampleRate");
    }

    [TestMethod]
    public void NativeSampleFormat_IsCIntWide()
    {
        Assert.AreEqual(4, Marshal.SizeOf(Enum.GetUnderlyingType(typeof(NativeSampleFormat))), "NativeSampleFormat underlying width");
    }

    [TestMethod]
    public void NativeErrorCode_IsCIntWide()
    {
        Assert.AreEqual(4, Marshal.SizeOf(Enum.GetUnderlyingType(typeof(NativeErrorCode))), "NativeErrorCode underlying width");
    }

    [TestMethod]
    public void NativeHostApi_IsCIntWide()
    {
        Assert.AreEqual(4, Marshal.SizeOf(Enum.GetUnderlyingType(typeof(NativeHostApi))), "NativeHostApi underlying width");
    }

#pragma warning disable MSTEST0032
    [TestMethod]
    public void NativeSampleFormat_Discriminants()
    {
        Assert.AreEqual(0, (int)NativeSampleFormat.F32);
        Assert.AreEqual(1, (int)NativeSampleFormat.I16);
        Assert.AreEqual(2, (int)NativeSampleFormat.U16);
    }

    [TestMethod]
    public void NativeErrorCode_Discriminants()
    {
        Assert.AreEqual(0, (int)NativeErrorCode.Success);
        Assert.AreEqual(1, (int)NativeErrorCode.DeviceNotFound);
        Assert.AreEqual(2, (int)NativeErrorCode.DeviceEnumerationFailed);
        Assert.AreEqual(3, (int)NativeErrorCode.UnsupportedConfig);
        Assert.AreEqual(4, (int)NativeErrorCode.StreamBuildFailed);
        Assert.AreEqual(5, (int)NativeErrorCode.StreamControlFailed);
        Assert.AreEqual(6, (int)NativeErrorCode.NullPointer);
        Assert.AreEqual(7, (int)NativeErrorCode.InvalidHandle);
        Assert.AreEqual(8, (int)NativeErrorCode.InternalPanic);
        Assert.AreEqual(9, (int)NativeErrorCode.InternalError);
        Assert.AreEqual(10, (int)NativeErrorCode.HostApiNotAvailable);
        Assert.AreEqual(11, (int)NativeErrorCode.AsioDriverNotFound);
        Assert.AreEqual(12, (int)NativeErrorCode.DecoderOpenFailed);
        Assert.AreEqual(13, (int)NativeErrorCode.DecoderUnsupportedFormat);
        Assert.AreEqual(14, (int)NativeErrorCode.DecoderReadFailed);
        Assert.AreEqual(15, (int)NativeErrorCode.DecoderSeekFailed);
    }

    [TestMethod]
    public void NativeHostApi_Discriminants()
    {
        Assert.AreEqual(0, (int)NativeHostApi.Wasapi);
        Assert.AreEqual(1, (int)NativeHostApi.Asio);
        Assert.AreEqual(2, (int)NativeHostApi.CoreAudio);
        Assert.AreEqual(3, (int)NativeHostApi.Alsa);
    }
#pragma warning restore MSTEST0032
}
