using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Decoders;
using Ownaudio.Decoders.Wav;
using Ownaudio.Core;
using System;
using System.IO;

namespace Ownaudio.EngineTest
{
    /// <summary>
    /// Test suite for audio decoders.
    /// Tests WAV decoder, format conversion, and decode operations.
    /// </summary>
    [TestClass]
    public class DecoderTests
    {
        [TestMethod]
        public void WavDecoder_WithValidStream_ShouldInitialize()
        {
            // Arrange
            byte[] wavData = CreateSimpleWavFile(48000, 2, 16, 1.0);
            using var stream = new MemoryStream(wavData);

            // Act
            using var decoder = new WavDecoder(stream);

            // Assert
            Assert.IsNotNull(decoder, "Decoder should be created");
            Assert.IsNotNull(decoder.StreamInfo, "StreamInfo should be populated");
        }

        [TestMethod]
        public void WavDecoder_StreamInfo_ShouldHaveCorrectProperties()
        {
            // Arrange
            int sampleRate = 48000;
            int channels = 2;
            byte[] wavData = CreateSimpleWavFile(sampleRate, channels, 16, 1.0);
            using var stream = new MemoryStream(wavData);

            // Act
            using var decoder = new WavDecoder(stream);
            var streamInfo = decoder.StreamInfo;

            // Assert
            Assert.AreEqual(sampleRate, streamInfo.SampleRate, "Sample rate should match");
            Assert.AreEqual(channels, streamInfo.Channels, "Channels should match");
            Assert.IsTrue(streamInfo.Duration > TimeSpan.Zero, "Duration should be positive");
        }


        [TestMethod]
        public void WavDecoder_TrySeek_WithValidPosition_ShouldSucceed()
        {
            // Arrange
            byte[] wavData = CreateSimpleWavFile(48000, 2, 16, 2.0);
            using var stream = new MemoryStream(wavData);
            using var decoder = new WavDecoder(stream);

            // Act
            bool seekResult = decoder.TrySeek(TimeSpan.FromSeconds(0.5), out string error);

            // Assert
            Assert.IsTrue(seekResult, $"Seek should succeed. Error: {error}");
            Assert.IsTrue(string.IsNullOrEmpty(error), "Error should be empty on success");
        }

        [TestMethod]
        public void WavDecoder_TrySeek_BeyondEnd_ShouldHandleGracefully()
        {
            // Arrange
            byte[] wavData = CreateSimpleWavFile(48000, 2, 16, 1.0);
            using var stream = new MemoryStream(wavData);
            using var decoder = new WavDecoder(stream);

            // Act
            bool seekResult = decoder.TrySeek(TimeSpan.FromSeconds(100), out string error);

            // Assert - Either succeeds (clamped to end) or fails with error
            if (!seekResult)
            {
                Assert.IsFalse(string.IsNullOrEmpty(error), "Error message should be provided on failure");
            }
        }

        [TestMethod]
        public void WavDecoder_WithNullStream_ShouldThrow()
        {
            // Act & Assert
            try
            {
                using var decoder = new WavDecoder(null!);
                Assert.Fail("Expected ArgumentNullException was not thrown");
            }
            catch (ArgumentNullException)
            {
                // Expected
            }
        }

        [TestMethod]
        public void WavDecoder_Dispose_ShouldNotThrow()
        {
            // Arrange
            byte[] wavData = CreateSimpleWavFile(48000, 2, 16, 1.0);
            var stream = new MemoryStream(wavData);
            var decoder = new WavDecoder(stream);

            // Act & Assert (should not throw)
            decoder.Dispose();
        }

        [TestMethod]
        public void WavDecoder_DisposeCalledTwice_ShouldNotThrow()
        {
            // Arrange
            byte[] wavData = CreateSimpleWavFile(48000, 2, 16, 1.0);
            var stream = new MemoryStream(wavData);
            var decoder = new WavDecoder(stream);

            // Act & Assert (should not throw)
            decoder.Dispose();
            decoder.Dispose();
        }

        #region Helper Methods

        /// <summary>
        /// Creates a simple WAV file in memory for testing purposes.
        /// </summary>
        /// <param name="sampleRate">Sample rate in Hz</param>
        /// <param name="channels">Number of channels</param>
        /// <param name="bitsPerSample">Bits per sample (8, 16, 24, or 32)</param>
        /// <param name="durationSeconds">Duration in seconds</param>
        /// <returns>Byte array containing a valid WAV file</returns>
        private byte[] CreateSimpleWavFile(int sampleRate, int channels, int bitsPerSample, double durationSeconds)
        {
            int bytesPerSample = bitsPerSample / 8;
            int blockAlign = channels * bytesPerSample;
            int byteRate = sampleRate * blockAlign;
            int numSamples = (int)(sampleRate * durationSeconds);
            int dataSize = numSamples * blockAlign;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // RIFF header
            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataSize); // Chunk size
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });

            // fmt sub-chunk
            writer.Write(new char[] { 'f', 'm', 't', ' ' });
            writer.Write(16); // Sub-chunk size (16 for PCM)
            writer.Write((ushort)1); // Audio format (1 = PCM)
            writer.Write((ushort)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((ushort)blockAlign);
            writer.Write((ushort)bitsPerSample);

            // data sub-chunk
            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(dataSize);

            // Write audio data (sine wave)
            for (int i = 0; i < numSamples; i++)
            {
                // Generate a 440Hz sine wave
                double t = (double)i / sampleRate;
                double value = Math.Sin(2.0 * Math.PI * 440.0 * t);

                // Convert to integer based on bit depth
                int sampleValue;
                if (bitsPerSample == 8)
                {
                    sampleValue = (int)((value + 1.0) * 127.5); // 8-bit is unsigned
                    writer.Write((byte)sampleValue);
                    if (channels == 2)
                        writer.Write((byte)sampleValue);
                }
                else if (bitsPerSample == 16)
                {
                    sampleValue = (int)(value * 32767.0);
                    writer.Write((short)sampleValue);
                    if (channels == 2)
                        writer.Write((short)sampleValue);
                }
                else if (bitsPerSample == 24)
                {
                    sampleValue = (int)(value * 8388607.0);
                    byte[] bytes = BitConverter.GetBytes(sampleValue);
                    writer.Write(bytes[0]);
                    writer.Write(bytes[1]);
                    writer.Write(bytes[2]);
                    if (channels == 2)
                    {
                        writer.Write(bytes[0]);
                        writer.Write(bytes[1]);
                        writer.Write(bytes[2]);
                    }
                }
                else // 32-bit
                {
                    sampleValue = (int)(value * 2147483647.0);
                    writer.Write(sampleValue);
                    if (channels == 2)
                        writer.Write(sampleValue);
                }
            }

            return ms.ToArray();
        }

        #endregion
    }
}
