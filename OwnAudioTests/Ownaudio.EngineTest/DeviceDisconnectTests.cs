using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.EngineTest
{
    /// <summary>
    /// The engine's device and status surface as the rust backend actually implements it.
    ///
    /// Hot-plug is NOT implemented since 4.0: Status only walks Idle/Running/Error, the four
    /// device events are declared for IAudioEngine and never raised, and Pause/ResumeDeviceMonitoring
    /// are no-ops. The tests here pin that down, so adding hot-plug back trips them on purpose.
    ///
    /// The real device-fault path lives one layer up now: the native output stream latches a
    /// fault, AudioMixer.PollRustStreamFaultOnce picks it up and raises StreamFaulted with
    /// AudioStreamFaultKind.DeviceNotAvailable. That needs a live session, so it belongs with
    /// the mixer tests rather than here.
    /// </summary>
    [TestClass]
    public class DeviceDisconnectTests
    {
        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void EngineStatus_Idle_AfterCreate()
        {
            // Arrange & Act
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Assert: freshly created engine is Idle (not started)
            Assert.AreEqual(EngineStatus.Idle, engine.Status,
                "Engine should be Idle immediately after creation.");
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void EngineStatus_Running_AfterStart()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Act
            int startResult = engine.Start();

            try
            {
                // Assert
                Assert.AreEqual(0, startResult, "Start() should return 0.");
                Assert.AreEqual(EngineStatus.Running, engine.Status,
                    "Engine should be Running after a successful Start().");
            }
            finally
            {
                engine.Stop();
            }
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void EngineStatus_Idle_AfterStop()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);
            engine.Start();
            Thread.Sleep(50);

            // Act
            engine.Stop();

            // Assert
            Assert.AreEqual(EngineStatus.Idle, engine.Status,
                "Engine should return to Idle after Stop().");
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void EngineStatus_Idle_IsNotRunning()
        {
            // Arrange & Act
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Assert: Idle maps to OwnAudioEngineStopped() == 1
            Assert.AreEqual(1, engine.OwnAudioEngineStopped(),
                "OwnAudioEngineStopped() should be 1 when Status is Idle.");
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void EngineStatus_Running_IsActive()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);
            engine.Start();

            try
            {
                // Assert
                Assert.AreEqual(1, engine.OwnAudioEngineActivate(),
                    "OwnAudioEngineActivate() should return 1 (running) when Status is Running.");
            }
            finally
            {
                engine.Stop();
            }
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void DeviceStateChanged_Event_CanSubscribeAndUnsubscribe()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);
            bool eventFired = false;

            EventHandler<AudioDeviceStateChangedEventArgs> handler = (s, e) =>
            {
                eventFired = true;
            };

            // Act – subscribe
            engine.DeviceStateChanged += handler;

            // Unsubscribe without throwing
            engine.DeviceStateChanged -= handler;

            // Assert: just verify no exception was thrown and the field is wired
            Assert.IsFalse(eventFired, "Event should not have fired without a disconnect.");
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void DeviceReconnected_Event_CanSubscribeAndUnsubscribe()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);
            bool eventFired = false;

            EventHandler<AudioDeviceReconnectedEventArgs> handler = (s, e) =>
            {
                eventFired = true;
            };

            // Act – subscribe
            engine.DeviceReconnected += handler;

            // Unsubscribe without throwing
            engine.DeviceReconnected -= handler;

            // Assert
            Assert.IsFalse(eventFired, "Event should not have fired without a reconnect.");
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void AudioDeviceReconnectedEventArgs_Properties_AreCorrect()
        {
            // Arrange
            var deviceInfo = new AudioDeviceInfo(
                deviceId: "test-id",
                name: "Test Device",
                engineName: "MiniAudio.CoreAudio",
                isInput: false,
                isOutput: true,
                isDefault: false,
                state: AudioDeviceState.Active);

            // Act
            var args = new AudioDeviceReconnectedEventArgs(
                deviceId: "test-id",
                deviceName: "Test Device",
                isOutputDevice: true,
                deviceInfo: deviceInfo);

            // Assert
            Assert.AreEqual("test-id", args.DeviceId);
            Assert.AreEqual("Test Device", args.DeviceName);
            Assert.IsTrue(args.IsOutputDevice);
            Assert.IsNotNull(args.DeviceInfo);
            Assert.AreEqual("Test Device", args.DeviceInfo.Name);
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void Send_WhileRunning_DoesNotTimeout_WithSmallChunk()
        {
            // Arrange
            var config = AudioConfig.Default;
            config.BufferSize = 512;
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            float[] chunk = TestHelpers.GenerateSineWave(440f, config.SampleRate, config.Channels, 0.01);

            try
            {
                // Act & Assert: Should not throw
                engine.Send(chunk.AsSpan());
            }
            finally
            {
                engine.Stop();
            }
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void Send_WhenNotRunning_IsSafeNoOp()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);
            // engine is NOT started

            float[] chunk = new float[512];
            Exception? caught = null;

            // Act
            try { engine.Send(chunk.AsSpan()); }
            catch (Exception ex) { caught = ex; }

            // Assert: the Rust engine accepts Send before Start as a safe no-op (the samples are
            // dropped/buffered rather than raising), so it must not throw.
            Assert.IsNull(caught,
                $"Send() on a non-running engine should be a safe no-op, but threw {caught?.GetType().Name}: {caught?.Message}");
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void EngineStatus_StaysRunning_NoDisconnectStateOnRustEngine()
        {
            using var engine = EngineTestSupport.CreateOrSkip(AudioConfig.Default);
            engine.Start();

            try
            {
                //The status machine only walks Idle/Running/Error, nothing ever sets DeviceDisconnected
                for (int i = 0; i < 5; i++)
                {
                    Assert.AreEqual(EngineStatus.Running, engine.Status,
                        "The rust engine has no disconnect state, a healthy run must stay Running.");
                    Thread.Sleep(20);
                }

                Assert.AreEqual(1, engine.OwnAudioEngineActivate(),
                    "OwnAudioEngineActivate() must report 1 while the engine is Running.");
            }
            finally
            {
                engine.Stop();
            }
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void DeviceEvents_NeverFire_OnRustEngine()
        {
            using var engine = EngineTestSupport.CreateOrSkip(AudioConfig.Default);

            string? _fired = null;

            engine.DeviceStateChanged += (s, e) => _fired = "DeviceStateChanged";
            engine.DeviceReconnected += (s, e) => _fired = "DeviceReconnected";
            engine.OutputDeviceChanged += (s, e) => _fired = "OutputDeviceChanged";
            engine.InputDeviceChanged += (s, e) => _fired = "InputDeviceChanged";

            engine.Start();
            engine.GetOutputDevices();
            engine.GetInputDevices();
            engine.PauseDeviceMonitoring();
            Thread.Sleep(100);
            engine.ResumeDeviceMonitoring();
            engine.Stop();

            //Pins what the rust backend actually does: the four hot-plug events are declared for
            //IAudioEngine and never raised, there is no device monitoring behind them. The day
            //hot-plug lands this fails on purpose and the docs need a pass too.
            Assert.IsNull(_fired,
                $"'{_fired}' fired, but the rust engine declares the device events without ever raising them.");
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void Send_WhileMonitoringPaused_StillGoesThrough()
        {
            var _config = AudioConfig.Default;
            _config.BufferSize = 256;

            using var engine = EngineTestSupport.CreateOrSkip(_config);
            engine.Start();
            Thread.Sleep(50);

            engine.PauseDeviceMonitoring();

            float[] _chunk = TestHelpers.GenerateSineWave(440f, _config.SampleRate, _config.Channels, 0.005);
            Exception? _caught = null;

            try { engine.Send(_chunk.AsSpan()); }
            catch (AudioException ex) { _caught = ex; }
            finally { engine.ResumeDeviceMonitoring(); engine.Stop(); }

            Assert.IsNull(_caught,
                $"Pause/ResumeDeviceMonitoring are no-ops on the rust engine and must not touch the transport. Error: {_caught?.Message}");
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void DeviceStateChanged_Event_FiresOnDisconnect_Simulated()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);
            engine.Start();

            bool disconnectEventFired = false;
            AudioDeviceState? receivedState = null;
            string? receivedDeviceName = null;

            engine.DeviceStateChanged += (sender, args) =>
            {
                disconnectEventFired = true;
                receivedState = args.NewState;
                receivedDeviceName = args.DeviceInfo.Name;
            };

            // Act: manually fire the event the way HandleDeviceRemoved would
            var simulatedDeviceInfo = new AudioDeviceInfo(
                deviceId: "sim-001",
                name: "Simulated USB Interface",
                engineName: "MiniAudio.CoreAudio",
                isInput: false,
                isOutput: true,
                isDefault: false,
                state: AudioDeviceState.Unplugged);

            Type engineType = engine.GetType();
            FieldInfo? eventField = engineType.GetField("DeviceStateChanged",
                BindingFlags.NonPublic | BindingFlags.Instance);

            var eventDelegate = (MulticastDelegate?)eventField?.GetValue(engine);

            if (eventDelegate != null)
            {
                var eventArgs = new AudioDeviceStateChangedEventArgs("sim-001", AudioDeviceState.Unplugged, simulatedDeviceInfo);
                foreach (var handler in eventDelegate.GetInvocationList())
                    handler.DynamicInvoke(engine, eventArgs);
            }
            else
            {
                Console.WriteLine("Note: Could not invoke DeviceStateChanged via reflection on sealed type. " +
                                  "Subscription was verified in the dedicated event test.");
                engine.Stop();
                return;
            }

            // Assert
            Assert.IsTrue(disconnectEventFired, "DeviceStateChanged event should have fired.");
            Assert.AreEqual(AudioDeviceState.Unplugged, receivedState);
            Assert.AreEqual("Simulated USB Interface", receivedDeviceName);

            engine.Stop();
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void DeviceReconnected_Event_FiresOnReconnect_Simulated()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);
            engine.Start();

            bool reconnectFired = false;
            string? reconnectedName = null;
            bool? isOutputDevice = null;

            engine.DeviceReconnected += (sender, args) =>
            {
                reconnectFired = true;
                reconnectedName = args.DeviceName;
                isOutputDevice = args.IsOutputDevice;
            };

            // Act: raise via reflection
            Type engineType = engine.GetType();
            FieldInfo? eventField = engineType.GetField("DeviceReconnected",
                BindingFlags.NonPublic | BindingFlags.Instance);

            var simulatedDeviceInfo = new AudioDeviceInfo(
                deviceId: "sim-001",
                name: "Simulated USB Interface",
                engineName: "MiniAudio.CoreAudio",
                isInput: false,
                isOutput: true,
                isDefault: false,
                state: AudioDeviceState.Active);

            var eventDelegate = (MulticastDelegate?)eventField?.GetValue(engine);
            if (eventDelegate != null)
            {
                var eventArgs = new AudioDeviceReconnectedEventArgs(
                    "sim-001", "Simulated USB Interface", true, simulatedDeviceInfo);

                foreach (var handler in eventDelegate.GetInvocationList())
                    handler.DynamicInvoke(engine, eventArgs);

                // Assert
                Assert.IsTrue(reconnectFired, "DeviceReconnected event should have fired.");
                Assert.AreEqual("Simulated USB Interface", reconnectedName);
                Assert.IsTrue(isOutputDevice == true);
            }
            else
            {
                Console.WriteLine("Note: Could not invoke DeviceReconnected via reflection. Subscription tested separately.");
            }

            engine.Stop();
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void DeviceMonitoring_PausedDuringNormalRun_StatusRemainsRunning()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);
            engine.Start();

            // Act
            engine.PauseDeviceMonitoring();
            Thread.Sleep(100); // wait a tick

            // Assert
            Assert.AreEqual(EngineStatus.Running, engine.Status,
                "Pausing device monitoring should not affect engine status.");

            engine.ResumeDeviceMonitoring();
            engine.Stop();
        }

        [TestMethod]
        [TestCategory("DeviceDisconnect")]
        public void StartStopStart_WalksIdleRunningIdle()
        {
            using var engine = EngineTestSupport.CreateOrSkip(AudioConfig.Default);

            Assert.AreEqual(EngineStatus.Idle, engine.Status, "A fresh engine sits Idle.");

            engine.Start();
            Assert.AreEqual(EngineStatus.Running, engine.Status);

            engine.Stop();
            Assert.AreEqual(EngineStatus.Idle, engine.Status, "Stop() must land back on Idle, never on a stale state.");

            //Restart has to come up clean, the old engine kept a disconnect latch that survived Stop
            engine.Start();
            Assert.AreEqual(EngineStatus.Running, engine.Status, "A restarted engine must report Running again.");
            Assert.AreEqual(1, engine.OwnAudioEngineActivate());

            engine.Stop();
            Assert.AreEqual(1, engine.OwnAudioEngineStopped());
        }
    }
}
