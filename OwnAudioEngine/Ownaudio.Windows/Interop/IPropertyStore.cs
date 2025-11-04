using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Windows.Interop
{
    /// <summary>
    /// COM interface for IPropertyStore.
    /// Used to read device properties like friendly name.
    /// </summary>
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        /// <summary>
        /// Gets the number of properties in the store.
        /// </summary>
        /// <param name="cProps">Receives the property count.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetCount(out uint cProps);

        /// <summary>
        /// Gets a property key by index.
        /// </summary>
        /// <param name="iProp">Zero-based property index.</param>
        /// <param name="pkey">Receives the property key.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetAt(uint iProp, out PropertyKey pkey);

        /// <summary>
        /// Gets the value of a property.
        /// </summary>
        /// <param name="key">The property key.</param>
        /// <param name="pv">Receives the property value.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant pv);

        /// <summary>
        /// Sets the value of a property.
        /// </summary>
        /// <param name="key">The property key.</param>
        /// <param name="propvar">The property value to set.</param>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant propvar);

        /// <summary>
        /// Commits changes to the property store.
        /// </summary>
        /// <returns>HRESULT error code.</returns>
        [PreserveSig]
        int Commit();
    }

    /// <summary>
    /// Represents a property key (GUID + ID).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        /// <summary>
        /// The format ID (GUID) of the property.
        /// </summary>
        public Guid fmtid;

        /// <summary>
        /// The property ID.
        /// </summary>
        public uint pid;

        /// <summary>
        /// Initializes a new instance of the <see cref="PropertyKey"/> struct.
        /// </summary>
        /// <param name="fmtid">The format ID.</param>
        /// <param name="pid">The property ID.</param>
        public PropertyKey(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    /// <summary>
    /// PROPVARIANT structure for property values.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        /// <summary>
        /// Variant type.
        /// </summary>
        [FieldOffset(0)]
        public ushort vt;

        /// <summary>
        /// Reserved field 1.
        /// </summary>
        [FieldOffset(2)]
        public ushort wReserved1;

        /// <summary>
        /// Reserved field 2.
        /// </summary>
        [FieldOffset(4)]
        public ushort wReserved2;

        /// <summary>
        /// Reserved field 3.
        /// </summary>
        [FieldOffset(6)]
        public ushort wReserved3;

        /// <summary>
        /// String pointer value.
        /// </summary>
        [FieldOffset(8)]
        public IntPtr pwszVal;

        /// <summary>
        /// Gets the string value from the PROPVARIANT.
        /// </summary>
        /// <returns>The string value, or null if not a string type.</returns>
        public string GetStringValue()
        {
            if (vt == 31) // VT_LPWSTR
            {
                return Marshal.PtrToStringUni(pwszVal);
            }
            return null;
        }
    }

    /// <summary>
    /// Well-known property keys for audio devices.
    /// </summary>
    internal static class PropertyKeys
    {
        /// <summary>
        /// PKEY_Device_FriendlyName - The friendly name of the device.
        /// </summary>
        public static readonly PropertyKey PKEY_Device_FriendlyName =
            new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

        /// <summary>
        /// PKEY_DeviceInterface_FriendlyName - The friendly name of the device interface.
        /// </summary>
        public static readonly PropertyKey PKEY_DeviceInterface_FriendlyName =
            new PropertyKey(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);

        /// <summary>
        /// PKEY_Device_DeviceDesc - The device description.
        /// </summary>
        public static readonly PropertyKey PKEY_Device_DeviceDesc =
            new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    }

    /// <summary>
    /// Helper methods for property store operations.
    /// </summary>
    internal static class PropertyStoreHelper
    {
        /// <summary>
        /// STGM_READ constant for opening property stores in read mode.
        /// </summary>
        public const uint STGM_READ = 0x00000000;
    }
}
