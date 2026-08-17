using System;
using NvAPIWrapper;
using NvAPIWrapper.DRS;

namespace SteamOSConfigurator
{
    public static class NvidiaFastSync
    {
        public static void Activar()
        {
            try 
            {
                NVIDIA.Initialize();
                using (var session = DriverSettingsSession.CreateAndLoad()) 
                {
                    dynamic dynSession = session; 
                    dynamic? profile = null;
                    
                    try { profile = dynSession.BaseProfile; } catch { Logger.Log("Error al obtener perfil base de NVIDIA."); }
                    if (profile == null) { try { profile = dynSession.GlobalProfile; } catch { Logger.Log("Error al obtener perfil global de NVIDIA."); } }
                    
                    if (profile != null) 
                    {
                        try { profile.SetSetting(0x00A879CEu, 4u); } 
                        catch { try { profile.SetSetting(0x00A879CEu, 4u, 0); } catch (Exception ex) { Logger.Log($"Error al inyectar Fast Sync: {ex.Message}"); } }
                        
                        dynSession.Save(); 
                        Logger.Log("Fast Sync inyectado al instante.");
                    }
                }
            } 
            catch (Exception ex) { Logger.Log($"NVIDIA Fast Sync no disponible en este sistema: {ex.Message}"); }
        }

        public static void Restaurar()
        {
            try 
            {
                NVIDIA.Initialize();
                using (var session = DriverSettingsSession.CreateAndLoad()) 
                {
                    dynamic dynSession = session; 
                    dynamic? profile = null;
                    
                    try { profile = dynSession.BaseProfile; } catch { Logger.Log("Error al obtener perfil base de NVIDIA para restaurar."); }
                    if (profile == null) { try { profile = dynSession.GlobalProfile; } catch { Logger.Log("Error al obtener perfil global de NVIDIA para restaurar."); } }
                    
                    if (profile != null) 
                    {
                        try { profile.SetSetting(0x00A879CEu, 0u); } 
                        catch { try { profile.SetSetting(0x00A879CEu, 0u, 0); } catch (Exception ex) { Logger.Log($"Error al restaurar Fast Sync: {ex.Message}"); } }
                        
                        dynSession.Save();
                    }
                }
            } 
            catch (Exception ex) { Logger.Log($"NVIDIA Fast Sync no disponible en este sistema: {ex.Message}"); }
        }
    }
}
