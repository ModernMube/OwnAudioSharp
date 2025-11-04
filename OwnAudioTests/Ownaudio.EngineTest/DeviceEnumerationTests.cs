using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Core;
using System.Collections.Generic;
using System.Linq;

namespace Ownaudio.EngineTest
{
    /// <summary>
    /// Test suite for audio device enumeration and selection.
    /// Tests device discovery, selection by name/index, and device switching.
    /// Platform-independent tests using AudioEngineFactory.
    /// </summary>
    [TestClass]
    public class DeviceEnumerationTests
    {
        [TestMethod]
        public void GetOutputDevices_ShouldReturnNonEmptyList()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Act
            var devices = engine.GetOutputDevices();

            // Assert
            Assert.IsNotNull(devices, "GetOutputDevices should not return null");
            Assert.IsTrue(devices.Count > 0, "GetOutputDevices should return at least one device");
        }

        [TestMethod]
        public void GetInputDevices_ShouldReturnNonEmptyList()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Act
            var devices = engine.GetInputDevices();

            // Assert
            Assert.IsNotNull(devices, "GetInputDevices should not return null");
            Assert.IsTrue(devices.Count > 0, "GetInputDevices should return at least one device");
        }

        [TestMethod]
        public void GetOutputDevices_ShouldHaveDefaultDevice()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Act
            var devices = engine.GetOutputDevices();
            var defaultDevice = devices.FirstOrDefault(d => d.IsDefault);

            // Assert
            Assert.IsNotNull(defaultDevice, "There should be at least one default output device");
            Assert.IsTrue(defaultDevice.IsOutput, "Default output device should be marked as output");
        }

        [TestMethod]
        public void GetInputDevices_ShouldHaveDefaultDevice()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Act
            var devices = engine.GetInputDevices();
            var defaultDevice = devices.FirstOrDefault(d => d.IsDefault);

            // Assert
            Assert.IsNotNull(defaultDevice, "There should be at least one default input device");
            Assert.IsTrue(defaultDevice.IsInput, "Default input device should be marked as input");
        }

        [TestMethod]
        public void OutputDeviceInfo_ShouldHaveValidProperties()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Act
            var devices = engine.GetOutputDevices();
            var firstDevice = devices[0];

            // Assert
            Assert.IsFalse(string.IsNullOrWhiteSpace(firstDevice.DeviceId), "DeviceId should not be empty");
            Assert.IsFalse(string.IsNullOrWhiteSpace(firstDevice.Name), "Name should not be empty");
            Assert.IsTrue(firstDevice.IsOutput, "Output device should be marked as output");
        }

        [TestMethod]
        public void InputDeviceInfo_ShouldHaveValidProperties()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Act
            var devices = engine.GetInputDevices();
            var firstDevice = devices[0];

            // Assert
            Assert.IsFalse(string.IsNullOrWhiteSpace(firstDevice.DeviceId), "DeviceId should not be empty");
            Assert.IsFalse(string.IsNullOrWhiteSpace(firstDevice.Name), "Name should not be empty");
            Assert.IsTrue(firstDevice.IsInput, "Input device should be marked as input");
        }

        [TestMethod]
        public void SetOutputDeviceByIndex_WithValidIndex_ShouldSucceed()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);
            var devices = engine.GetOutputDevices();

            if (devices.Count < 1)
            {
                Assert.Inconclusive("Not enough output devices to test");
                return;
            }

            // Act
            int result = engine.SetOutputDeviceByIndex(0);

            // Assert
            Assert.AreEqual(0, result, "SetOutputDeviceByIndex should succeed with valid index");
        }

        [TestMethod]
        public void SetOutputDeviceByIndex_WithNegativeIndex_ShouldFail()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Act
            int result = engine.SetOutputDeviceByIndex(-1);

            // Assert
            Assert.AreEqual(-1, result, "SetOutputDeviceByIndex should return -1 with negative index");
        }

        [TestMethod]
        public void SetOutputDeviceByIndex_WithOutOfRangeIndex_ShouldFail()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);
            var devices = engine.GetOutputDevices();

            // Act
            int result = engine.SetOutputDeviceByIndex(devices.Count + 100);

            // Assert
            Assert.AreEqual(-3, result, "SetOutputDeviceByIndex should return -3 with out of range index");
        }

        [TestMethod]
        public void SetOutputDeviceByIndex_WhileRunning_ShouldFail()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);
            engine.Start();

            // Act
            int result = engine.SetOutputDeviceByIndex(0);

            // Assert
            Assert.AreEqual(-2, result, "SetOutputDeviceByIndex should return -2 when engine is running");

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void SetOutputDeviceByName_WithValidName_ShouldSucceed()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);
            var devices = engine.GetOutputDevices();

            if (devices.Count < 1)
            {
                Assert.Inconclusive("No output devices available to test");
                return;
            }

            string deviceName = devices[0].Name;

            // Act
            int result = engine.SetOutputDeviceByName(deviceName);

            // Assert
            Assert.AreEqual(0, result, "SetOutputDeviceByName should succeed with valid device name");
        }

        [TestMethod]
        public void SetOutputDeviceByName_WithNullName_ShouldFail()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Act
            int result = engine.SetOutputDeviceByName(null!);

            // Assert
            Assert.AreEqual(-1, result, "SetOutputDeviceByName should return -1 with null name");
        }

        [TestMethod]
        public void SetOutputDeviceByName_WithEmptyName_ShouldFail()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Act
            int result = engine.SetOutputDeviceByName("");

            // Assert
            Assert.AreEqual(-1, result, "SetOutputDeviceByName should return -1 with empty name");
        }

        [TestMethod]
        public void SetOutputDeviceByName_WithInvalidName_ShouldFail()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Act
            int result = engine.SetOutputDeviceByName("NonExistentDevice123456");

            // Assert
            Assert.AreEqual(-3, result, "SetOutputDeviceByName should return -3 when device is not found");
        }

        [TestMethod]
        public void SetInputDeviceByIndex_WithValidIndex_ShouldSucceed()
        {
            // Arrange
            var config = new AudioConfig
            {
                EnableInput = true,
                EnableOutput = true
            };
            using var engine = AudioEngineFactory.Create(config);
            var devices = engine.GetInputDevices();

            if (devices.Count < 1)
            {
                Assert.Inconclusive("Not enough input devices to test");
                return;
            }

            // Act
            int result = engine.SetInputDeviceByIndex(0);

            // Assert
            Assert.AreEqual(0, result, "SetInputDeviceByIndex should succeed with valid index");
        }

        [TestMethod]
        public void SetInputDeviceByIndex_WithNegativeIndex_ShouldFail()
        {
            // Arrange
            var config = new AudioConfig
            {
                EnableInput = true,
                EnableOutput = true
            };
            using var engine = AudioEngineFactory.Create(config);

            // Act
            int result = engine.SetInputDeviceByIndex(-1);

            // Assert
            Assert.AreEqual(-1, result, "SetInputDeviceByIndex should return -1 with negative index");
        }

        [TestMethod]
        public void SetInputDeviceByName_WithValidName_ShouldSucceed()
        {
            // Arrange
            var config = new AudioConfig
            {
                EnableInput = true,
                EnableOutput = true
            };
            using var engine = AudioEngineFactory.Create(config);
            var devices = engine.GetInputDevices();

            if (devices.Count < 1)
            {
                Assert.Inconclusive("No input devices available to test");
                return;
            }

            string deviceName = devices[0].Name;

            // Act
            int result = engine.SetInputDeviceByName(deviceName);

            // Assert
            Assert.AreEqual(0, result, "SetInputDeviceByName should succeed with valid device name");
        }

        [TestMethod]
        public void DeviceInfo_ToString_ShouldReturnReadableString()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);
            var devices = engine.GetOutputDevices();
            var device = devices[0];

            // Act
            string deviceString = device.ToString();

            // Assert
            Assert.IsFalse(string.IsNullOrWhiteSpace(deviceString), "ToString should return a non-empty string");
            Assert.IsTrue(deviceString.Contains(device.Name), "ToString should contain device name");
        }

        [TestMethod]
        public void GetOutputDevices_MultipleCallsShouldReturnConsistentResults()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Act
            var devices1 = engine.GetOutputDevices();
            var devices2 = engine.GetOutputDevices();

            // Assert
            Assert.AreEqual(devices1.Count, devices2.Count, "Multiple calls should return same number of devices");
        }

        [TestMethod]
        public void GetInputDevices_MultipleCallsShouldReturnConsistentResults()
        {
            // Arrange
            using var engine = AudioEngineFactory.Create(AudioConfig.Default);

            // Act
            var devices1 = engine.GetInputDevices();
            var devices2 = engine.GetInputDevices();

            // Assert
            Assert.AreEqual(devices1.Count, devices2.Count, "Multiple calls should return same number of devices");
        }
    }
}
