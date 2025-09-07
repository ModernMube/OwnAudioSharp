using Ownaudio;
using Ownaudio.Utilities.Matchering;

class Program
{
    static async Task Main(string[] args)
    {
        if (OwnAudio.Initialize())
        {
            try
            {
                var processor = new AudioAnalyzer();

                string sourceFile = @"path/audio/sourceaudio.mp3";  // File to be processed
                string targetFile = @"path/audio/targetaudio.mp3"; // Reference file
                string outputFile = @"path/audio/outputaudio.mp3"; // Output file that we create

                processor.ProcessEQMatching(sourceFile, targetFile, outputFile);

                OwnAudio.Free();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
            finally
            {
                OwnAudio.Free();
            }
        }
        else
        {
            Console.WriteLine("Failed to initialize OwnAudio!");
        }
    }
}
