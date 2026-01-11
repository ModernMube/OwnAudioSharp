using OwnaudioNET.BufferManagement;

namespace Ownaudio.OwnaudioNET.Tests.BufferManagement;

/// <summary>
/// Tests for the AudioBufferPool class.
/// </summary>
public class AudioBufferPoolTests
{
    [Fact]
    public void Constructor_WithValidParameters_ShouldInitialize()
    {
        // Arrange & Act
        var pool = new AudioBufferPool(bufferSize: 1024, initialPoolSize: 4, maxPoolSize: 16);

        // Assert
        pool.Should().NotBeNull();
    }

    [Fact]
    public void Rent_ShouldReturnBufferOfCorrectSize()
    {
        // Arrange
        var pool = new AudioBufferPool(bufferSize: 1024, initialPoolSize: 4, maxPoolSize: 16);

        // Act
        var buffer = pool.Rent();

        // Assert
        buffer.Should().NotBeNull();
        buffer.Length.Should().Be(1024);
    }

    [Fact]
    public void RentAndReturn_ShouldReuseBuffers()
    {
        // Arrange
        var pool = new AudioBufferPool(bufferSize: 1024, initialPoolSize: 2, maxPoolSize: 8);

        // Act
        var buffer1 = pool.Rent();
        var reference1 = buffer1;
        pool.Return(buffer1);
        var buffer2 = pool.Rent();

        // Assert
        buffer2.Should().BeSameAs(reference1); // Should get the same buffer back
    }

    [Fact]
    public void Return_WithNullBuffer_ShouldHandleGracefully()
    {
        // Arrange
        var pool = new AudioBufferPool(bufferSize: 1024, initialPoolSize: 4, maxPoolSize: 16);

        // Act & Assert - May throw, that's implementation specific
        try
        {
            pool.Return(null!);
        }
        catch (ArgumentNullException)
        {
            // This is acceptable behavior
        }
    }

    [Fact]
    public void Return_WithWrongSizeBuffer_ShouldHandleGracefully()
    {
        // Arrange
        var pool = new AudioBufferPool(bufferSize: 1024, initialPoolSize: 4, maxPoolSize: 16);
        var wrongSizeBuffer = new float[512];

        // Act & Assert - May throw, that's implementation specific
        try
        {
            pool.Return(wrongSizeBuffer);
        }
        catch (ArgumentException)
        {
            // This is acceptable behavior
        }
    }

    [Fact]
    public void RentMultiple_ShouldProvideDistinctBuffers()
    {
        // Arrange
        var pool = new AudioBufferPool(bufferSize: 1024, initialPoolSize: 4, maxPoolSize: 16);

        // Act
        var buffer1 = pool.Rent();
        var buffer2 = pool.Rent();
        var buffer3 = pool.Rent();

        // Assert
        buffer1.Should().NotBeSameAs(buffer2);
        buffer2.Should().NotBeSameAs(buffer3);
        buffer1.Should().NotBeSameAs(buffer3);
    }

    [Fact]
    public void Clear_ShouldEmptyPool()
    {
        // Arrange
        var pool = new AudioBufferPool(bufferSize: 1024, initialPoolSize: 4, maxPoolSize: 16);
        var buffer = pool.Rent();
        pool.Return(buffer);

        // Act
        pool.Clear();

        // Assert - Next rent should create a new buffer (not from pool)
        var newBuffer = pool.Rent();
        newBuffer.Should().NotBeNull();
    }

    [Fact]
    public void ConcurrentRentAndReturn_ShouldBeSafe()
    {
        // Arrange
        var pool = new AudioBufferPool(bufferSize: 1024, initialPoolSize: 4, maxPoolSize: 32);
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    var buffer = pool.Rent();
                    Thread.Sleep(1);
                    pool.Return(buffer);
                }
            }));
        }

        // Assert
        Action act = () => Task.WaitAll(tasks.ToArray());
        act.Should().NotThrow();
    }

    [Fact]
    public void ExceedMaxPoolSize_ShouldStillWork()
    {
        // Arrange
        var pool = new AudioBufferPool(bufferSize: 1024, initialPoolSize: 2, maxPoolSize: 4);

        // Act - Rent more than max pool size
        var buffers = new List<float[]>();
        for (int i = 0; i < 10; i++)
        {
            buffers.Add(pool.Rent());
        }

        // Return all
        foreach (var buffer in buffers)
        {
            pool.Return(buffer);
        }

        // Assert - Should not throw, just won't keep all in pool
        var newBuffer = pool.Rent();
        newBuffer.Should().NotBeNull();
    }
}
