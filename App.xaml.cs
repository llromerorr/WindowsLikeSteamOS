using System;
using System.Windows;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Windows.Interop;
using System.Security.Principal;
using System.Text;
using SteamOSConfigurator.Models;
using SteamOSConfigurator.Helpers;
using SteamOSConfigurator.Services;

// Librerías de NVIDIA
using NvAPIWrapper;
using NvAPIWrapper.DRS;

namespace SteamOSConfigurator
{
    // ── TELEMETRÍA (LOGGER) ──
    public static class Logger
    {
        public static bool HabilitarDebug = true; 
        private static readonly string RutaLog = AppPaths.LogFile;

        public static void Log(string mensaje) 
        { 
            if (!HabilitarDebug) return; 
            try 
            { 
                string linea = $"[{DateTime.Now:HH:mm:ss.fff}] {mensaje}\n"; 
                File.AppendAllText(RutaLog, linea); 
            } 
            catch { } 
        }
    }

    // ── MÓDULO 1: V-SYNC ULTRA RÁPIDO (NVIDIA FAST SYNC) ──
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
                    dynamic profile = null;
                    
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
                    dynamic profile = null;
                    
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

    // ── MÓDULO 2: INYECTOR DE FRAME PACING (RIVA TUNER) ──
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

        public static string OsdEngineActual { get; private set; } = "WPF";

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
                    float diskLoad = (float)Math.Clamp(SysInfo.GetDiskReadWriteMBps(), 0, 100);

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

        public static void AplicarConfiguracion(int limiteFPS, int nivelOSD, string osdEngine = null) 
        { 
            try 
            { 
                if (osdEngine == null) {
                    var conf = SteamOSConfigurator.Helpers.ConfigManager.CargarConfiguracion();
                    osdEngine = conf.OsdEngine;
                }
                LimiteFPSActual = limiteFPS;
                NivelOSDActual = nivelOSD;
                OsdEngineActual = osdEngine;
                
                // Eliminado el soporte de VentanaHUD (Panel WPF).
                
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
                
                // Auto-configurar llaves de Registro de Windows (para no requerir clicks del usuario)
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

                // Auto-configurar llaves de Registro de Windows (para no requerir clicks del usuario)
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

                // Llamar a UpdateProfiles de RTSS para que recargue el archivo Global sin tener que reiniciar
                try
                {
                    string dllPath = Path.Combine(Path.GetDirectoryName(RutaExe)!, "RTSSHooks64.dll");
                    if (Environment.Is64BitProcess && File.Exists(dllPath))
                    {
                        var dllHandle = App.LoadLibrary(dllPath);
                        if (dllHandle != IntPtr.Zero)
                        {
                            var procAddress = App.GetProcAddress(dllHandle, "UpdateProfiles");
                            if (procAddress != IntPtr.Zero)
                            {
                                var updateProfiles = Marshal.GetDelegateForFunctionPointer<App.UpdateProfilesDelegate>(procAddress);
                                updateProfiles();
                                Logger.Log("[RTSS] UpdateProfiles ejecutado exitosamente.");
                            }
                            App.FreeLibrary(dllHandle);
                        }
                    }
                    else
                    {
                        Logger.Log("[RTSS] No se pudo encontrar RTSSHooks64.dll o no es proceso 64-bit.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[RTSS] Error llamando a UpdateProfiles: {ex.Message}");
                }

                // Aseguramos que RTSS esté corriendo, pero NO lo reiniciamos para no perder el hook en el juego
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
                            UseShellExecute = true,
                            // Parámetros para forzar inicio minimizado si es necesario, 
                            // aunque MSI AB suele iniciar minimizado si está configurado
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

    // ── EL ORQUESTADOR PRINCIPAL ──
    public partial class App : System.Windows.Application
    {
        // ── P/INVOKES Y CONSTANTES DE WINDOWS API ──
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag); 
        static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] static extern bool SystemParametersInfoTimeout(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
        
        [StructLayout(LayoutKind.Sequential)] 
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        
        const uint SPI_SETWORKAREA = 0x002F; 
        const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001; 
        const uint SPIF_SENDCHANGE = 0x0002; 
        const uint SPIF_UPDATEINIFILE = 0x0001;
        
        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", SetLastError = true)] static extern bool ExitWindowsEx(uint uFlags, uint dwReason);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); 
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hWnd);
        
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)] public static extern IntPtr LoadLibrary(string lpFileName);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeLibrary(IntPtr hModule);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void UpdateProfilesDelegate();
        
        const int SW_HIDE = 0; 
        const uint EVENT_SYSTEM_FOREGROUND = 3; 
        const uint WINEVENT_OUTOFCONTEXT = 0;

        const int WM_POWERBROADCAST = 0x0218;
        const int PBT_APMSUSPEND = 0x0004;
        const int PBT_APMRESUMESUSPEND = 0x0007;

        // ── SERVICIOS EXTRAÍDOS ──
        private readonly IAudioService _audioService = new AudioService();
        private readonly ISteamService _steamService = new SteamService();
        private readonly IDisplayService _displayService = new DisplayService();
        private readonly IKeyboardHookService _keyboardHookService = new KeyboardHookService();
        private readonly IGpuScalingService _gpuScalingService = new NvidiaGpuScalingService();
        private readonly IPowerService _powerService = new PowerService();
        private readonly IDependencyService _dependencyService = new DependencyService();

        // ── VARIABLES DE CONTROL DE ESTADO ──
        private volatile bool _modoEscritorio = false; 
        private volatile bool _cerrandoSesion = false;
        private HwndSource? _hwndSourceShell = null;
        private IntPtr _hwndShell = IntPtr.Zero;
        private System.Threading.Timer? _debounceTimer; 
        private int _suppressDisplayChange = 0; 
        private readonly object _timerLock = new object();
        private WinEventDelegate? _winEventDelegate; 
        private IntPtr _hWinEventHook = IntPtr.Zero;
        private SteamOSConfigurator.Services.WindowWatcherService? _windowWatcherService;

        public static volatile bool ReinstalandoOReinicioSteam = false;

        // ── ARRANQUE Y PRIVILEGIOS ──
        protected override void OnExit(ExitEventArgs e)
        {
            if (_windowWatcherService != null)
            {
                _windowWatcherService.Dispose();
                _windowWatcherService = null;
            }
            base.OnExit(e);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ev) => 
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "CrashLog.txt"), ev.ExceptionObject.ToString());
            };
            
            this.DispatcherUnhandledException += (s, ev) => 
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "CrashLogWPF.txt"), ev.Exception.ToString());
                ev.Handled = true;
            };

            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
            base.OnStartup(e);
            
            if (!EsAdministrador()) 
            { 
                try 
                { 
                    using (var pStart = Process.Start(new ProcessStartInfo 
                    { 
                        UseShellExecute = true, 
                        WorkingDirectory = Environment.CurrentDirectory, 
                        FileName = Environment.ProcessPath, 
                        Arguments = e.Args.Length > 0 ? string.Join(" ", e.Args) : "", 
                        Verb = "runas" 
                    })) {} 
                } 
                catch (Exception ex)
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "UAC_Error.txt"), ex.ToString());
                } 
                Environment.Exit(0);  
                return; 
            }

            SystemParametersInfoTimeout(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE | SPIF_UPDATEINIFILE);

            if (e.Args.Length > 0 && e.Args[0] == "-shell") 
            { 
                // En modo Shell (-shell), operamos como servicio de fondo persistente
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                _ = EjecutarModoConsolaAsync(); 
            } 
            else 
            { 
                // En modo Configuración Normal (sin -shell), se cierra limpiamente al cerrar MainWindow
                Application.Current.ShutdownMode = ShutdownMode.OnLastWindowClose;
                MainWindow main = new MainWindow();
                main.Closed += (s, ev) => 
                {
                    Application.Current.Shutdown();
                };
                main.Show(); 
            }
        }

        private bool EsAdministrador() 
        { 
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) 
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); 
        }

        private void ForzarRegistroEscaladoNVIDIA() 
        { 
            try 
            { 
                Logger.Log("[ForzarRegistroEscaladoNVIDIA] Aplicando forzado de escalado en registro de Windows...");
                using (var config = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration", true)) 
                { 
                    if (config == null)
                    {
                        Logger.Log("[ForzarRegistroEscaladoNVIDIA] ADVERTENCIA: La clave de registro GraphicsDrivers\\Configuration no existe o no es accesible.");
                        return; 
                    }
                    string[] subKeys = config.GetSubKeyNames();
                    Logger.Log($"[ForzarRegistroEscaladoNVIDIA] Encontradas {subKeys.Length} configuraciones de pantalla.");
                    foreach (string sub in subKeys) 
                    { 
                        using (var key0 = config.OpenSubKey(sub + @"\00\00", true)) 
                        { 
                            if (key0 == null) continue; 
                            try 
                            { 
                                key0.SetValue("Scaling", 3, RegistryValueKind.DWord); 
                                key0.SetValue("ScalingMode", 3, RegistryValueKind.DWord); 
                                Logger.Log($"[ForzarRegistroEscaladoNVIDIA] Escalado forzado a Completo (3) en subclave: {sub}\\00\\00");
                            } 
                            catch (Exception ex) 
                            { 
                                Logger.Log($"[ForzarRegistroEscaladoNVIDIA] Error al establecer escalado en subkey {sub}: {ex.Message}"); 
                            } 
                        } 
                    } 
                } 
            } 
            catch (Exception ex) { Logger.Log($"[ForzarRegistroEscaladoNVIDIA] Error al forzar registro de escalado NVIDIA: {ex.Message}"); } 
        }

        private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime) 
        {
            if (hwnd == IntPtr.Zero || _modoEscritorio || _steamService.JuegoActivoHwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out uint pid); 
            if (pid == 0) return;
            
            try 
            { 
                using (var proc = Process.GetProcessById((int)pid))
                {
                    string pName = proc.ProcessName.ToLower(); 
                    if (pName == "steam" || pName == "steamwebhelper") 
                    { 
                        int length = GetWindowTextLength(hwnd);
                        string titulo = "";
                        if (length > 0)
                        {
                            StringBuilder sb = new StringBuilder(length + 1);
                            GetWindowText(hwnd, sb, sb.Capacity);
                            titulo = sb.ToString();
                        }
                        Logger.Log($"[WinEventCallback] Ocultando ventana de Steam en segundo plano durante juego: HWND={hwnd.ToInt64():X}, Title=\"{titulo}\", PID={pid}");
                        ShowWindow(hwnd, SW_HIDE); 
                        _steamService.AddVentanaSteamOculta(hwnd); 
                        SetForegroundWindow(_steamService.JuegoActivoHwnd); 
                    } 
                } 
            } 
            catch (Exception ex)
            {
                Logger.Log($"[WinEventCallback] Error procesando evento de ventana: {ex.Message}");
            }
        }

        // ── MOTOR PRINCIPAL DEL KIOSCO ──
        private async Task EjecutarModoConsolaAsync()
        {
            try 
            {
                Logger.Log("[EjecutarModoConsolaAsync] Iniciando modo consola (Shell)...");
                RegistrarAtajoTecladoSilencioso(); 
                Logger.Log("[EjecutarModoConsolaAsync] Atajo de teclado silencioso registrado.");

                GameBarHelper.DesactivarGameBarEnUsuarioActual();
                
                var config = CargarConfig();
                if (config == null) 
                { 
                    Logger.Log("[EjecutarModoConsolaAsync] ERROR: La configuración es nula. Cerrando sesión.");
                    CerrarSesionRapido(); 
                    return; 
                }
                Logger.Log($"[EjecutarModoConsolaAsync] Configuración cargada: Monitor={config.MonitorDeviceName}, Resolucion={config.ResolucionWidth}x{config.ResolucionHeight}@{config.RefreshRate}Hz, LimitFPS={config.LimiteFPS}");

                // Activar plan de máximo rendimiento y prevenir suspensión automática
                _powerService.ActivarPlanMaximoRendimiento();
                _powerService.PrevenirSuspensionAutomatica();
                Logger.Log("[EjecutarModoConsolaAsync] Plan de máximo rendimiento y suspensión configurados.");

                // Exorcismo previo
                Logger.Log("[EjecutarModoConsolaAsync] Limpiando procesos de Steam previos...");
                foreach (var p in Process.GetProcessesByName("steam")) { try { p.Kill(); p.Dispose(); Logger.Log($"[EjecutarModoConsolaAsync] Proceso 'steam' (PID {p.Id}) terminado."); } catch (Exception ex) { Logger.Log($"[EjecutarModoConsolaAsync] Error al matar 'steam': {ex.Message}"); } }
                foreach (var p in Process.GetProcessesByName("steamwebhelper")) { try { p.Kill(); p.Dispose(); Logger.Log($"[EjecutarModoConsolaAsync] Proceso 'steamwebhelper' (PID {p.Id}) terminado."); } catch (Exception ex) { Logger.Log($"[EjecutarModoConsolaAsync] Error al matar 'steamwebhelper': {ex.Message}"); } }
                await Task.Delay(1000); 

                Logger.Log("[EjecutarModoConsolaAsync] Configurando área de trabajo (SPI_SETWORKAREA)...");
                var workArea = new RECT { Left = 0, Top = 0, Right = config.ResolucionWidth, Bottom = config.ResolucionHeight };
                bool resWorkArea = SystemParametersInfo(SPI_SETWORKAREA, 0, ref workArea, SPIF_SENDCHANGE);
                Logger.Log($"[EjecutarModoConsolaAsync] Resultado área de trabajo: {resWorkArea}");
                
                // ── CONFIGURACIÓN DINÁMICA DE RENDIMIENTO ──
                if (config.ForzarFastSync) 
                {
                    Logger.Log("[EjecutarModoConsolaAsync] Activando Nvidia Fast Sync...");
                    NvidiaFastSync.Activar();
                }
                else 
                {
                    Logger.Log("[EjecutarModoConsolaAsync] Restaurando Nvidia Fast Sync...");
                    NvidiaFastSync.Restaurar();
                }
                
                Logger.Log("[EjecutarModoConsolaAsync] Limpiando instancias previas de RTSS y MSI Afterburner...");
                foreach (var p in Process.GetProcessesByName("RTSS")) { try { p.Kill(); p.Dispose(); } catch { } }
                foreach (var p in Process.GetProcessesByName("rtss")) { try { p.Kill(); p.Dispose(); } catch { } }
                foreach (var p in Process.GetProcessesByName("MSIAfterburner")) { try { p.Kill(); p.Dispose(); } catch { } }
                System.Threading.Thread.Sleep(800); // Dar un margen para que Windows cierre los procesos por completo
                
                Logger.Log("[EjecutarModoConsolaAsync] Configurando RivaTuner (RTSS)...");
                RivaTunerCore.AsegurarInstalacionSilenciosa(); 
                RivaTunerCore.ForzarModoConsola(config.LimiteFPS); 
                RivaTunerCore.DespertarFantasma(); 
                Logger.Log("[EjecutarModoConsolaAsync] RivaTuner configurado.");

                Logger.Log("[EjecutarModoConsolaAsync] Iniciando WindowWatcherService (Gestión Anti-Cheat Borderless)...");
                _windowWatcherService = new SteamOSConfigurator.Services.WindowWatcherService();
                _windowWatcherService.Start();

                Logger.Log("[EjecutarModoConsolaAsync] Asegurando MSI Afterburner (Sensores)...");
                MSIAfterburnerCore.AsegurarEjecucion();
                
                Logger.Log("[EjecutarModoConsolaAsync] Aplicando configuración inicial (HUD y Límites)...");
                RivaTunerCore.AplicarConfiguracion(config.LimiteFPS, config.IndexOSD);

                Logger.Log("[EjecutarModoConsolaAsync] Desactivando Multiplane Overlays (MPO)...");
                Helpers.MPOService.AsegurarMPODesactivado();
                // ───────────────────────────────────────────

                Logger.Log("[EjecutarModoConsolaAsync] Forzando registro de escalado NVIDIA...");
                ForzarRegistroEscaladoNVIDIA();
                
                if (config.AudioDispositivo != null)
                {
                    Logger.Log($"[EjecutarModoConsolaAsync] Configurando audio predeterminado: {config.AudioDispositivo}...");
                    _audioService.EstablecerDispositivoPorDefecto(config.AudioDispositivo);
                }
 
                Logger.Log("[EjecutarModoConsolaAsync] Aplicando aislamiento de pantalla...");
                _displayService.AislarPantalla(config, _gpuScalingService);
                Logger.Log("[EjecutarModoConsolaAsync] Pantalla aislada.");

                if (config.EmuladorActivado) 
                {
                    Logger.Log("[EjecutarModoConsolaAsync] Iniciando traductor de mando...");
                    _ = TraductorMando.IniciarAsync();
                }
                
                Logger.Log("[EjecutarModoConsolaAsync] Iniciando retraso de seguridad de 4 segundos...");
                await Task.Delay(4000); 
                Logger.Log("[EjecutarModoConsolaAsync] Fin de retraso de seguridad.");

                Logger.Log("[EjecutarModoConsolaAsync] Activando hook de teclado...");
                _keyboardHookService.IniciarHook(() => !_modoEscritorio);
                Logger.Log("[EjecutarModoConsolaAsync] Hook de teclado activado.");

                string rutaSteam = _steamService.ObtenerRutaSteam(); 
                if (string.IsNullOrEmpty(rutaSteam)) 
                { 
                    Logger.Log("[EjecutarModoConsolaAsync] ERROR: No se encontró la ruta de Steam. Cerrando sesión.");
                    CerrarSesionRapido(); 
                    return; 
                }
                
                Logger.Log($"[EjecutarModoConsolaAsync] Ruta de Steam: {rutaSteam}. Limpiando registro de ventana...");
                _steamService.LimpiarPosicionVentanaSteam();

                Logger.Log("[EjecutarModoConsolaAsync] Iniciando proceso de Steam con '-gamepadui'...");
                using (Process? steam = Process.Start(new ProcessStartInfo { FileName = rutaSteam, Arguments = "-gamepadui", UseShellExecute = true }))
                {
                    if (steam != null) 
                    {
                        Logger.Log($"[EjecutarModoConsolaAsync] Proceso de Steam iniciado (PID={steam.Id}).");
                        _steamService.MoverVentanaSteamAlMonitorPrincipal(steam.Id, 25);
                    }
                    else
                    {
                        Logger.Log("[EjecutarModoConsolaAsync] ADVERTENCIA: Process.Start de Steam retornó nulo.");
                    }
                }
 
                Logger.Log("[EjecutarModoConsolaAsync] Esperando a que Steam esté listo...");
                bool steamListo = await _steamService.EsperarSteamListoAsync(() => _modoEscritorio);
                
                if (!steamListo && !_modoEscritorio)
                {
                    Logger.Log("[EjecutarModoConsolaAsync] Steam no se inició correctamente (Timeout).");
                }
                
                Logger.Log("[EjecutarModoConsolaAsync] Steam reportado como Listo. Iniciando bucle principal.");

                if (!_modoEscritorio) 
                { 
                    System.Windows.Application.Current.Dispatcher.Invoke(() => 
                    { 
                        _winEventDelegate = new WinEventDelegate(WinEventCallback); 
                        _hWinEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT); 
                    }); 
                    _ = Task.Run(() => _steamService.MonitorDeJuegosAsync(() => _modoEscritorio, _keyboardHookService)); 
                }
                
                while (!_modoEscritorio) 
                { 
                    var procesosSteam = Process.GetProcessesByName("steam"); 
                    if (procesosSteam.Length == 0) 
                    { 
                        if (ReinstalandoOReinicioSteam)
                        {
                            Logger.Log("[EjecutarModoConsolaAsync] Steam está en proceso de reinstalación/reinicio. Esperando...");
                            await Task.Delay(3000);
                            continue;
                        }

                        Logger.Log("[EjecutarModoConsolaAsync] Steam cerrado. Verificando si es reinicio...");
                        bool seReinicio = await EsperarPosibleReinicio(4000); // 4 segundos de gracia
                        
                        if (seReinicio || ReinstalandoOReinicioSteam)
                        {
                            Logger.Log("[EjecutarModoConsolaAsync] Steam se reinició (actualización/cambio/reinstalación). Reconectando...");
                            await _steamService.EsperarSteamListoAsync(() => _modoEscritorio);
                            ReinstalandoOReinicioSteam = false;
                            continue;
                        }
                        else
                        {
                            Logger.Log("[EjecutarModoConsolaAsync] Steam cerrado definitivamente. Cerrando sesión.");
                            break;
                        }
                    } 
                    
                    Process steamPrincipal = procesosSteam[0]; 
                    steamPrincipal.EnableRaisingEvents = true; 
                    for (int j = 1; j < procesosSteam.Length; j++) procesosSteam[j].Dispose();
                    try { await steamPrincipal.WaitForExitAsync(); } catch { } 
                    steamPrincipal.Dispose(); 
                }
            } 
            catch (Exception ex) { Logger.Log($"Error en EjecutarModoConsolaAsync: {ex.Message}"); }
            finally 
            { 
                Logger.Log("[EjecutarModoConsolaAsync] Iniciando restauración y limpieza final...");
                if (!_modoEscritorio && !_cerrandoSesion) 
                { 
                    try { _displayService.RestaurarEntornoOriginal(_gpuScalingService); } catch (Exception ex) { Logger.Log($"[Finally] Error restaurando entorno: {ex.Message}"); }
                    _cerrandoSesion = true;
                    CerrarSesionRapido(); 
                } 
                
                try { _powerService.RestaurarPlanEnergia(); } catch { }
                try { _powerService.PermitirSuspension(); } catch { }
                try { RivaTunerCore.ApagarFantasma(); } catch { }
                try { NvidiaFastSync.Restaurar(); } catch { }
                
                try { if (_hWinEventHook != IntPtr.Zero) { UnhookWinEvent(_hWinEventHook); _hWinEventHook = IntPtr.Zero; } } catch { }
                try { _keyboardHookService.DetenerHook(); } catch { }
                try { LimpiarVentanaOculta(); } catch { }
                try { SystemParametersInfoTimeout(SPI_SETFOREGROUNDLOCKTIMEOUT, 200000, IntPtr.Zero, SPIF_SENDCHANGE | SPIF_UPDATEINIFILE); } catch { }
                Logger.Log("[EjecutarModoConsolaAsync] Entorno restaurado.");
            }

            // Si salimos al escritorio, cerrar la app WPF limpiamente
            if (_modoEscritorio)
            {
                Logger.Log("[EjecutarModoConsolaAsync] Cerrando aplicación WPF limpiamente tras salir al escritorio...");
                try { Dispatcher.Invoke(() => Shutdown()); } catch { }
            }
            // Si cerramos sesión, no hacemos nada más. Dejamos que Windows mate el proceso como parte del logoff.
            else if (_cerrandoSesion)
            {
                Logger.Log("[EjecutarModoConsolaAsync] Modo cierre de sesión. Dejando que Windows termine el proceso de forma natural.");
            }
        }

        private async Task<bool> EsperarPosibleReinicio(int timeoutMs)
        {
            int transcurrido = 0;
            while (transcurrido < timeoutMs && !_modoEscritorio && !_cerrandoSesion)
            {
                var procesos = Process.GetProcessesByName("steam");
                bool tieneProcesos = procesos.Length > 0;
                foreach (var p in procesos) p.Dispose();
                if (tieneProcesos)
                {
                    // Encontró algún proceso "steam". 
                    // Esperamos para ver si es transitorio (ej. sincronización de nube al cerrar) o permanente.
                    Logger.Log("[EsperarPosibleReinicio] Detectado proceso 'steam'. Esperando para confirmar si es persistente...");
                    await Task.Delay(1500);
                    transcurrido += 1500;

                    var procesosNuevos = Process.GetProcessesByName("steam");
                    bool tieneProcesosNuevos = procesosNuevos.Length > 0;
                    foreach (var p in procesosNuevos) p.Dispose();
                    if (tieneProcesosNuevos)
                    {
                        // Esperamos otro momento para estar 100% seguros
                        await Task.Delay(1500);
                        transcurrido += 1500;

                        var procesosConfirmacion = Process.GetProcessesByName("steam");
                        bool tieneConfirmacion = procesosConfirmacion.Length > 0;
                        foreach (var p in procesosConfirmacion) p.Dispose();
                        if (tieneConfirmacion)
                        {
                            Logger.Log("[EsperarPosibleReinicio] Proceso 'steam' confirmado como persistente (Reinicio detectado).");
                            return true;
                        }
                    }
                    Logger.Log("[EsperarPosibleReinicio] El proceso 'steam' detectado era transitorio. Continuando verificación...");
                }
                await Task.Delay(500);
                transcurrido += 500;
            }
            return false;
        }

        // ── FUNCIONES DE LIMPIEZA Y RESTAURACIÓN ──
        private void LimpiarVentanaOculta()
        {
            if (_hwndShell != IntPtr.Zero)
            {
                try { UnregisterHotKey(_hwndShell, 1); } catch { }
                try { UnregisterHotKey(_hwndShell, 2); } catch { }
                try 
                { 
                    if (_hwndSourceShell != null)
                    {
                        _hwndSourceShell.RemoveHook(ShellWindowHook);
                        _hwndSourceShell.Dispose();
                        _hwndSourceShell = null;
                    }
                } catch { }
                _hwndShell = IntPtr.Zero;
                Logger.Log("[LimpiarVentanaOculta] Ventana oculta y hotkeys eliminados.");
            }
        }

        private void CerrarSesionRapido() 
        { 
            Logger.Log("[CerrarSesionRapido] Iniciando cierre de sesión de Windows...");
            
            // Matar todo residuo de Steam
            foreach (var p in Process.GetProcessesByName("steam")) { try { p.Kill(); p.Dispose(); } catch { } }
            foreach (var p in Process.GetProcessesByName("steamwebhelper")) { try { p.Kill(); p.Dispose(); } catch { } }

            // Usar shutdown.exe /l para cerrar sesión
            try
            {
                Logger.Log("[CerrarSesionRapido] Ejecutando shutdown.exe /l...");
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/l",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[CerrarSesionRapido] Error con shutdown /l: {ex.Message}");
            }

            // IMPORTANTE: NO llamamos a Shutdown() ni Environment.Exit(). 
            // Como somos el shell, matarnos a nosotros mismos antes de que Windows termine 
            // de procesar el logoff causa un black screen inmediato y posibles crashes.
            // Simplemente retornamos y dejamos que el SO envíe WM_CLOSE/WM_ENDSESSION para cerrarnos limpiamente.
            Logger.Log("[CerrarSesionRapido] Solicitud de cierre enviada. Esperando a Windows...");
        }
        
        private void RegistrarAtajoTecladoSilencioso() 
        { 
            var parameters = new HwndSourceParameters("HiddenHotkeyWindow")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };
            _hwndSourceShell = new HwndSource(parameters);
            _hwndShell = _hwndSourceShell.Handle;
            _hwndSourceShell.AddHook(ShellWindowHook); 
            RegisterHotKey(_hwndShell, 1, 0x0007, 0x53); // Ctrl + Shift + Alt + S (Escritorio)
            RegisterHotKey(_hwndShell, 2, 0x0007, 0x52); // Ctrl + Shift + Alt + R (Modo Recuperación)
        }
        
        private IntPtr ShellWindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) 
        { 
            if (msg == 0x0312 && wParam.ToInt32() == 1) 
            { 
                Logger.Log("[ShellWindowHook] Atajo de escritorio presionado (Ctrl+Shift+Alt+S).");
                _modoEscritorio = true; 
                try { _displayService.RestaurarEntornoOriginal(_gpuScalingService); } catch { }
                foreach (var p in Process.GetProcessesByName("steam")) { try { p.Kill(); p.Dispose(); } catch { } } 
                try { using (Process.Start("explorer.exe")) {} } catch { }
                handled = true; 
            } 

            else if (msg == 0x007E && _displayService.AislamientoActivo) 
            { 
                if (Interlocked.CompareExchange(ref _suppressDisplayChange, 0, 0) == 0) 
                { 
                    lock (_timerLock) 
                    { 
                        _debounceTimer?.Dispose(); 
                        _debounceTimer = new System.Threading.Timer(ReaplicarEscaladoCallback, null, 120, Timeout.Infinite); 
                    } 
                } 
            } 
            else if (msg == WM_POWERBROADCAST)
            {
                int wp = wParam.ToInt32();
                if (wp == PBT_APMSUSPEND)
                {
                    Logger.Log("Sistema suspendiendo...");
                    TraductorMando.Detener();
                    _keyboardHookService.DetenerHook();
                }
                else if (wp == PBT_APMRESUMESUSPEND)
                {
                    Logger.Log("Sistema reanudado. Restaurando entorno...");
                    _ = Task.Run(async () => 
                    {
                        await Task.Delay(500);
                        var config = CargarConfig();
                        if (config != null)
                        {
                            _displayService.AislarPantalla(config, _gpuScalingService);
                            if (config.AudioDispositivo != null)
                                _audioService.EstablecerDispositivoPorDefecto(config.AudioDispositivo);
                            if (config.EmuladorActivado) _ = TraductorMando.IniciarAsync();
                        }
                        _keyboardHookService.IniciarHook(() => !_modoEscritorio);

                        try
                        {
                            var procs = Process.GetProcessesByName("steam");
                            if (procs.Length > 0)
                            {
                                _steamService.MoverVentanaSteamAlMonitorPrincipal(procs[0].Id, 1);
                            }
                        }
                        catch { }
                    });
                }
            }
            return IntPtr.Zero; 
        }


        
        private void ReaplicarEscaladoCallback(object? state) 
        { 
            Interlocked.Increment(ref _suppressDisplayChange); 
            try { _gpuScalingService.ForzarEscaladoCompleto(); } 
            finally 
            { 
                Thread.Sleep(60); 
                Interlocked.Decrement(ref _suppressDisplayChange); 
            } 
        }

        private ConfiguracionSteamOS? CargarConfig() 
        { 
            string ruta = AppPaths.Config; 
            if (!File.Exists(ruta)) return null; 
            return JsonSerializer.Deserialize<ConfiguracionSteamOS>(File.ReadAllText(ruta)); 
        }
    }
}