using OwnaudioNET.Core;

namespace OwnaudioNET.Sources;

/// <summary>
/// FileSource partial class - Channel Routing functionality
/// </summary>
public partial class FileSource
{
   private int[]? _outputChannelMapping = null;

   /// <summary>
   /// Specifies which output channels this source should write to.
   /// If null, writes to all channels starting from 0 (default behavior).
   /// Example: new int[] { 2, 3 } → write stereo source to output channels 2-3
   /// Example: new int[] { 0, 1, 4, 5 } → write 4-channel source to outputs 0,1,4,5
   /// </summary>
   /// <remarks>
   /// - Array length must match the source's channel count
   /// - Channel indices must be valid for the mixer's output configuration
   /// - Channels are zero-indexed
   /// </remarks>
   /// <exception cref="ArgumentException">Thrown when array length doesn't match source channel count</exception>
   public int[]? OutputChannelMapping
   {
      get => _outputChannelMapping;
      set
      {
         // Validate if not null
         if (value != null && value.Length != _streamInfo.Channels)
         {
            throw new ArgumentException(
                $"OutputChannelMapping array length ({value.Length}) must match source channel count ({_streamInfo.Channels})");
         }
         _outputChannelMapping = value;
      }
   }
}
