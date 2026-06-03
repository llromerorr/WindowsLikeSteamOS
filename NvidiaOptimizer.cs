using System;
using System.Diagnostics;
using NvAPIWrapper;
using NvAPIWrapper.DRS;

namespace SteamOSConfigurator
{
    public static class NvidiaOptimizer
    {
        public static void ActivarModoConsola(uint limiteFPS = 30)
        {
            try
            {
                NVIDIA.Initialize();
                using (var session = DriverSettingsSession.CreateAndLoad())
                {
                    // Convertimos la sesión en un objeto dinámico para saltar las reglas del compilador
                    dynamic dynSession = session;
                    dynamic profile = null;
                    
                    // Probamos los nombres de propiedad que usan las diferentes versiones de la librería
                    try { profile = dynSession.BaseProfile; } catch { }
                    if (profile == null) { try { profile = dynSession.GlobalProfile; } catch { } }

                    if (profile != null)
                    {
                        // ── 0x00A879CE = VSyncControl | 4u = Fast Sync ──
                        // Probamos la sintaxis moderna (3 argumentos) y si falla, la antigua (2 argumentos)
                        try { profile.SetSetting(0x00A879CE, 0, 4u); } 
                        catch { profile.SetSetting(0x00A879CE, 4u); }

                        // ── 0x278311B0 = FrameRateLimiter ──
                        try { profile.SetSetting(0x278311B0, 0, limiteFPS); } 
                        catch { profile.SetSetting(0x278311B0, limiteFPS); }

                        dynSession.Save(); // Escribimos en el hardware
                    }
                }
            }
            catch (Exception ex)
            {
                // Si la PC usa AMD o Intel, fallará en silencio sin colgar tu aplicación
                Debug.WriteLine($"Nota NVAPI: Fallo silencioso (¿Gráfica AMD/Intel?) - {ex.Message}");
            }
        }

        public static void RestaurarModoNormal()
        {
            try
            {
                NVIDIA.Initialize();
                using (var session = DriverSettingsSession.CreateAndLoad())
                {
                    dynamic dynSession = session;
                    dynamic profile = null;
                    
                    try { profile = dynSession.BaseProfile; } catch { }
                    if (profile == null) { try { profile = dynSession.GlobalProfile; } catch { } }

                    if (profile != null)
                    {
                        // 0u = Controlado por la aplicación / Off
                        try { profile.SetSetting(0x00A879CE, 0, 0u); } catch { profile.SetSetting(0x00A879CE, 0u); }
                        try { profile.SetSetting(0x278311B0, 0, 0u); } catch { profile.SetSetting(0x278311B0, 0u); }

                        dynSession.Save();
                    }
                }
            }
            catch { }
        }
    }
}