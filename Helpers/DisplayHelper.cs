using System.Runtime.InteropServices;

namespace SteamOSConfigurator.Helpers
{
    /// <summary>
    /// Métodos compartidos para consulta de información de monitores.
    /// </summary>
    public static class DisplayHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplayDevices(string? lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);

        /// <summary>
        /// Obtiene el Device ID físico (hardware) de un monitor por su nombre de dispositivo.
        /// </summary>
        public static string ObtenerDeviceIdFisico(string deviceName)
        {
            DISPLAY_DEVICE dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(deviceName, 0, ref dd, 0)) return dd.DeviceID;
            return "";
        }
    }
}
