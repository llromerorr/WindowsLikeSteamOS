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

namespace SteamOSConfigurator
{
    public class ConfiguracionSteamOS
    {
        public string? MonitorDeviceName { get; set; }
        public int ResolucionWidth { get; set; }
        public int ResolucionHeight { get; set; }
        public int RefreshRate { get; set; }
        public string? AudioDispositivo { get; set; }
        public bool EmuladorActivado { get; set; } = true; 
    }

    public partial class App : System.Windows.Application
    {
        // ── P/INVOKES CLÁSICOS ──────────────────────────────────────────
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);
        static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
        
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        const uint SPI_SETWORKAREA = 0x002F;
        const uint SPIF_SENDCHANGE = 0x0002;

        // ── ESTRUCTURA DEVMODE ANSI EXPLÍCITA (NVIDIA FIX) ──
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
        public struct DISPLAY_DEVICE { public int cb; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString; public int StateFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey; }
        
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool EnumDisplayDevices(string? lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern int EnumDisplaySettingsA(string? deviceName, int modeNum, ref DEVMODE_ANSI devMode);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam); delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

        const uint SWP_NOSIZE = 0x0001; const uint SWP_NOZORDER = 0x0004; 
        const int SW_HIDE = 0; const int SW_SHOW = 5; const int SW_RESTORE = 9;
        
        const int DM_POSITION = 0x00000020; const int DM_BITSPERPEL = 0x00040000; const int DM_PELSWIDTH = 0x00080000; const int DM_PELSHEIGHT = 0x00100000; const int DM_DISPLAYFREQUENCY = 0x00400000;
        const int DM_DISPLAYFIXEDOUTPUT = 0x20000000;
        
        const uint DMDFO_DEFAULT = 0; 
        const uint CDS_UPDATEREGISTRY = 0x00000001; 
        const uint CDS_SET_PRIMARY = 0x00000010; 
        const uint CDS_NORESET = 0x10000000; 
        const uint CDS_GLOBAL = 0x00000008; 
        const int ENUM_CURRENT_SETTINGS = -1;

        private Dictionary<string, DEVMODE_ANSI> _monitoresOriginales = new();
        private bool _modoEscritorio = false;
        private bool _aislamientoActivo = false;
        private IntPtr _hwndShell = IntPtr.Zero;

        // ── LISTA PARA RECORDAR QUÉ VENTANAS OCULTAMOS ──
        private List<IntPtr> _ventanasSteamOcultas = new List<IntPtr>();

        // ── VARIABLES DEL MONITOR DE JUEGOS ──
        private System.Threading.Timer? _debounceTimer;
        private int _suppressDisplayChange = 0;
        private readonly object _timerLock = new object();

        protected override void OnStartup(StartupEventArgs e)
        {
            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
            base.OnStartup(e);
            
            if (!EsAdministrador())
            {
                try { Process.Start(new ProcessStartInfo { UseShellExecute = true, WorkingDirectory = Environment.CurrentDirectory, FileName = Environment.ProcessPath, Arguments = e.Args.Length > 0 ? string.Join(" ", e.Args) : "", Verb = "runas" }); } catch { }
                Environment.Exit(0); return;
            }

            if (e.Args.Length > 0 && e.Args[0] == "-shell") { _ = EjecutarModoConsolaAsync(); } else { new MainWindow().Show(); }
        }

        private bool EsAdministrador()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
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
                            try { key0.SetValue("Scaling", 3, RegistryValueKind.DWord); key0.SetValue("ScalingMode", 3, RegistryValueKind.DWord); } catch { }
                        }
                    }
                }
            } catch { }
        }

        // ── LA IDEA MAESTRA DE LUIS: EL MONITOR DE VISIBILIDAD ──
        private async Task MonitorDeJuegosAsync()
        {
            Process? juegoActivo = null;

            while (!_modoEscritorio)
            {
                await Task.Delay(1000); // Revisamos silenciosamente cada segundo

                if (juegoActivo != null)
                {
                    try
                    {
                        // Si el juego se cerró o crasheó
                        if (juegoActivo.HasExited)
                        {
                            CambiarVisibilidadSteam(false); // Revivimos a Steam
                            juegoActivo = null;
                        }
                    }
                    catch { juegoActivo = null; CambiarVisibilidadSteam(false); }
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

                            // Si NO es Steam, ni nuestro Shell, ni el Overlay, entonces ES UN JUEGO
                            if (pName != "steam" && pName != "steamwebhelper" && pName != "gameoverlayui" && pName != "windowslikesteamos" && pName != "explorer")
                            {
                                juegoActivo = proc;
                                CambiarVisibilidadSteam(true); // Ocultamos a Steam de la existencia
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
                // Limpiamos la memoria antes de guardar
                _ventanasSteamOcultas.Clear();

                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    try
                    {
                        var proc = Process.GetProcessById((int)pid);
                        string pName = proc.ProcessName.ToLower();

                        if (pName == "steam" || pName == "steamwebhelper")
                        {
                            // 1. Tomamos una "foto" de las ventanas que ESTÁN VISIBLES ahora mismo
                            if (IsWindowVisible(hWnd))
                            {
                                // 2. Las guardamos en nuestra lista y las ocultamos
                                _ventanasSteamOcultas.Add(hWnd);
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
                // 3. Cuando el juego se cierra, restauramos ÚNICAMENTE las que nosotros escondimos
                foreach (IntPtr hWnd in _ventanasSteamOcultas)
                {
                    ShowWindow(hWnd, SW_SHOW);
                    SetForegroundWindow(hWnd);
                }
                
                // Vaciamos la memoria
                _ventanasSteamOcultas.Clear();
            }
        }

        private async Task EjecutarModoConsolaAsync()
        {
            try
            {
                RegistrarAtajoTecladoSilencioso(); 
                var config = CargarConfig();
                if (config == null) { CerrarSesionRapido(); return; }

                // Sincronizamos el WorkArea de Windows para que los menús de Mad Max se vean
                var workArea = new RECT { Left = 0, Top = 0, Right = config.ResolucionWidth, Bottom = config.ResolucionHeight };
                SystemParametersInfo(SPI_SETWORKAREA, 0, ref workArea, SPIF_SENDCHANGE);
                
                ForzarRegistroEscaladoNVIDIA();
                AislarPantallaYAudio();

                if (config.EmuladorActivado) _ = TraductorMando.IniciarAsync();

                // ── ARRANCAMOS EL MONITOR INVISIBLE DE JUEGOS ──
                _ = Task.Run(() => MonitorDeJuegosAsync());

                string rutaSteam = ObtenerRutaSteam();
                if (string.IsNullOrEmpty(rutaSteam)) { CerrarSesionRapido(); return; }

                LimpiarPosicionVentanaSteam();
                Process? steam = Process.Start(new ProcessStartInfo { FileName = rutaSteam, Arguments = "-gamepadui", UseShellExecute = true });
                if (steam != null) MoverVentanaSteamAlMonitorPrincipal(steam.Id, 25);

                while (!_modoEscritorio)
                {
                    if (Process.GetProcessesByName("steam").Length == 0) break;
                    await Task.Delay(2000); 
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
            }
        }

        private void CerrarSesionRapido() { TraductorMando.Detener(); ExitWindowsEx(4, 0); Environment.Exit(0); }

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
                _modoEscritorio = true; RestaurarEntornoOriginal();
                foreach (var p in Process.GetProcessesByName("steam")) { try { p.Kill(); } catch { } }
                Process.Start("explorer.exe"); handled = true;
            }
            else if (msg == 0x007E && _aislamientoActivo) // WM_DISPLAYCHANGE
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
            try
            {
                NvidiaScaler.ForzarEscaladoCompleto((NvidiaScaler.NvScaling)2); 
            }
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
                    foreach (var dev in ctrl.GetPlaybackDevices()) if (dev.FullName == config.AudioDispositivo) { dev.SetAsDefault(); break; }
                }

                int id = 0; DISPLAY_DEVICE dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() }; 
                List<string> activos = new();
                bool monitorDeseadoConectado = false;

                while (EnumDisplayDevices(null, id, ref dd, 0))
                {
                    if ((dd.StateFlags & 0x1) != 0) 
                    {
                        activos.Add(dd.DeviceName);
                        if (dd.DeviceName == config.MonitorDeviceName) monitorDeseadoConectado = true;
                        if (!_monitoresOriginales.ContainsKey(dd.DeviceName))
                        {
                            DEVMODE_ANSI modeOrig = new DEVMODE_ANSI { dmSize = (short)Marshal.SizeOf<DEVMODE_ANSI>() }; 
                            if (EnumDisplaySettingsA(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref modeOrig) != 0) _monitoresOriginales[dd.DeviceName] = modeOrig; 
                        }
                    }
                    id++; dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                }

                if (!monitorDeseadoConectado) return;

                foreach (string deviceName in activos)
                {
                    if (deviceName == config.MonitorDeviceName) { 
                        DEVMODE_ANSI mode = new DEVMODE_ANSI { dmSize = (short)Marshal.SizeOf<DEVMODE_ANSI>() }; 
                        EnumDisplaySettingsA(deviceName, ENUM_CURRENT_SETTINGS, ref mode); 
                        mode.dmPelsWidth = (uint)config.ResolucionWidth; mode.dmPelsHeight = (uint)config.ResolucionHeight; mode.dmBitsPerPel = 32; mode.dmDisplayFrequency = (uint)config.RefreshRate; mode.dmPositionX = 0; mode.dmPositionY = 0; 
                        
                        mode.dmDisplayFixedOutput = DMDFO_DEFAULT; 
                        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY | DM_POSITION | DM_DISPLAYFIXEDOUTPUT; 
                        
                        ChangeDisplaySettingsExA(deviceName, ref mode, IntPtr.Zero, CDS_SET_PRIMARY | CDS_UPDATEREGISTRY | CDS_NORESET | CDS_GLOBAL, IntPtr.Zero); 
                    }
                    else { 
                        DEVMODE_ANSI modeDetach = new DEVMODE_ANSI { dmSize = (short)Marshal.SizeOf<DEVMODE_ANSI>() }; 
                        modeDetach.dmPelsWidth = 0; modeDetach.dmPelsHeight = 0; modeDetach.dmPositionX = 0; modeDetach.dmPositionY = 0; modeDetach.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT; 
                        ChangeDisplaySettingsExA(deviceName, ref modeDetach, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET | CDS_GLOBAL, IntPtr.Zero); 
                    }
                }
                
                ChangeDisplaySettingsExReset(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                _aislamientoActivo = true;

                Task.Run(() => { 
                    Thread.Sleep(300); 
                    NvidiaScaler.ForzarEscaladoCompleto((NvidiaScaler.NvScaling)2); 
                });
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
                    foreach (var kvp in _monitoresOriginales) { DEVMODE_ANSI mode = kvp.Value; if (mode.dmPositionX == 0 && mode.dmPositionY == 0) { mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY | DM_POSITION; ChangeDisplaySettingsExA(kvp.Key, ref mode, IntPtr.Zero, CDS_SET_PRIMARY | CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero); } }
                    foreach (var kvp in _monitoresOriginales) { DEVMODE_ANSI mode = kvp.Value; if (mode.dmPositionX == 0 && mode.dmPositionY == 0) continue; mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY | DM_POSITION; ChangeDisplaySettingsExA(kvp.Key, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero); }
                }
                ChangeDisplaySettingsExReset(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                _aislamientoActivo = false;
                Task.Run(() => { NvidiaScaler.RestaurarEscaladoPorMonitor(); });
            }
            catch { }
        }

        private void LimpiarPosicionVentanaSteam() { try { using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true); if (key == null) return; key.SetValue("SteamWindowX", 0, RegistryValueKind.DWord); key.SetValue("SteamWindowY", 0, RegistryValueKind.DWord); } catch { } }

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
                    
                    if (ventanas.Count > 0) { foreach (IntPtr hWnd in ventanas) { ShowWindow(hWnd, SW_RESTORE); SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER); } break; } 
                } 
            });
        }

        private ConfiguracionSteamOS? CargarConfig() { string ruta = @"C:\ProgramData\SteamOS\config.json"; if (!File.Exists(ruta)) return null; return JsonSerializer.Deserialize<ConfiguracionSteamOS>(File.ReadAllText(ruta)); }
        private string ObtenerRutaSteam() { try { using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"); if (key != null) return Path.Combine(key.GetValue("InstallPath") as string ?? "", "steam.exe"); } catch { } return @"C:\Program Files (x86)\Steam\steam.exe"; }
    }
}