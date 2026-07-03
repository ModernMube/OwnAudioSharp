using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Native.MiniAudio;
using System.Runtime.InteropServices;

namespace Ownaudio.EngineTest
{
    /// <summary>
    /// Guards the managed MaBinding struct mirrors against drift from the native miniaudio ABI.
    ///
    /// Expected values below are ground truth for miniaudio 0.11.24 on 64-bit platforms, produced
    /// with a C program printing offsetof()/sizeof() against the official miniaudio.h (they are
    /// identical on arm64 and x64 — the structs contain only 4-byte scalars and pointers).
    ///
    /// This matters because ma_device_config is built managed-side and blitted with
    /// Marshal.StructureToPtr before ma_device_init reads it natively: any size mismatch in an
    /// embedded struct shifts every following field. A 16-byte overshoot in MaResamplerConfig
    /// (a leftover miniaudio 0.10 shape) previously displaced playback.pDeviceID and
    /// capture.pDeviceID, so explicit device selection was silently ignored and the default
    /// device always opened.
    /// </summary>
    [TestClass]
    public class MaBindingLayoutTests
    {
        [TestMethod]
        public void MaResamplerConfig_MatchesNativeLayout()
        {
            Assert.AreEqual(48, Marshal.SizeOf<MaBinding.MaResamplerConfig>());
            Assert.AreEqual(16, (int)Marshal.OffsetOf<MaBinding.MaResamplerConfig>("algorithm"));
            Assert.AreEqual(24, (int)Marshal.OffsetOf<MaBinding.MaResamplerConfig>("pBackendVTable"));
            Assert.AreEqual(32, (int)Marshal.OffsetOf<MaBinding.MaResamplerConfig>("pBackendUserData"));
            Assert.AreEqual(40, (int)Marshal.OffsetOf<MaBinding.MaResamplerConfig>("linear"));
        }

        [TestMethod]
        public void MaDeviceConfig_MatchesNativeLayout()
        {
            Assert.AreEqual(296, Marshal.SizeOf<MaBinding.MaDeviceConfig>());
            Assert.AreEqual(32, (int)Marshal.OffsetOf<MaBinding.MaDeviceConfig>("dataCallback"));
            Assert.AreEqual(56, (int)Marshal.OffsetOf<MaBinding.MaDeviceConfig>("pUserData"));
            Assert.AreEqual(64, (int)Marshal.OffsetOf<MaBinding.MaDeviceConfig>("resampling"));
            Assert.AreEqual(112, (int)Marshal.OffsetOf<MaBinding.MaDeviceConfig>("playback"));
            Assert.AreEqual(152, (int)Marshal.OffsetOf<MaBinding.MaDeviceConfig>("capture"));
            Assert.AreEqual(192, (int)Marshal.OffsetOf<MaBinding.MaDeviceConfig>("wasapi"));
            Assert.AreEqual(208, (int)Marshal.OffsetOf<MaBinding.MaDeviceConfig>("alsa"));
            Assert.AreEqual(224, (int)Marshal.OffsetOf<MaBinding.MaDeviceConfig>("pulse"));
            Assert.AreEqual(248, (int)Marshal.OffsetOf<MaBinding.MaDeviceConfig>("coreaudio"));
            Assert.AreEqual(252, (int)Marshal.OffsetOf<MaBinding.MaDeviceConfig>("opensl"));
            Assert.AreEqual(264, (int)Marshal.OffsetOf<MaBinding.MaDeviceConfig>("aaudio"));
        }

        [TestMethod]
        public void MaDevicePlaybackAndCaptureConfig_MatchNativeLayout()
        {
            Assert.AreEqual(40, Marshal.SizeOf<MaBinding.MaDevicePlaybackConfig>());
            Assert.AreEqual(40, Marshal.SizeOf<MaBinding.MaDeviceCaptureConfig>());
            Assert.AreEqual(0, (int)Marshal.OffsetOf<MaBinding.MaDeviceCaptureConfig>("pDeviceID"));
            Assert.AreEqual(8, (int)Marshal.OffsetOf<MaBinding.MaDeviceCaptureConfig>("format"));
            Assert.AreEqual(12, (int)Marshal.OffsetOf<MaBinding.MaDeviceCaptureConfig>("channels"));
            Assert.AreEqual(16, (int)Marshal.OffsetOf<MaBinding.MaDeviceCaptureConfig>("pChannelMap"));
        }

        [TestMethod]
        public void MaDeviceInfo_MatchesNativeLayout()
        {
            Assert.AreEqual(1544, Marshal.SizeOf<MaBinding.MaDeviceInfo>());
            Assert.AreEqual(256, (int)Marshal.OffsetOf<MaBinding.MaDeviceInfo>("nameBytes"));
            Assert.AreEqual(512, (int)Marshal.OffsetOf<MaBinding.MaDeviceInfo>("isDefault"));
        }

        [TestMethod]
        public void MaContext_BackendFieldMatchesNativeOffset()
        {
            // ma_context.backend sits after ma_backend_callbacks (13 function pointers = 104 bytes).
            Assert.AreEqual(104, (int)Marshal.OffsetOf<MaBinding.MaContext>("backend"));
            // The mirror only needs to be layout-accurate through 'backend', but must never be
            // SMALLER than the native ma_context (688 bytes) because allocate_context sizes with it.
            Assert.IsTrue(Marshal.SizeOf<MaBinding.MaContext>() >= 688);
        }
    }
}
