using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Core.Common;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.EngineTest
{
    /// <summary>
    /// Test suite for LockFreeRingBuffer.
    /// Tests thread-safe operations, capacity management, and concurrent access.
    /// </summary>
    [TestClass]
    public class RingBufferTests
    {
        [TestMethod]
        public void Constructor_WithValidCapacity_ShouldCreate()
        {
            // Act
            var buffer = new LockFreeRingBuffer<float>(1024);

            // Assert
            Assert.IsNotNull(buffer, "Buffer should be created");
            Assert.IsTrue(buffer.Capacity >= 1024, "Capacity should be at least requested size");
        }

        [TestMethod]
        public void Constructor_RoundsUpToPowerOf2()
        {
            // Arrange & Act
            var buffer = new LockFreeRingBuffer<float>(1000);

            // Assert
            Assert.AreEqual(1024, buffer.Capacity, "Capacity should be rounded up to next power of 2");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Constructor_WithZeroCapacity_ShouldThrow()
        {
            // Act
            var buffer = new LockFreeRingBuffer<float>(0);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Constructor_WithNegativeCapacity_ShouldThrow()
        {
            // Act
            var buffer = new LockFreeRingBuffer<float>(-1);
        }

        [TestMethod]
        public void Available_EmptyBuffer_ShouldReturnZero()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(1024);

            // Act
            int available = buffer.Available;

            // Assert
            Assert.AreEqual(0, available, "Empty buffer should have 0 available elements");
        }

        [TestMethod]
        public void AvailableRead_EmptyBuffer_ShouldReturnZero()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(1024);

            // Act
            int available = buffer.AvailableRead;

            // Assert
            Assert.AreEqual(0, available, "Empty buffer should have 0 available elements to read");
        }

        [TestMethod]
        public void WritableCount_EmptyBuffer_ShouldReturnCapacityMinusOne()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(1024);

            // Act
            int writable = buffer.WritableCount;

            // Assert
            Assert.AreEqual(1023, writable, "Empty buffer should have capacity-1 writable elements");
        }

        [TestMethod]
        public void Write_WithValidData_ShouldSucceed()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(1024);
            float[] data = { 1.0f, 2.0f, 3.0f, 4.0f };

            // Act
            int written = buffer.Write(data);

            // Assert
            Assert.AreEqual(4, written, "Should write all 4 elements");
            Assert.AreEqual(4, buffer.Available, "Available should be 4 after writing");
        }

        [TestMethod]
        public void Write_EmptySpan_ShouldReturnZero()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(1024);
            float[] data = Array.Empty<float>();

            // Act
            int written = buffer.Write(data);

            // Assert
            Assert.AreEqual(0, written, "Writing empty span should return 0");
            Assert.AreEqual(0, buffer.Available, "Available should remain 0");
        }

        [TestMethod]
        public void Read_AfterWrite_ShouldReturnSameData()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(1024);
            float[] writeData = { 1.0f, 2.0f, 3.0f, 4.0f };
            float[] readData = new float[4];

            buffer.Write(writeData);

            // Act
            int read = buffer.Read(readData);

            // Assert
            Assert.AreEqual(4, read, "Should read all 4 elements");
            CollectionAssert.AreEqual(writeData, readData, "Read data should match written data");
            Assert.AreEqual(0, buffer.Available, "Buffer should be empty after reading all data");
        }

        [TestMethod]
        public void Read_EmptyBuffer_ShouldReturnZero()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(1024);
            float[] readData = new float[4];

            // Act
            int read = buffer.Read(readData);

            // Assert
            Assert.AreEqual(0, read, "Reading from empty buffer should return 0");
        }

        [TestMethod]
        public void Read_PartialData_ShouldReturnAvailable()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(1024);
            float[] writeData = { 1.0f, 2.0f };
            float[] readData = new float[10];

            buffer.Write(writeData);

            // Act
            int read = buffer.Read(readData);

            // Assert
            Assert.AreEqual(2, read, "Should only read available 2 elements");
            Assert.AreEqual(writeData[0], readData[0]);
            Assert.AreEqual(writeData[1], readData[1]);
        }

        [TestMethod]
        public void WriteRead_MultipleTimes_ShouldSucceed()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(1024);

            // Act & Assert
            for (int i = 0; i < 10; i++)
            {
                float[] writeData = { i * 1.0f, i * 2.0f, i * 3.0f };
                float[] readData = new float[3];

                int written = buffer.Write(writeData);
                Assert.AreEqual(3, written, $"Iteration {i}: should write 3 elements");

                int read = buffer.Read(readData);
                Assert.AreEqual(3, read, $"Iteration {i}: should read 3 elements");

                CollectionAssert.AreEqual(writeData, readData, $"Iteration {i}: data should match");
            }
        }

        [TestMethod]
        public void Write_FullBuffer_ShouldReturnPartialWrite()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(16); // Small buffer, capacity will be 16
            float[] data = new float[20]; // Try to write more than capacity

            // Act
            int written = buffer.Write(data);

            // Assert
            Assert.IsTrue(written < 20, "Should write less than requested when buffer is full");
            Assert.IsTrue(written <= 15, "Should write at most capacity-1 elements");
        }

        [TestMethod]
        public void Clear_ShouldEmptyBuffer()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(1024);
            float[] data = { 1.0f, 2.0f, 3.0f, 4.0f };
            buffer.Write(data);

            // Act
            buffer.Clear();

            // Assert
            Assert.AreEqual(0, buffer.Available, "Buffer should be empty after Clear");
        }

        [TestMethod]
        public void Wraparound_ShouldHandleCorrectly()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(16);

            // Fill buffer almost to capacity
            float[] data1 = Enumerable.Range(0, 10).Select(x => (float)x).ToArray();
            buffer.Write(data1);

            // Read some data
            float[] readBuffer = new float[6];
            buffer.Read(readBuffer);

            // Write more data (should wrap around)
            float[] data2 = Enumerable.Range(100, 8).Select(x => (float)x).ToArray();
            int written = buffer.Write(data2);

            // Act - Read all remaining data
            float[] allData = new float[20];
            int totalRead = buffer.Read(allData);

            // Assert
            Assert.IsTrue(written > 0, "Should write some data during wraparound");
            Assert.AreEqual(12, totalRead, "Should read the correct amount after wraparound");
        }

        [TestMethod]
        public void ConcurrentWriteRead_ShouldBeThreadSafe()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(8192);
            int itemsToProcess = 10000;
            int itemsWritten = 0;
            int itemsRead = 0;
            bool writeComplete = false;

            // Act
            var writeTask = Task.Run(() =>
            {
                for (int i = 0; i < itemsToProcess; i++)
                {
                    float[] data = { i };
                    while (buffer.Write(data) == 0)
                    {
                        Thread.Sleep(1);
                    }
                    Interlocked.Increment(ref itemsWritten);
                }
                writeComplete = true;
            });

            var readTask = Task.Run(() =>
            {
                while (!writeComplete || buffer.Available > 0)
                {
                    float[] data = new float[1];
                    int read = buffer.Read(data);
                    if (read > 0)
                    {
                        Interlocked.Increment(ref itemsRead);
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
            });

            Task.WaitAll(writeTask, readTask);

            // Assert
            Assert.AreEqual(itemsToProcess, itemsWritten, "All items should be written");
            Assert.AreEqual(itemsToProcess, itemsRead, "All items should be read");
            Assert.AreEqual(0, buffer.Available, "Buffer should be empty at the end");
        }

        [TestMethod]
        public void MultipleReads_ShouldConsumeDataInOrder()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(1024);
            float[] writeData = { 1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f };
            buffer.Write(writeData);

            // Act
            float[] read1 = new float[2];
            float[] read2 = new float[2];
            float[] read3 = new float[2];

            buffer.Read(read1);
            buffer.Read(read2);
            buffer.Read(read3);

            // Assert
            Assert.AreEqual(1.0f, read1[0]);
            Assert.AreEqual(2.0f, read1[1]);
            Assert.AreEqual(3.0f, read2[0]);
            Assert.AreEqual(4.0f, read2[1]);
            Assert.AreEqual(5.0f, read3[0]);
            Assert.AreEqual(6.0f, read3[1]);
        }

        [TestMethod]
        public void LargeDataTransfer_ShouldSucceed()
        {
            // Arrange
            var buffer = new LockFreeRingBuffer<float>(8192);
            int totalSamples = 100000;
            float[] writeData = Enumerable.Range(0, totalSamples).Select(x => (float)x).ToArray();
            float[] readData = new float[totalSamples];

            int writePos = 0;
            int readPos = 0;

            // Act - Write and read in chunks
            while (writePos < totalSamples || readPos < writePos)
            {
                // Write chunk
                if (writePos < totalSamples)
                {
                    int chunkSize = Math.Min(512, totalSamples - writePos);
                    int written = buffer.Write(writeData.AsSpan(writePos, chunkSize));
                    writePos += written;
                }

                // Read chunk
                if (readPos < writePos)
                {
                    int toRead = Math.Min(512, totalSamples - readPos);
                    int read = buffer.Read(readData.AsSpan(readPos, toRead));
                    readPos += read;
                }
            }

            // Assert
            Assert.AreEqual(totalSamples, readPos, "Should read all samples");
            CollectionAssert.AreEqual(writeData, readData, "Data should match");
        }

        [TestMethod]
        public void PowerOf2Capacities_ShouldAllWork()
        {
            // Arrange
            int[] capacities = { 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096 };

            // Act & Assert
            foreach (var capacity in capacities)
            {
                var buffer = new LockFreeRingBuffer<float>(capacity);
                Assert.AreEqual(capacity, buffer.Capacity, $"Capacity {capacity} should be preserved");

                float[] data = { 1.0f, 2.0f };
                int written = buffer.Write(data);
                Assert.AreEqual(2, written, $"Should write to buffer with capacity {capacity}");

                float[] readData = new float[2];
                int read = buffer.Read(readData);
                Assert.AreEqual(2, read, $"Should read from buffer with capacity {capacity}");
            }
        }
    }
}
