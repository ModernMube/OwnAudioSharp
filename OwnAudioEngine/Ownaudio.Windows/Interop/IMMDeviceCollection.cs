using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Windows.Interop
{
    /// <summary>
    /// COM interface for IMMDeviceCollection.
    /// Represents a collection of multimedia device resources.
    /// </summary>
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        /// <summary>
        /// Gets the number of devices in the collection.
        /// </summary>
        /// <param name="pcDevices">Receives the device count.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetCount(out uint pcDevices);

        /// <summary>
        /// Gets a device from the collection by index.
        /// </summary>
        /// <param name="nDevice">Zero-based device index.</param>
        /// <param name="ppDevice">Receives the IMMDevice interface pointer.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int Item(uint nDevice, out IMMDevice ppDevice);
    }
}
