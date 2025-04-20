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

            if(RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                ownAudioInit = OwnAudio.Initialize(Ownaudio.Engines.OwnAudioEngine.EngineHostType.ALSA);
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                ownAudioInit = OwnAudio.Initialize(Ownaudio.Engines.OwnAudioEngine.EngineHostType.CoreAudio);
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                ownAudioInit = OwnAudio.Initialize(Ownaudio.Engines.OwnAudioEngine.EngineHostType.WASAPI);
            }

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

                AudioEngineOutputOptions _audioEngineOptions = new AudioEngineOutputOptions
                (
                    device: OwnAudio.DefaultOutputDevice,
                    channels: OwnAudioEngine.EngineChannels.Stereo,
                    sampleRate: OwnAudio.DefaultOutputDevice.DefaultSampleRate,
                    latency: OwnAudio.DefaultOutputDevice.DefaultHighOutputLatency
                );

                AudioEngineInputOptions _audioInputOptions = new AudioEngineInputOptions
                (
                    device: OwnAudio.DefaultInputDevice,
                    channels: OwnAudioEngine.EngineChannels.Mono,
                    sampleRate: OwnAudio.DefaultInputDevice.DefaultSampleRate,
                    latency: OwnAudio.DefaultInputDevice.DefaultLowInputLatency
                );

                SourceManager.OutputEngineOptions = _audioEngineOptions;
                SourceManager.InputEngineOptions = _audioInputOptions;
                SourceManager.EngineFramesPerBuffer = 512;

                SourceManager manager = SourceManager.Instance;

                await manager.AddOutputSource("D:\\Sogorock\\Ocam\\2025\\Szep julia\\Szép Júlia - Beszkid József (cover)_audio_music.wav");
                int track1Number = manager.Sources.Count - 1;

                manager.Play();

                Console.Clear();

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
                    Console.WriteLine("Unpack the files in the LIB directory!");
                }
            }
        }
    }
}
