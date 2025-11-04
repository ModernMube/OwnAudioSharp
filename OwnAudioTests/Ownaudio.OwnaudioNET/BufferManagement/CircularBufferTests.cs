using OwnaudioNET.BufferManagement;

namespace Ownaudio.OwnaudioNET.Tests.BufferManagement;

/// <summary>
/// Tests for the CircularBuffer class.
/// </summary>
public class CircularBufferTests
{
    [Fact]
    public void Constructor_WithValidCapacity_ShouldInitialize()
    {
        // Arrange & Act
        var buffer = new CircularBuffer(1024);

        // Assert
        buffer.Should().NotBeNull();
        buffer.Capacity.Should().Be(1024);
        buffer.Available.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithInvalidCapacity_ShouldThrow()
    {
        // Arrange & Act
        Action act = () => new CircularBuffer(0);

        // Assert
        act.Should().Throw<Exception>(); // Can be ArgumentException or other
    }

    [Fact]
    public void Write_ShouldIncreaseAvailable()
    {
        // Arrange
        var buffer = new CircularBuffer(1024);
        var data = new float[100];

        // Act
        int written = buffer.Write(data);

        // Assert
        written.Should().Be(100);
        buffer.Available.Should().Be(100);
    }

    [Fact]
    public void Read_ShouldDecreaseAvailable()
    {
        // Arrange
        var buffer = new CircularBuffer(1024);
        var writeData = new float[100];
        var readData = new float[50];
        buffer.Write(writeData);

        // Act
        int read = buffer.Read(readData);

        // Assert
        read.Should().Be(50);
        buffer.Available.Should().Be(50);
    }

    [Fact]
    public void ReadMoreThanAvailable_ShouldReadPartial()
    {
        // Arrange
        var buffer = new CircularBuffer(1024);
        var writeData = new float[50];
        var readData = new float[100];
        buffer.Write(writeData);

        // Act
        int read = buffer.Read(readData);

        // Assert
        read.Should().Be(50);
        buffer.Available.Should().Be(0);
    }

    [Fact]
    public void Clear_ShouldResetBuffer()
    {
        // Arrange
        var buffer = new CircularBuffer(1024);
        var data = new float[100];
        buffer.Write(data);

        // Act
        buffer.Clear();

        // Assert
        buffer.Available.Should().Be(0);
    }

    [Fact]
    public void WriteAndReadCycle_ShouldPreserveData()
    {
        // Arrange
        var buffer = new CircularBuffer(1024);
        var writeData = new float[100];
        for (int i = 0; i < writeData.Length; i++)
            writeData[i] = i;

        var readData = new float[100];

        // Act
        buffer.Write(writeData);
        buffer.Read(readData);

        // Assert
        readData.Should().Equal(writeData);
    }

    [Fact]
    public void WrapAround_ShouldHandleCorrectly()
    {
        // Arrange
        var buffer = new CircularBuffer(100);
        var data1 = new float[80];
        var data2 = new float[50];
        var readBuffer = new float[50];

        // Act
        buffer.Write(data1);  // Write 80
        buffer.Read(readBuffer); // Read 50 (30 left)
        buffer.Write(data2);  // Write 50 (should wrap around)

        // Assert
        buffer.Available.Should().Be(80); // 30 + 50
    }

    [Fact]
    public void ConcurrentWriteAndRead_ShouldBeSafe()
    {
        // Arrange
        var buffer = new CircularBuffer(10000);
        var writeTask = Task.Run(() =>
        {
            var data = new float[100];
            for (int i = 0; i < 100; i++)
            {
                buffer.Write(data);
                Thread.Sleep(1);
            }
        });

        var readTask = Task.Run(() =>
        {
            var data = new float[100];
            for (int i = 0; i < 100; i++)
            {
                buffer.Read(data);
                Thread.Sleep(1);
            }
        });

        // Act & Assert
        Action act = () => Task.WaitAll(writeTask, readTask);
        act.Should().NotThrow();
    }
}
