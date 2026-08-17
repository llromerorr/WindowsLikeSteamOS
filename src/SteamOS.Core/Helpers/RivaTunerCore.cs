using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SteamOSConfigurator.Helpers;
using SteamOSConfigurator.Services;

namespace SteamOSConfigurator
{
    public static class RivaTunerCore
    {
        private static readonly string RutaInstalador = AppPaths.RutaInstaladorRTSS;
        private static readonly string RutaExe = AppPaths.RutaExeRTSS;
        private static readonly string RutaPerfilGlobal = AppPaths.RutaPerfilGlobalRTSS;

        public static void AsegurarInstalacionSilenciosa() 
        { 
            if (!File.Exists(RutaExe)) 
            { 
                Logger.Log("RTSS no detectado. Intentando instalar via DependencyService...");
                var depService = new DependencyService();
                Task.Run(async () => await depService.InstalarRtssAsync()).Wait();
            } 
        }

        public static int LimiteFPSActual { get; private set; } = 60;
        public static int NivelOSDActual { get; private set; } = 0;

        public static void ForzarModoConsola(int limiteFPS) 
        { 
            AplicarConfiguracion(limiteFPS, NivelOSDActual);
        }

        public static string OsdEngineActual { get; private set; } = "RTSS";

        private static System.Threading.Timer? _osdTimer;

        public static void IniciarOSDBackground()
        {
            if (_osdTimer == null)
            {
                _osdTimer = new System.Threading.Timer(_ => 
                {
                    int nivel = NivelOSDActual;
                    if (nivel <= 0 || OsdEngineActual != "RTSS")
                    {
                        RTSSSharedMemory.UpdateOSD("");
                        return;
                    }

                    double cpu = SysInfo.GetCpuUsage();
                    float cpuTemp = SysInfo.GetCpuTemp();
                    double ram = SysInfo.GetRamUsage();
                    float gpuLoad = SysInfo.GetGpuLoad();
                    float gpuTemp = SysInfo.GetGpuTemp();

                    string osdText = "";
                    if (nivel == 1)
                    {
                        osdText = "<C=66C0F4>FPS";
                    }
                    else if (nivel == 2)
                    {
                        osdText = $"<C=66C0F4>CPU <C=FFFFFF>{cpu:F0}%   <C=66C0F4>GPU <C=FFFFFF>{gpuLoad:F0}%";
                    }
                    else if (nivel == 3)
                    {
                        osdText = $"<C=66C0F4>CPU <C=FFFFFF>{cpu:F0}% <C=FF8800>{cpuTemp:F0}°C   <C=66C0F4>GPU <C=FFFFFF>{gpuLoad:F0}% <C=FF8800>{gpuTemp:F0}°C";
                    }
                    else if (nivel >= 4)
                    {
                        float cpuFan = SysInfo.GetCpuFanRPM();
                        float gpuFan = SysInfo.GetGpuFanRPM();
                        string cpuFanStr = cpuFan > 0 ? $" <C=88FF88>{cpuFan:F0}RPM" : "";
                        string gpuFanStr = gpuFan > 0 ? $" <C=88FF88>{gpuFan:F0}RPM" : "";
                        osdText = $"<C=66C0F4>CPU <C=FFFFFF>{cpu:F0}% <C=FF8800>{cpuTemp:F0}°C{cpuFanStr}\n<C=66C0F4>GPU <C=FFFFFF>{gpuLoad:F0}% <C=FF8800>{gpuTemp:F0}°C{gpuFanStr}\n<C=66C0F4>RAM <C=FFFFFF>{ram:F1}GB";
                    }

                    RTSSSharedMemory.UpdateOSD(osdText);
                }, null, 0, 1000);
            }
        }

        public static void AplicarConfiguracion(int limiteFPS, int nivelOSD, string? osdEngine = null) 
        { 
            try 
            { 
                if (osdEngine == null) {
                    var conf = ConfigManager.CargarConfiguracion();
                    osdEngine = conf.OsdEngine;
                }
                LimiteFPSActual = limiteFPS;
                NivelOSDActual = nivelOSD;
                OsdEngineActual = osdEngine;
                
                IniciarOSDBackground();

                string dirPerfiles = Path.GetDirectoryName(RutaPerfilGlobal)!; 
                if (!Directory.Exists(dirPerfiles)) Directory.CreateDirectory(dirPerfiles); 
                
                int enableOSD = (nivelOSD > 0 && osdEngine == "RTSS") ? 1 : 0; 
                int showFramerate = (nivelOSD > 0 && osdEngine == "RTSS") ? 1 : 0;
                int showFrametime = 0;

                string configuracion = 
                    "[Settings]\nName=Global\n\n" +
                    "[Hooking]\nEnableHooking=1\nHookLevel=1\nInjectionDelay=15000\n\n" +
                    $"[Framerate]\nLimit={limiteFPS}\nLimitDenominator=1\n\n" +
                    $"[OSD]\nEnableOSD={enableOSD}\nShowStat={enableOSD}\nShowFramerate={showFramerate}\nShowFrametime={showFrametime}\nPlacementX=15\nPlacementY=15\nPositionX=1\nPositionY=1\nZoomRatio=1\n"; 
                
                File.WriteAllText(RutaPerfilGlobal, configuracion); 
                
                // Auto-configurar llaves de Registro de Windows
                try
                {
                    string rutaConfig = Path.Combine(dirPerfiles, "Config");
                    var lineas = File.Exists(rutaConfig) ? File.ReadAllLines(rutaConfig).ToList() : new List<string> { "[Master]" };
                    bool foundShowOSD = false, foundHooking = false;
                    for (int i = 0; i < lineas.Count; i++)
                    {
                        if (lineas[i].StartsWith("ShowOSD="))
                        {
                            lineas[i] = $"ShowOSD=0";
                            foundShowOSD = true;
                        }
                        if (lineas[i].StartsWith("EnableHooking="))
                        {
                            lineas[i] = "EnableHooking=1";
                            foundHooking = true;
                        }
                    }
                    if (!foundShowOSD) lineas.Add($"ShowOSD=0");
                    if (!foundHooking) lineas.Add("EnableHooking=1");
                    File.WriteAllLines(rutaConfig, lineas);
                }
                catch { }

                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Unwinder\RTSS"))
                    {
                        if (key != null)
                        {
                            key.SetValue("ShowOSD", enableOSD, Microsoft.Win32.RegistryValueKind.DWord);
                            key.SetValue("EnableOSD", enableOSD, Microsoft.Win32.RegistryValueKind.DWord);
                            key.SetValue("StartWithWindows", 1, Microsoft.Win32.RegistryValueKind.DWord);
                            key.SetValue("StartMinimized", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        }
                    }
                }
                catch { }

                Logger.Log($"[RTSS] Perfil y Registro actualizados: FPS={limiteFPS}, Nivel OSD={nivelOSD}");

                // Llamar a UpdateProfiles de RTSS
                try
                {
                    string dllPath = Path.Combine(Path.GetDirectoryName(RutaExe)!, "RTSSHooks64.dll");
                    if (Environment.Is64BitProcess && File.Exists(dllPath))
                    {
                        var dllHandle = NativeMethods.LoadLibrary(dllPath);
                        if (dllHandle != IntPtr.Zero)
                        {
                            var procAddress = NativeMethods.GetProcAddress(dllHandle, "UpdateProfiles");
                            if (procAddress != IntPtr.Zero)
                            {
                                var updateProfiles = Marshal.GetDelegateForFunctionPointer<NativeMethods.UpdateProfilesDelegate>(procAddress);
                                updateProfiles();
                                Logger.Log("[RTSS] UpdateProfiles ejecutado exitosamente.");
                            }
                            NativeMethods.FreeLibrary(dllHandle);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[RTSS] Error llamando a UpdateProfiles: {ex.Message}");
                }

                DespertarFantasma();
            } 
            catch (Exception ex) { Logger.Log($"Error al actualizar RTSS: {ex.Message}"); } 
        }

        public static void DespertarFantasma() 
        { 
            if (File.Exists(RutaExe)) 
            { 
                var procesos = Process.GetProcessesByName("RTSS"); 
                bool tieneProcesos = procesos.Length > 0;
                foreach (var p in procesos) p.Dispose();
                if (!tieneProcesos)
                {
                    using (var pStart = Process.Start(new ProcessStartInfo 
                    { 
                        FileName = RutaExe, 
                        WorkingDirectory = Path.GetDirectoryName(RutaExe),
                        UseShellExecute = true
                    })) {} 
                    Logger.Log("[RTSS] Lanzado en background silenciosamente.");
                } 
            } 
        }

        public static void ApagarFantasma() 
        { 
            foreach (var proc in Process.GetProcessesByName("RTSS")) { try { proc.Kill(); proc.Dispose(); } catch { } } 
            foreach (var proc in Process.GetProcessesByName("rtss")) { try { proc.Kill(); proc.Dispose(); } catch { } } 
        }
    }

    public static class MSIAfterburnerCore
    {
        private static readonly string RutaExe = AppPaths.RutaExeMSIAfterburner;
        private static readonly string RutaExeFallback = @"C:\Program Files\MSI Afterburner\MSIAfterburner.exe";

        public static void AsegurarEjecucion()
        {
            string rutaFinal = File.Exists(RutaExe) ? RutaExe : (File.Exists(RutaExeFallback) ? RutaExeFallback : "");
            if (!string.IsNullOrEmpty(rutaFinal))
            {
                var procesos = Process.GetProcessesByName("MSIAfterburner");
                bool tieneProcesos = procesos.Length > 0;
                foreach (var p in procesos) p.Dispose();
                if (!tieneProcesos)
                {
                    try
                    {
                        using (var pStart = Process.Start(new ProcessStartInfo 
                        { 
                            FileName = rutaFinal, 
                            WorkingDirectory = Path.GetDirectoryName(rutaFinal),
                            UseShellExecute = true
                        })) {} 
                        Logger.Log("[MSI Afterburner] Lanzado silenciosamente.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[MSI Afterburner] Error al lanzar: {ex.Message}");
                    }
                }
            }
            else
            {
                Logger.Log("[MSI Afterburner] No encontrado, no se puede iniciar.");
            }
        }
    }
}
