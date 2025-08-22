using System.Collections.Generic;

using Ownaudio.Exceptions;
using Ownaudio.Utilities;

namespace Ownaudio;

/// <summary>
/// Functions to retrieve, configure and manage the current Ownaudio environment, 
/// which affects the entire directory configuration.
/// </summary>
public static partial class OwnAudio
{
   /// <summary>
   /// Define local variables
   /// </summary>
   private static AudioDevice _defaultOutputDevice;
   private static AudioDevice _defaultInputDevice;
   private static List<AudioDevice> _outputDevices = new List<AudioDevice>();
   private static List<AudioDevice> _inputDevices = new List<AudioDevice>();

   /// <summary>
   /// Boolean variable in which we store the value of whether FFmpeg is initialized or not.
   /// </summary>
   public static bool IsFFmpegInitialized { get; private set; }

   /// <summary>
   /// It stores FFmpeg's default or specified path.
   /// </summary>
   public static string? LibraryPath { get; private set; }

   /// <summary>
   /// Boolean variable in which we store the value of whether the PortAudio library is initialized or not.
   /// </summary>
   public static bool IsPortAudioInitialized { get; private set; }

    /// <summary>
    /// Boolean variable in which we store the value of whether the MiniAudio library is initialized or not.
    /// </summary>
    public static bool IsMiniAudioInitialized { get; private set; }

   /// <summary>
   /// Stores the default or specified path of PortAudio
   /// </summary>
   public static string? PortAudioPath { get; private set; }

    /// <summary>
    /// Stores the default or specified path of PortAudio
    /// </summary>
    public static string? MiniAudioPath { get; private set; }

    /// <summary>
    /// The api id of the selected system
    /// </summary>
    public static int  HostID { get; private set; }

   /// <summary>
   /// AudioDevice is the default output device used by the current system.
   /// </summary>
   /// <exception cref="OwnaudioException">Exception if PortAudio is not initialized.</exception>
   public static AudioDevice DefaultOutputDevice
   {
      get
      {
         Ensure.That<OwnaudioException>(IsPortAudioInitialized || IsMiniAudioInitialized, "Audio engine is not initialized.");
         return _defaultOutputDevice;
      }
   }

   /// <summary>
   /// AudioDevice is the default input device used by the current system.
   /// </summary>
   /// <exception cref="OwnaudioException">Exception if PortAudio is not initialized.</exception>
   public static AudioDevice DefaultInputDevice
   {
      get
      {
         Ensure.That<OwnaudioException>(IsPortAudioInitialized || IsMiniAudioInitialized, "Audio engine is not initialized.");
         return _defaultInputDevice;
      }
   }

   /// <summary>
   /// List of audio input devices available in the current system.
   /// <exception cref="OwnaudioException">Exception if PortAudio is not initialized.</exception>
   /// </summary>
   public static IReadOnlyCollection<AudioDevice> InputDevices
   {
      get
      {
         Ensure.That<OwnaudioException>(IsPortAudioInitialized || IsMiniAudioInitialized, "Audio engine is not initialized.");
         return _inputDevices;
      }
   }

   /// <summary>
   /// List of audio output devices available in the current system.
   /// <exception cref="OwnaudioException">Exception if PortAudio is not initialized.</exception>
   /// </summary>
   public static IReadOnlyCollection<AudioDevice> OutputDevices
   {
      get
      {
         Ensure.That<OwnaudioException>(IsPortAudioInitialized || IsMiniAudioInitialized, "Audio engine is not initialized.");
         return _outputDevices;
      }
   }
}
