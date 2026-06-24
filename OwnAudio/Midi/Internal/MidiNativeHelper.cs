using System.Runtime.InteropServices;
using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Helper routines that translate native port-enumeration results into managed
/// collections and free the underlying native memory.
/// </summary>
internal static class MidiNativeHelper
{
    /// <summary>
    /// Returns the names of all available MIDI input ports.
    /// </summary>
    public static IReadOnlyList<string> ListInputPortNames()
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_list_input_ports(
            out IntPtr names, out nuint count);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(ListInputPortNames));
        return ReadAndFreeNames(names, count);
    }

    /// <summary>
    /// Returns the names of all available MIDI output ports.
    /// </summary>
    public static IReadOnlyList<string> ListOutputPortNames()
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_list_output_ports(
            out IntPtr names, out nuint count);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(ListOutputPortNames));
        return ReadAndFreeNames(names, count);
    }

    /// <summary>
    /// Marshals a native array of UTF-8 string pointers into a managed list and
    /// releases the native array.
    /// </summary>
    /// <param name="names">
    /// Pointer to the native array of string pointers, or zero.
    /// </param>
    /// <param name="count">
    /// Number of entries in the array.
    /// </param>
    private static IReadOnlyList<string> ReadAndFreeNames(IntPtr names, nuint count)
    {
        if (names == IntPtr.Zero || count == 0)
        {
            return Array.Empty<string>();
        }

        int n = (int)count;
        var result = new List<string>(n);
        unsafe
        {
            IntPtr* array = (IntPtr*)names;
            for (int i = 0; i < n; i++)
            {
                result.Add(Marshal.PtrToStringUTF8(array[i]) ?? string.Empty);
            }
        }

        MidiNativeMethods.ownaudio_midi_v1_free_port_names(names, count);
        return result;
    }
}
