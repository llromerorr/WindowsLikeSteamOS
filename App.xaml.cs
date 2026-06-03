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
using AudioSwitcher.AudioApi.CoreAudio;
using System.Windows.Interop;
using System.Security.Principal;
using System.Text;

// Librerías de NVIDIA
using NvAPIWrapper;
using NvAPIWrapper.DRS;

namespace SteamOSConfigurator
{
    // ── MODELO DE DATOS ACTUALIZADO ──
    public class ConfiguracionSteamOS
    {
        public string? MonitorDeviceName { get; set; }
        public string? MonitorDeviceId { get; set; } 
        public int ResolucionWidth { get; set; }
        public int ResolucionHeight { get; set; }
        public int RefreshRate { get; set; }
        public string? AudioDispositivo { get; set; }
        public bool EmuladorActivado { get; set; } = true; 
        public int LimiteFPS { get; set; } = 30;
        public bool ForzarFastSync { get; set; } = true;
        public int DelayBotonHome { get; set; } = 65;
    }

    // ── TELEMETRÍA (LOGGER) ──
    public static class Logger
    {
        public static bool HabilitarDebug = true; 
        private static readonly string RutaLog = @"C:\ProgramData\SteamOS\debug_log.txt";

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
                    
                    try { profile = dynSession.BaseProfile; } catch { }
                    if (profile == null) { try { profile = dynSession.GlobalProfile; } catch { } }
                    
                    if (profile != null) 
                    {
                        try { profile.SetSetting(0x00A879CEu, 4u); } 
                        catch { try { profile.SetSetting(0x00A879CEu, 4u, 0); } catch { } }
                        
                        dynSession.Save(); 
                        Logger.Log("Fast Sync inyectado al instante.");
                    }
                }
            } 
            catch { }
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
                    
                    try { profile = dynSession.BaseProfile; } catch { }
                    if (profile == null) { try { profile = dynSession.GlobalProfile; } catch { } }
                    
                    if (profile != null) 
                    {
                        try { profile.SetSetting(0x00A879CEu, 0u); } 
                        catch { try { profile.SetSetting(0x00A879CEu, 0u, 0); } catch { } }
                        
                        dynSession.Save();
                    }
                }
            } 
            catch { }
        }
    }

    // ── MÓDULO 2: INYECTOR DE FRAME PACING (RIVA TUNER) ──
    public static class RivaTunerCore
    {
        private static readonly string RutaInstalador = @"C:\ProgramData\SteamOS\Dependencias\RTSSSetup.exe";
        private static readonly string RutaExe = @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe";
        private static readonly string RutaPerfilGlobal = @"C:\Program Files (x86)\RivaTuner Statistics Server\Profiles\Global";

        public static void AsegurarInstalacionSilenciosa() 
        { 
            if (!File.Exists(RutaExe) && File.Exists(RutaInstalador)) 
            { 
                Process.Start(new ProcessStartInfo 
                { 
                    FileName = RutaInstalador, 
                    Arguments = "/S", 
                    WindowStyle = ProcessWindowStyle.Hidden, 
                    CreateNoWindow = true 
                })?.WaitForExit(); 
            } 
        }

        public static void ForzarModoConsola(int limiteFPS) 
        { 
            try 
            { 
                string dirPerfiles = Path.GetDirectoryName(RutaPerfilGlobal)!; 
                if (!Directory.Exists(dirPerfiles)) Directory.CreateDirectory(dirPerfiles); 
                
                string configuracion = $"[Framerate]\nLimit={limiteFPS}\nLimitDenominator=1\n"; 
                File.WriteAllText(RutaPerfilGlobal, configuracion); 
            } 
            catch { } 
        }

        public static void DespertarFantasma() 
        { 
            if (File.Exists(RutaExe)) 
            { 
                var procesos = Process.GetProcessesByName("RTSS"); 
                if (procesos.Length == 0) 
                {
                    Process.Start(new ProcessStartInfo 
                    { 
                        FileName = RutaExe, 
                        UseShellExecute = false, 
                        CreateNoWindow = true 
                    }); 
                }
            } 
        }

        public static void ApagarFantasma() 
        { 
            foreach (var proc in Process.GetProcessesByName("RTSS")) { try { proc.Kill(); } catch { } } 
            foreach (var proc in Process.GetProcessesByName("rtss")) { try { proc.Kill(); } catch { } } 
        }
    }

    // ── EL ORQUESTADOR PRINCIPAL ──
    public partial class App : System.Windows.Application
    {
        // ── P/INVOKES Y CONSTANTES DE WINDOWS API ──
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag); 
        static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow(); 
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] static extern bool SystemParametersInfoTimeout(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount); 
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hWnd);
        
        [StructLayout(LayoutKind.Sequential)] 
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        
        const uint SPI_SETWORKAREA = 0x002F; 
        const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001; 
        const uint SPIF_SENDCHANGE = 0x0002; 
        const uint SPIF_UPDATEINIFILE = 0x0001;
        
        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        
        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")] static extern short GetKeyState(int nVirtKey);
        
        const int WH_KEYBOARD_LL = 13; 
        const uint EVENT_SYSTEM_FOREGROUND = 3; 
        const uint WINEVENT_OUTOFCONTEXT = 0;
        
        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Ansi)] 
        public struct DEVMODE_ANSI 
        { 
            [FieldOffset(0)] [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName; 
            [FieldOffset(36)] public short dmSize; 
            [FieldOffset(40)] public int dmFields; 
            [FieldOffset(44)] public int dmPositionX; 
            [FieldOffset(48)] public int dmPositionY; 
            [FieldOffset(52)] public uint dmDisplayOrientation; 
            [FieldOffset(56)] public uint dmDisplayFixedOutput; 
            [FieldOffset(104)] public uint dmBitsPerPel; 
            [FieldOffset(108)] public uint dmPelsWidth; 
            [FieldOffset(112)] public uint dmPelsHeight; 
            [FieldOffset(116)] public uint dmDisplayFlags; 
            [FieldOffset(120)] public uint dmDisplayFrequency; 
        }
        
        [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern int ChangeDisplaySettingsExA(string? lpszDeviceName, ref DEVMODE_ANSI lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExA", CharSet = CharSet.Ansi)] static extern int ChangeDisplaySettingsExReset(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
        
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
        
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool EnumDisplayDevices(string? lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern int EnumDisplaySettingsA(string? deviceName, int modeNum, ref DEVMODE_ANSI devMode);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam); 
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd); 
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); 
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] static extern bool ExitWindowsEx(uint uFlags, uint dwReason);
        
        const uint SWP_NOSIZE = 0x0001; 
        const uint SWP_NOZORDER = 0x0004; 
        const int SW_HIDE = 0; 
        const int SW_SHOW = 5; 
        const int SW_RESTORE = 9;
        const int DM_POSITION = 0x00000020; 
        const int DM_BITSPERPEL = 0x00040000; 
        const int DM_PELSWIDTH = 0x00080000; 
        const int DM_PELSHEIGHT = 0x00100000; 
        const int DM_DISPLAYFREQUENCY = 0x00400000; 
        const int DM_DISPLAYFIXEDOUTPUT = 0x20000000; 
        const uint DMDFO_DEFAULT = 0; 
        const uint CDS_UPDATEREGISTRY = 0x00000001; 
        const uint CDS_SET_PRIMARY = 0x00000010; 
        const uint CDS_NORESET = 0x10000000; 
        const uint CDS_GLOBAL = 0x00000008; 
        const int ENUM_CURRENT_SETTINGS = -1;

        // ── VARIABLES DE CONTROL DE ESTADO ──
        private Dictionary<string, DEVMODE_ANSI> _monitoresOriginales = new();
        private bool _modoEscritorio = false; 
        private bool _aislamientoActivo = false; 
        private IntPtr _hwndShell = IntPtr.Zero;
        private System.Threading.Timer? _debounceTimer; 
        private int _suppressDisplayChange = 0; 
        private readonly object _timerLock = new object();
        private HashSet<IntPtr> _ventanasSteamOcultas = new HashSet<IntPtr>(); 
        private readonly object _lockVentanas = new object();
        private IntPtr _juegoActivoHwnd = IntPtr.Zero; 
        private WinEventDelegate? _winEventDelegate; 
        private IntPtr _hWinEventHook = IntPtr.Zero;
        private LowLevelKeyboardProc? _keyboardDelegate; 
        private IntPtr _keyboardHook = IntPtr.Zero;

        // ── ARRANQUE Y PRIVILEGIOS ──
        protected override void OnStartup(StartupEventArgs e)
        {
            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
            base.OnStartup(e);
            
            if (!EsAdministrador()) 
            { 
                try 
                { 
                    Process.Start(new ProcessStartInfo 
                    { 
                        UseShellExecute = true, 
                        WorkingDirectory = Environment.CurrentDirectory, 
                        FileName = Environment.ProcessPath, 
                        Arguments = e.Args.Length > 0 ? string.Join(" ", e.Args) : "", 
                        Verb = "runas" 
                    }); 
                } 
                catch { } 
                Environment.Exit(0); 
                return; 
            }
            
            SystemParametersInfoTimeout(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE | SPIF_UPDATEINIFILE);
            
            if (e.Args.Length > 0 && e.Args[0] == "-shell") 
            { 
                _ = EjecutarModoConsolaAsync(); 
            } 
            else 
            { 
                new MainWindow().Show(); 
            }
        }

        private bool EsAdministrador() 
        { 
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) 
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); 
        }
        
        private string ObtenerDeviceIdFisico(string deviceName) 
        { 
            DISPLAY_DEVICE dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() }; 
            if (EnumDisplayDevices(deviceName, 0, ref dd, 0)) return dd.DeviceID; 
            return ""; 
        }

        private void ForzarRegistroEscaladoNVIDIA() 
        { 
            try 
            { 
                using (var config = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration", true)) 
                { 
                    if (config == null) return; 
                    foreach (string sub in config.GetSubKeyNames()) 
                    { 
                        using (var key0 = config.OpenSubKey(sub + @"\00\00", true)) 
                        { 
                            if (key0 == null) continue; 
                            try { key0.SetValue("Scaling", 3, RegistryValueKind.DWord); key0.SetValue("ScalingMode", 3, RegistryValueKind.DWord); } 
                            catch { } 
                        } 
                    } 
                } 
            } 
            catch { } 
        }

        // ── HOOKS DE TECLADO Y VENTANAS ──
        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam) 
        {
            if (nCode >= 0 && _aislamientoActivo) 
            {
                int vkCode = Marshal.ReadInt32(lParam); 
                bool altPressed = (GetKeyState(0x12) & 0x8000) != 0; 
                bool ctrlPressed = (GetKeyState(0x11) & 0x8000) != 0; 
                
                if ((vkCode == 0x09 && altPressed) || (vkCode == 0x1B && altPressed) || (vkCode == 0x1B && ctrlPressed) || vkCode == 0x5B || vkCode == 0x5C) 
                    return new IntPtr(1); 
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime) 
        {
            if (hwnd == IntPtr.Zero || _modoEscritorio || _juegoActivoHwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out uint pid); 
            if (pid == 0) return;
            
            try 
            { 
                var proc = Process.GetProcessById((int)pid); 
                string pName = proc.ProcessName.ToLower(); 
                if (pName == "steam" || pName == "steamwebhelper") 
                { 
                    ShowWindow(hwnd, SW_HIDE); 
                    lock (_lockVentanas) { _ventanasSteamOcultas.Add(hwnd); } 
                    SetForegroundWindow(_juegoActivoHwnd); 
                } 
            } 
            catch { }
        }

        private async Task EsperarBigPictureAsync() 
        {
            bool detectado = false;
            while (!_modoEscritorio && !detectado) 
            {
                EnumWindows((hWnd, _) => 
                { 
                    if (!IsWindowVisible(hWnd)) return true; 
                    int length = GetWindowTextLength(hWnd); 
                    if (length > 0) 
                    { 
                        StringBuilder sb = new StringBuilder(length + 1); 
                        GetWindowText(hWnd, sb, sb.Capacity); 
                        string titulo = sb.ToString().ToLower(); 
                        if (titulo.Contains("big picture")) 
                        { 
                            GetWindowThreadProcessId(hWnd, out uint pid); 
                            try 
                            { 
                                var proc = Process.GetProcessById((int)pid); 
                                string pName = proc.ProcessName.ToLower(); 
                                if (pName == "steam" || pName == "steamwebhelper") 
                                { 
                                    detectado = true; 
                                    return false; 
                                } 
                            } 
                            catch { } 
                        } 
                    } 
                    return true; 
                }, IntPtr.Zero);
                
                if (!detectado) await Task.Delay(1000);
            }
        }

        private async Task MonitorDeJuegosAsync() 
        {
            Process? juegoActivo = null;
            while (!_modoEscritorio) 
            {
                await Task.Delay(1000); 
                if (juegoActivo != null) 
                { 
                    try 
                    { 
                        if (juegoActivo.HasExited) 
                        { 
                            _juegoActivoHwnd = IntPtr.Zero; 
                            CambiarVisibilidadSteam(false); 
                            juegoActivo = null; 
                        } 
                    } 
                    catch { juegoActivo = null; _juegoActivoHwnd = IntPtr.Zero; CambiarVisibilidadSteam(false); } 
                }
                else 
                { 
                    IntPtr fgHwnd = GetForegroundWindow(); 
                    if (fgHwnd != IntPtr.Zero) 
                    { 
                        GetWindowThreadProcessId(fgHwnd, out uint pid); 
                        try 
                        { 
                            var proc = Process.GetProcessById((int)pid); 
                            string pName = proc.ProcessName.ToLower(); 
                            if (pName != "steam" && pName != "steamwebhelper" && pName != "gameoverlayui" && pName != "windowslikesteamos" && pName != "explorer") 
                            { 
                                juegoActivo = proc; 
                                _juegoActivoHwnd = fgHwnd; 
                                CambiarVisibilidadSteam(true); 
                            } 
                        } 
                        catch { } 
                    } 
                }
            }
        }

        private void CambiarVisibilidadSteam(bool ocultar) 
        {
            if (ocultar) 
            { 
                lock (_lockVentanas) { _ventanasSteamOcultas.Clear(); } 
                EnumWindows((hWnd, lParam) => 
                { 
                    GetWindowThreadProcessId(hWnd, out uint pid); 
                    try 
                    { 
                        var proc = Process.GetProcessById((int)pid); 
                        string pName = proc.ProcessName.ToLower(); 
                        if (pName == "steam" || pName == "steamwebhelper") 
                        { 
                            if (IsWindowVisible(hWnd)) 
                            { 
                                lock (_lockVentanas) { _ventanasSteamOcultas.Add(hWnd); } 
                                ShowWindow(hWnd, SW_HIDE); 
                            } 
                        } 
                    } 
                    catch { } 
                    return true; 
                }, IntPtr.Zero); 
            }
            else 
            { 
                lock (_lockVentanas) 
                { 
                    foreach (IntPtr hWnd in _ventanasSteamOcultas) 
                    { 
                        ShowWindow(hWnd, SW_SHOW); 
                        SetForegroundWindow(hWnd); 
                    } 
                    _ventanasSteamOcultas.Clear(); 
                } 
            }
        }

        // ── MOTOR PRINCIPAL DEL KIOSCO ──
        private async Task EjecutarModoConsolaAsync()
        {
            try 
            {
                RegistrarAtajoTecladoSilencioso(); 
                var config = CargarConfig();
                if (config == null) { CerrarSesionRapido(); return; }

                // Exorcismo previo
                foreach (var p in Process.GetProcessesByName("steam")) { try { p.Kill(); } catch { } }
                foreach (var p in Process.GetProcessesByName("steamwebhelper")) { try { p.Kill(); } catch { } }
                await Task.Delay(1000); 

                var workArea = new RECT { Left = 0, Top = 0, Right = config.ResolucionWidth, Bottom = config.ResolucionHeight };
                SystemParametersInfo(SPI_SETWORKAREA, 0, ref workArea, SPIF_SENDCHANGE);
                
                // ── CONFIGURACIÓN DINÁMICA DE RENDIMIENTO ──
                if (config.ForzarFastSync) NvidiaFastSync.Activar();
                else NvidiaFastSync.Restaurar();
                
                RivaTunerCore.AsegurarInstalacionSilenciosa(); 
                RivaTunerCore.ForzarModoConsola(config.LimiteFPS); 
                RivaTunerCore.DespertarFantasma(); 
                // ───────────────────────────────────────────

                ForzarRegistroEscaladoNVIDIA();
                AislarPantallaYAudio();

                if (config.EmuladorActivado) _ = TraductorMando.IniciarAsync();
                
                await Task.Delay(4000); 

                _keyboardDelegate = KeyboardHookCallback;
                using (Process curProcess = Process.GetCurrentProcess()) 
                using (ProcessModule curModule = curProcess.MainModule!) 
                { 
                    _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardDelegate, GetModuleHandle(curModule.ModuleName), 0); 
                }

                string rutaSteam = ObtenerRutaSteam(); 
                if (string.IsNullOrEmpty(rutaSteam)) { CerrarSesionRapido(); return; }
                
                LimpiarPosicionVentanaSteam();
                Process? steam = Process.Start(new ProcessStartInfo { FileName = rutaSteam, Arguments = "-gamepadui", UseShellExecute = true });
                if (steam != null) MoverVentanaSteamAlMonitorPrincipal(steam.Id, 25);

                await EsperarBigPictureAsync();

                if (!_modoEscritorio) 
                { 
                    System.Windows.Application.Current.Dispatcher.Invoke(() => 
                    { 
                        _winEventDelegate = new WinEventDelegate(WinEventCallback); 
                        _hWinEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT); 
                    }); 
                    _ = Task.Run(() => MonitorDeJuegosAsync()); 
                }
                
                while (!_modoEscritorio) 
                { 
                    var procesosSteam = Process.GetProcessesByName("steam"); 
                    if (procesosSteam.Length > 0) 
                    { 
                        Process steamPrincipal = procesosSteam[0]; 
                        steamPrincipal.EnableRaisingEvents = true; 
                        try { await steamPrincipal.WaitForExitAsync(); } catch { } 
                    } 
                    
                    if (_modoEscritorio) break; 
                    await Task.Delay(3000); 
                    if (Process.GetProcessesByName("steam").Length == 0) break; 
                }
            } 
            catch { }
            finally 
            { 
                if (!_modoEscritorio) 
                { 
                    RestaurarEntornoOriginal(); 
                    CerrarSesionRapido(); 
                } 
                
                RivaTunerCore.ApagarFantasma(); 
                NvidiaFastSync.Restaurar();
                
                if (_hWinEventHook != IntPtr.Zero) { UnhookWinEvent(_hWinEventHook); _hWinEventHook = IntPtr.Zero; }
                if (_keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
                SystemParametersInfoTimeout(SPI_SETFOREGROUNDLOCKTIMEOUT, 200000, IntPtr.Zero, SPIF_SENDCHANGE | SPIF_UPDATEINIFILE);
            }
        }

        // ── FUNCIONES DE LIMPIEZA Y RESTAURACIÓN ──
        private void CerrarSesionRapido() 
        { 
            TraductorMando.Detener(); 
            ExitWindowsEx(4, 0); 
            Environment.Exit(0); 
        }
        
        private void RegistrarAtajoTecladoSilencioso() 
        { 
            Window ventanaOculta = new Window { Width = 0, Height = 0, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, ShowInTaskbar = false, Visibility = Visibility.Hidden }; 
            ventanaOculta.Show(); 
            _hwndShell = new WindowInteropHelper(ventanaOculta).Handle; 
            HwndSource.FromHwnd(_hwndShell).AddHook(ShellWindowHook); 
            RegisterHotKey(_hwndShell, 1, 0x0007, 0x53); 
        }
        
        private IntPtr ShellWindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) 
        { 
            if (msg == 0x0312 && wParam.ToInt32() == 1) 
            { 
                _modoEscritorio = true; 
                RestaurarEntornoOriginal(); 
                foreach (var p in Process.GetProcessesByName("steam")) { try { p.Kill(); } catch { } } 
                Process.Start("explorer.exe"); 
                handled = true; 
            } 
            else if (msg == 0x007E && _aislamientoActivo) 
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
            return IntPtr.Zero; 
        }
        
        private void ReaplicarEscaladoCallback(object? state) 
        { 
            Interlocked.Increment(ref _suppressDisplayChange); 
            try { NvidiaScaler.ForzarEscaladoCompleto((NvidiaScaler.NvScaling)2); } 
            finally 
            { 
                Thread.Sleep(60); 
                Interlocked.Decrement(ref _suppressDisplayChange); 
            } 
        }

        private void AislarPantallaYAudio() 
        {
            try 
            {
                var config = CargarConfig(); if (config == null) return;
                
                if (!string.IsNullOrEmpty(config.AudioDispositivo) && config.AudioDispositivo != "Salida de audio por defecto") 
                { 
                    CoreAudioController ctrl = new CoreAudioController(); 
                    foreach (var dev in ctrl.GetPlaybackDevices()) 
                        if (dev.FullName == config.AudioDispositivo) { dev.SetAsDefault(); break; } 
                }
                
                int id = 0; 
                DISPLAY_DEVICE dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() }; 
                List<string> activos = new(); 
                string? monitorPrincipalSistema = null;
                
                while (EnumDisplayDevices(null, id, ref dd, 0)) 
                { 
                    if ((dd.StateFlags & 0x1) != 0) 
                    { 
                        activos.Add(dd.DeviceName); 
                        if ((dd.StateFlags & 0x4) != 0) monitorPrincipalSistema = dd.DeviceName; 
                        
                        if (!_monitoresOriginales.ContainsKey(dd.DeviceName)) 
                        { 
                            DEVMODE_ANSI modeOrig = new DEVMODE_ANSI { dmSize = (short)Marshal.SizeOf<DEVMODE_ANSI>() }; 
                            if (EnumDisplaySettingsA(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref modeOrig) != 0) 
                                _monitoresOriginales[dd.DeviceName] = modeOrig; 
                        } 
                    } 
                    id++; 
                    dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() }; 
                }
                
                if (activos.Count == 0) return;
                
                string monitorObjetivo = ""; 
                bool monitorEncontrado = false;
                
                foreach (string deviceName in activos) 
                { 
                    string idFisico = ObtenerDeviceIdFisico(deviceName); 
                    if (!string.IsNullOrEmpty(config.MonitorDeviceId) && idFisico == config.MonitorDeviceId) 
                    { 
                        monitorObjetivo = deviceName; 
                        monitorEncontrado = true; 
                        break; 
                    } 
                    else if (deviceName == config.MonitorDeviceName) 
                    { 
                        monitorObjetivo = deviceName; 
                        monitorEncontrado = true; 
                    } 
                }
                
                if (!monitorEncontrado) 
                { 
                    monitorObjetivo = monitorPrincipalSistema ?? activos[0]; 
                }
                
                foreach (string deviceName in activos) 
                { 
                    if (deviceName == monitorObjetivo) 
                    { 
                        DEVMODE_ANSI mode = new DEVMODE_ANSI { dmSize = (short)Marshal.SizeOf<DEVMODE_ANSI>() }; 
                        EnumDisplaySettingsA(deviceName, ENUM_CURRENT_SETTINGS, ref mode); 
                        
                        mode.dmPelsWidth = (uint)config.ResolucionWidth; 
                        mode.dmPelsHeight = (uint)config.ResolucionHeight; 
                        mode.dmBitsPerPel = 32; 
                        mode.dmDisplayFrequency = (uint)config.RefreshRate; 
                        mode.dmPositionX = 0; 
                        mode.dmPositionY = 0; 
                        mode.dmDisplayFixedOutput = DMDFO_DEFAULT; 
                        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY | DM_POSITION | DM_DISPLAYFIXEDOUTPUT; 
                        
                        ChangeDisplaySettingsExA(deviceName, ref mode, IntPtr.Zero, CDS_SET_PRIMARY | CDS_UPDATEREGISTRY | CDS_NORESET | CDS_GLOBAL, IntPtr.Zero); 
                    } 
                    else 
                    { 
                        DEVMODE_ANSI modeDetach = new DEVMODE_ANSI { dmSize = (short)Marshal.SizeOf<DEVMODE_ANSI>() }; 
                        modeDetach.dmPelsWidth = 0; 
                        modeDetach.dmPelsHeight = 0; 
                        modeDetach.dmPositionX = 0; 
                        modeDetach.dmPositionY = 0; 
                        modeDetach.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT; 
                        
                        ChangeDisplaySettingsExA(deviceName, ref modeDetach, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET | CDS_GLOBAL, IntPtr.Zero); 
                    } 
                }
                
                ChangeDisplaySettingsExReset(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); 
                _aislamientoActivo = true;
                
                Task.Run(() => { Thread.Sleep(300); NvidiaScaler.ForzarEscaladoCompleto((NvidiaScaler.NvScaling)2); });
            } 
            catch { }
        }

        private void RestaurarEntornoOriginal() 
        { 
            if (!_aislamientoActivo) return; 
            try 
            { 
                if (_monitoresOriginales.Count > 0) 
                { 
                    foreach (var kvp in _monitoresOriginales) 
                    { 
                        DEVMODE_ANSI mode = kvp.Value; 
                        if (mode.dmPositionX == 0 && mode.dmPositionY == 0) 
                        { 
                            mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY | DM_POSITION; 
                            ChangeDisplaySettingsExA(kvp.Key, ref mode, IntPtr.Zero, CDS_SET_PRIMARY | CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero); 
                        } 
                    } 
                    
                    foreach (var kvp in _monitoresOriginales) 
                    { 
                        DEVMODE_ANSI mode = kvp.Value; 
                        if (mode.dmPositionX == 0 && mode.dmPositionY == 0) continue; 
                        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY | DM_POSITION; 
                        ChangeDisplaySettingsExA(kvp.Key, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero); 
                    } 
                } 
                
                ChangeDisplaySettingsExReset(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); 
                _aislamientoActivo = false; 
                Task.Run(() => { NvidiaScaler.RestaurarEscaladoPorMonitor(); }); 
            } 
            catch { } 
        }

        private void LimpiarPosicionVentanaSteam() 
        { 
            try 
            { 
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true); 
                if (key == null) return; 
                key.SetValue("SteamWindowX", 0, RegistryValueKind.DWord); 
                key.SetValue("SteamWindowY", 0, RegistryValueKind.DWord); 
            } 
            catch { } 
        }

        private void MoverVentanaSteamAlMonitorPrincipal(int steamPid, int intentos) 
        { 
            Task.Run(async () => 
            { 
                for (int i = 0; i < intentos; i++) 
                { 
                    await Task.Delay(1000); 
                    List<IntPtr> ventanas = new(); 
                    
                    EnumWindows((hWnd, _) => 
                    { 
                        GetWindowThreadProcessId(hWnd, out uint pid); 
                        if (pid == (uint)steamPid && IsWindowVisible(hWnd)) ventanas.Add(hWnd); 
                        return true; 
                    }, IntPtr.Zero); 
                    
                    if (ventanas.Count > 0) 
                    { 
                        foreach (IntPtr hWnd in ventanas) 
                        { 
                            ShowWindow(hWnd, SW_RESTORE); 
                            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER); 
                        } 
                        break; 
                    } 
                } 
            }); 
        }

        private ConfiguracionSteamOS? CargarConfig() 
        { 
            string ruta = @"C:\ProgramData\SteamOS\config.json"; 
            if (!File.Exists(ruta)) return null; 
            return JsonSerializer.Deserialize<ConfiguracionSteamOS>(File.ReadAllText(ruta)); 
        }
        
        private string ObtenerRutaSteam() 
        { 
            try 
            { 
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"); 
                if (key != null) return Path.Combine(key.GetValue("InstallPath") as string ?? "", "steam.exe"); 
            } 
            catch { } 
            return @"C:\Program Files (x86)\Steam\steam.exe"; 
        }
    }
}