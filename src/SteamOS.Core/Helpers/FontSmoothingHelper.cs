using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SteamOSConfigurator.Helpers
{
    public static class FontSmoothingHelper
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        private const uint SPI_SETFONTSMOOTHING = 0x004B;
        private const uint SPI_SETFONTSMOOTHINGTYPE = 0x200B;
        private const uint SPI_SETFONTSMOOTHINGCONTRAST = 0x200D;
        private const uint SPI_SETFONTSMOOTHINGORIENTATION = 0x2013;

        private const uint SPIF_UPDATEINIFILE = 0x0001;
        private const uint SPIF_SENDCHANGE = 0x0002;
        private const uint SPIF_FLAGS = SPIF_UPDATEINIFILE | SPIF_SENDCHANGE;

        public static void ActivarClearType()
        {
            try
            {
                Logger.Log("[FontSmoothingHelper] Forzando activacion de suavizado de fuentes ClearType (Anti-aliasing)...");

                // 1. Aplicar en vivo via SystemParametersInfo (Win32)
                SystemParametersInfo(SPI_SETFONTSMOOTHING, 1, IntPtr.Zero, SPIF_FLAGS);                  // FontSmoothing = 1 (ON)
                SystemParametersInfo(SPI_SETFONTSMOOTHINGTYPE, 0, (IntPtr)2, SPIF_FLAGS);                // ClearType = 2
                SystemParametersInfo(SPI_SETFONTSMOOTHINGCONTRAST, 0, (IntPtr)1200, SPIF_FLAGS);         // Gamma = 1200
                SystemParametersInfo(SPI_SETFONTSMOOTHINGORIENTATION, 0, (IntPtr)1, SPIF_FLAGS);      // RGB = 1

                // 2. Persistir en el registro de la sesión activa (HKCU\Control Panel\Desktop)
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                {
                    if (key != null)
                    {
                        key.SetValue("FontSmoothing", "2", RegistryValueKind.String);
                        key.SetValue("FontSmoothingType", 2, RegistryValueKind.DWord);
                        key.SetValue("FontSmoothingGamma", 0, RegistryValueKind.DWord);
                        key.SetValue("FontSmoothingOrientation", 1, RegistryValueKind.DWord);
                        key.Flush();
                    }
                }

                Logger.Log("[FontSmoothingHelper] Suavizado ClearType aplicado exitosamente.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[FontSmoothingHelper] Error al activar ClearType: {ex.Message}");
            }
        }
    }
}