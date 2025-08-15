using Ownaudio;
using Ownaudio.Engines;
using Ownaudio.Sources;
using System.Runtime.InteropServices;

namespace Simpleplayer
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            bool ownAudioInit = false;

            //We set the audio host type appropriate for the system.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                ownAudioInit = OwnAudio.Initialize(Ownaudio.Engines.OwnAudioEngine.EngineHostType.ALSA);
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                ownAudioInit = OwnAudio.Initialize(Ownaudio.Engines.OwnAudioEngine.EngineHostType.COREAUDIO);
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                ownAudioInit = OwnAudio.Initialize(Ownaudio.Engines.OwnAudioEngine.EngineHostType.WASAPI);
            }

            // If the initialization was successful, we can use the audio engine.
            if (ownAudioInit)
            {
                foreach (AudioDevice device in OwnAudio.OutputDevices)
                {
                    Console.WriteLine("Output device: " + device.Name);
                }

                foreach (AudioDevice device in OwnAudio.InputDevices)
                {
                    Console.WriteLine("Input device: " + device.Name);
                }

                // We set the output and input options for the audio engine.
                AudioEngineOutputOptions _audioEngineOptions = new AudioEngineOutputOptions
                (
                    device: OwnAudio.OutputDevices.ElementAt(0),
                    channels: OwnAudioEngine.EngineChannels.Stereo,
                    sampleRate: OwnAudio.OutputDevices.ElementAt(0).DefaultSampleRate,
                    latency: OwnAudio.OutputDevices.ElementAt(0).DefaultHighOutputLatency
                );

                AudioEngineInputOptions _audioInputOptions = new AudioEngineInputOptions
                (
                    device: OwnAudio.InputDevices.ElementAt(0),
                    channels: OwnAudioEngine.EngineChannels.Mono,
                    sampleRate: OwnAudio.InputDevices.ElementAt(0).DefaultSampleRate,
                    latency: OwnAudio.InputDevices.ElementAt(0).DefaultLowInputLatency
                );

                SourceManager.OutputEngineOptions = _audioEngineOptions;
                SourceManager.InputEngineOptions = _audioInputOptions;
                SourceManager.EngineFramesPerBuffer = 512;

                SourceManager manager = SourceManager.Instance;

                await manager.AddOutputSource("E:\\AI\\MelBand\\VocalModel\\audiooutput\\Kamazlakteged_instrumental.wav");
                int track1Number = manager.Sources.Count - 1;

                manager.Play();

                Console.WriteLine("Hi! Ownaudio user");
                Console.WriteLine("Default output device: " + OwnAudio.DefaultOutputDevice.Name);

                Console.WriteLine("Press any key to stop playback...");
                Console.Read();

                manager.Stop();

                manager.Reset();
                OwnAudio.Free();
            }
            else
            {
                if (!OwnAudio.IsFFmpegInitialized || !OwnAudio.IsPortAudioInitialized)
                {
                    Console.WriteLine("library initialization failed!");
                }
            }
        }
    }
}
