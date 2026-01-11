using OwnaudioNET;
using OwnaudioNET.Features.Matchering;

class Program
{
    static async Task Main(string[] args)
    {
        OwnaudioNet.Initialize();

        if(OwnaudioNet.IsInitialized)
        {
            try
            {
                var processor = new AudioAnalyzer();

                processor.ProcessEQMatching(
                    sourceFile: @"path/audio/source.mp3",   // File to be processed
                    targetFile: @"path/audio/target.wav",   // Reference file
                    outputFile: @"path/output.wav");  // Output file that we create

                processor.ProcessWithEnhancedPreset(
                    sourceFile: @"path/audio/source.mp3",
                    outputFile: @"path/output.wav",
                    PlaybackSystem.ConcertPA);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
            finally
            {
                OwnaudioNet.Shutdown();
            }
        }
        else
        {
            Console.WriteLine("Ownaudio engine initialization failed!");
        }  
    }
}
