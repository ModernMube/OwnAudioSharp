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

                processor.ProcessEQMatching(
                    sourceFile: @"path/audio/source.mp3",   // File to be processed
                    targetFile: @"path/audio/target.mp3",   // Reference file
                    outputFile: @"path/audio/output.mp3");  // Output file that we create

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
