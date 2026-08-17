using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace SteamOSConfigurator.Helpers
{
    public static class MPOService
    {
        public static void AsegurarMPODesactivado()
        {
            try
            {
                SteamOSConfigurator.Logger.Log("[MPOService] Asegurando que MPO (Multiplane Overlays) esté desactivado...");
                bool restartDwm = false;

                // 1. DWM OverlayTestMode
                using (RegistryKey? key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\Dwm"))
                {
                    if (key != null)
                    {
                        object? val = key.GetValue("OverlayTestMode");
                        if (val == null || (int)val != 5)
                        {
                            key.SetValue("OverlayTestMode", 5, RegistryValueKind.DWord);
                            SteamOSConfigurator.Logger.Log("[MPOService] Clave DWM OverlayTestMode actualizada a 5.");
                            restartDwm = true;
                        }
                    }
                }

                // 2. GraphicsDrivers DisableOverlays
                using (RegistryKey? key = Registry.LocalMachine.CreateSubKey(@"System\CurrentControlSet\Control\GraphicsDrivers"))
                {
                    if (key != null)
                    {
                        object? val = key.GetValue("DisableOverlays");
                        if (val == null || (int)val != 1)
                        {
                            key.SetValue("DisableOverlays", 1, RegistryValueKind.DWord);
                            SteamOSConfigurator.Logger.Log("[MPOService] Clave GraphicsDrivers DisableOverlays actualizada a 1.");
                            restartDwm = true;
                        }
                    }
                }

                if (restartDwm)
                {
                    SteamOSConfigurator.Logger.Log("[MPOService] Cambios en el registro aplicados. (Nota: Puede requerir reiniciar el PC si es la primera vez que se aplica).");
                }
                else
                {
                    SteamOSConfigurator.Logger.Log("[MPOService] MPO ya estaba desactivado correctamente.");
                }
            }
            catch (Exception ex)
            {
                SteamOSConfigurator.Logger.Log($"[MPOService] Error al intentar desactivar MPO: {ex.Message} (¿Faltan permisos de administrador?)");
            }
        }

        public static void RestaurarMPO()
        {
            try
            {
                SteamOSConfigurator.Logger.Log("[MPOService] Restaurando MPO a valores por defecto de Windows...");

                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\Dwm", true))
                {
                    if (key != null && key.GetValue("OverlayTestMode") != null)
                    {
                        key.DeleteValue("OverlayTestMode");
                        SteamOSConfigurator.Logger.Log("[MPOService] Clave DWM OverlayTestMode eliminada.");
                    }
                }

                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Control\GraphicsDrivers", true))
                {
                    if (key != null && key.GetValue("DisableOverlays") != null)
                    {
                        key.DeleteValue("DisableOverlays");
                        SteamOSConfigurator.Logger.Log("[MPOService] Clave GraphicsDrivers DisableOverlays eliminada.");
                    }
                }
            }
            catch (Exception ex)
            {
                SteamOSConfigurator.Logger.Log($"[MPOService] Error al intentar restaurar MPO: {ex.Message}");
            }
        }
    }
}
