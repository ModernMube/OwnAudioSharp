using System.Runtime.InteropServices;
using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Port enumeration glue — pulls the native name arrays over and frees them.
/// </summary>
internal static class MidiNativeHelper
{
    /// <summary>
    /// Every MIDI input port name the backend knows about.
    /// </summary>
    public static IReadOnlyList<string> ListInputPortNames()
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_list_input_ports(out IntPtr names, out nuint count);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(ListInputPortNames));
        return _readAndFreeNames(names, count);
    }

    /// <summary>
    /// Same, for outputs.
    /// </summary>
    public static IReadOnlyList<string> ListOutputPortNames()
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_list_output_ports(out IntPtr names, out nuint count);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(ListOutputPortNames));
        return _readAndFreeNames(names, count);
    }

    /// <summary>
    /// Copies the UTF-8 pointer array into a managed list, then frees the native side.
    /// </summary>
    private static unsafe IReadOnlyList<string> _readAndFreeNames(IntPtr names, nuint count)
    {
        if(names == IntPtr.Zero || count == 0) return Array.Empty<string>();

        int _count = (int)count;
        var _result = new List<string>(_count);
        IntPtr* _array = (IntPtr*)names;
        for (int i = 0; i < _count; i++)
            _result.Add(Marshal.PtrToStringUTF8(_array[i]) ?? string.Empty);

        MidiNativeMethods.ownaudio_midi_v1_free_port_names(names, count);
        return _result;
    }
}
