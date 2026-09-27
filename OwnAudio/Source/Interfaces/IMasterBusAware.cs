using OwnaudioNET.Mixing;

namespace OwnaudioNET.Interfaces;

/// <summary>
/// A master effect that has to know whose bus it sits on - SmartMaster's room measurement
/// plays its noise and hangs its mic on that mixer.
/// </summary>
internal interface IMasterBusAware
{
    /// <summary>
    /// Null once it came off the bus.
    /// </summary>
    void AttachMixer(AudioMixer? mixer);
}
