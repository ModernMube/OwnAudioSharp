using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// Hands the Rust side the JVM pointer and the app Context on Android.
/// </summary>
/// <remarks>
/// AAudio playback itself is pure NDK and needs none of this. Listing devices does: cpal goes
/// through AudioManager.getDevices() over JNI and reads the handles from ndk_context, which is a
/// global that crates owning main (ndk-glue) normally fill in. We get dlopen'd through P/Invoke,
/// so nobody fills it in and the engine falls back to reporting the default device only.
/// Declares its own LibraryImport rather than using OwnAudioNative, so the loader can call this
/// from EnsureRegistered without re-entering that class's static constructor.
/// </remarks>
internal static partial class AndroidJniBootstrap
{
    private static bool _done;

    [LibraryImport(NativeLibraryLoader.LogicalName)]
    private static partial int ownaudio_v1_android_init(IntPtr javaVm, IntPtr context);

    /// <summary>
    /// Passes the pointers over, once per process. A failure here is not fatal — it only costs the
    /// real device list — so it stays quiet rather than taking initialization down with it.
    /// </summary>
    internal static void EnsureInitialized()
    {
        if (_done || !OperatingSystem.IsAndroid()) { return; }

        _done = true;

#if ANDROID
        try
        {
            IntPtr vm = Java.Interop.JniEnvironment.Runtime.InvocationPointer;
            IntPtr context = Android.App.Application.Context?.Handle ?? IntPtr.Zero;

            if (vm == IntPtr.Zero || context == IntPtr.Zero) { return; }

            //A local ref would go stale the moment the calling JNI frame unwinds
            ownaudio_v1_android_init(vm, Android.Runtime.JNIEnv.NewGlobalRef(context));
        }
        catch (Exception)
        {
        }
#endif
    }
}
