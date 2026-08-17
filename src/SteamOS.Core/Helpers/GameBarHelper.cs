using Microsoft.Win32;
using System;

namespace SteamOSConfigurator.Helpers
{
    public static class GameBarHelper
    {
        public static void DesactivarGameBarEnUsuarioActual()
        {
            try
            {
                using var keyDVR = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR");
                keyDVR?.SetValue("AppCaptureEnabled", 0, RegistryValueKind.DWord);

                using var keyBar = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameBar");
                keyBar?.SetValue("UseNexusForGameBarEnabled", 0, RegistryValueKind.DWord);
                keyBar?.SetValue("AllowAutoGameMode", 1, RegistryValueKind.DWord);

                Logger.Log("[GameBarHelper] GameBar desactivado en HKCU del usuario actual.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBarHelper] Advertencia al ajustar GameBar en registro: {ex.Message}");
            }
        }
    }
}
