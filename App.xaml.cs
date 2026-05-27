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
using System.Security.Principal; // Necesario para la Auto-Elevación

namespace SteamOSConfigurator
{
    public class ConfiguracionSteamOS
    {
        public string? MonitorDeviceName { get; set; }
        public int ResolucionWidth { get; set; }
        public int ResolucionHeight { get; set; }
        public int RefreshRate { get; set; }
        public string? AudioDispositivo { get; set; }
    }

    public partial class App : System.Windows.Application
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct DISPLAY_DEVICE { public int cb; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString; public int StateFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey; }
        [StructLayout(LayoutKind.Sequential)]
        public struct DEVMODE { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName; public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra; public int dmFields; public int dmPositionX; public int dmPositionY; public int dmDisplayOrientation; public int dmDisplayFixedOutput; public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName; public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight; public int dmDisplayFlags; public int dmDisplayFrequency; public int dmICMMethod; public int dmICMIntent; public int dmMediaType; public int dmDitherType; public int dmReserved1; public int dmReserved2; public int dmPanningWidth; public int dmPanningHeight; }

        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool EnumDisplayDevices(string? lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);
        [DllImport("user32.dll")] static extern int EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);
        [DllImport("user32.dll")] static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsEx")] static extern int ChangeDisplaySettingsExReset(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam); delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll", SetLastError = true)] static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

        const uint SWP_NOSIZE = 0x0001; const uint SWP_NOZORDER = 0x0004; const int SW_RESTORE = 9;
        const int DM_POSITION = 0x00000020; const int DM_BITSPERPEL = 0x00040000; const int DM_PELSWIDTH = 0x00080000; const int DM_PELSHEIGHT = 0x00100000; const int DM_DISPLAYFREQUENCY = 0x00400000;
        const int CDS_UPDATEREGISTRY = 0x00000001; const int CDS_SET_PRIMARY = 0x00000010; const int CDS_NORESET = 0x10000000; const int ENUM_CURRENT_SETTINGS = -1;

        private Dictionary<string, DEVMODE> _monitoresOriginales = new();
        private bool _modoEscritorio = false;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            if (e.Args.Length > 0 && e.Args[0] == "-shell") 
            {
                // MODO CONSOLA: Se ejecuta como usuario normal en las sombras.
                _ = EjecutarModoConsolaAsync();
            }
            else 
            {
                // MODO INTERFAZ: Verificamos si es Administrador
                if (!EsAdministrador())
                {
                    // Si no lo es, relanzamos la app pidiendo el Escudo de Administrador
                    try
                    {
                        ProcessStartInfo proc = new ProcessStartInfo
                        {
                            UseShellExecute = true,
                            WorkingDirectory = Environment.CurrentDirectory,
                            FileName = Environment.ProcessPath,
                            Verb = "runas"
                        };
                        Process.Start(proc);
                    }
                    catch { /* El usuario le dio a "No" */ }
                    Environment.Exit(0);
                    return;
                }
                new MainWindow().Show();
            }
        }

        private bool EsAdministrador()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private async Task EjecutarModoConsolaAsync()
        {
            try
            {
                RegistrarAtajoTecladoSilencioso();
                AislarPantallaYAudio();

                string rutaSteam = ObtenerRutaSteam();
                if (string.IsNullOrEmpty(rutaSteam)) { CerrarSesionRapido(); return; }

                LimpiarPosicionVentanaSteam();
                Process? steam = Process.Start(new ProcessStartInfo { FileName = rutaSteam, Arguments = "-gamepadui", UseShellExecute = true });
                if (steam != null) MoverVentanaSteamAlMonitorPrincipal(steam.Id, 20);

                while (!_modoEscritorio)
                {
                    if (Process.GetProcessesByName("steam").Length == 0) break;
                    await Task.Delay(2000); 
                }
            }
            catch { }
            finally
            {
                if (!_modoEscritorio) { RestaurarEntornoOriginal(); CerrarSesionRapido(); }
            }
        }

        private void CerrarSesionRapido() { ExitWindowsEx(4, 0); Environment.Exit(0); }

        private void RegistrarAtajoTecladoSilencioso()
        {
            Window ventanaOculta = new Window { Width = 0, Height = 0, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, ShowInTaskbar = false, Visibility = Visibility.Hidden };
            ventanaOculta.Show();
            IntPtr hwnd = new WindowInteropHelper(ventanaOculta).Handle;
            HwndSource.FromHwnd(hwnd).AddHook(HotkeyHook);
            RegisterHotKey(hwnd, 1, 0x0007, 0x53); 
        }

        private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && wParam.ToInt32() == 1) 
            {
                _modoEscritorio = true; RestaurarEntornoOriginal();
                foreach (var p in Process.GetProcessesByName("steam")) { try { p.Kill(); } catch { } }
                Process.Start("explorer.exe"); handled = true;
            }
            return IntPtr.Zero;
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

                int id = 0; DISPLAY_DEVICE dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() }; List<string> activos = new();
                while (EnumDisplayDevices(null, id, ref dd, 0))
                {
                    if ((dd.StateFlags & 0x1) != 0) { activos.Add(dd.DeviceName); DEVMODE modeOrig = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() }; if (EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref modeOrig) != 0) _monitoresOriginales[dd.DeviceName] = modeOrig; }
                    id++; dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                }

                foreach (string deviceName in activos)
                {
                    if (deviceName == config.MonitorDeviceName) { DEVMODE mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() }; EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref mode); mode.dmPelsWidth = config.ResolucionWidth; mode.dmPelsHeight = config.ResolucionHeight; mode.dmDisplayFrequency = config.RefreshRate; mode.dmPositionX = 0; mode.dmPositionY = 0; mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_POSITION; ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_SET_PRIMARY | CDS_NORESET, IntPtr.Zero); }
                }
                foreach (string deviceName in activos)
                {
                    if (deviceName != config.MonitorDeviceName) { DEVMODE modeDetach = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() }; modeDetach.dmPelsWidth = 0; modeDetach.dmPelsHeight = 0; modeDetach.dmPositionX = 0; modeDetach.dmPositionY = 0; modeDetach.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT; ChangeDisplaySettingsEx(deviceName, ref modeDetach, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero); }
                }
                ChangeDisplaySettingsExReset(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            }
            catch { }
        }

        private void RestaurarEntornoOriginal()
        {
            try
            {
                if (_monitoresOriginales.Count == 0) return;
                foreach (var kvp in _monitoresOriginales) { DEVMODE mode = kvp.Value; if (mode.dmPositionX == 0 && mode.dmPositionY == 0) { mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY | DM_POSITION; ChangeDisplaySettingsEx(kvp.Key, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_SET_PRIMARY | CDS_NORESET, IntPtr.Zero); } }
                foreach (var kvp in _monitoresOriginales) { DEVMODE mode = kvp.Value; if (mode.dmPositionX == 0 && mode.dmPositionY == 0) continue; mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY | DM_POSITION; ChangeDisplaySettingsEx(kvp.Key, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero); }
                ChangeDisplaySettingsExReset(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            }
            catch { }
        }

        private void LimpiarPosicionVentanaSteam() { try { using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true); if (key == null) return; key.SetValue("SteamWindowX", 0, RegistryValueKind.DWord); key.SetValue("SteamWindowY", 0, RegistryValueKind.DWord); } catch { } }

        private void MoverVentanaSteamAlMonitorPrincipal(int steamPid, int intentos)
        {
            Task.Run(async () => { for (int i = 0; i < intentos; i++) { await Task.Delay(1500); List<IntPtr> ventanas = new(); EnumWindows((hWnd, _) => { GetWindowThreadProcessId(hWnd, out uint pid); if (pid == (uint)steamPid && IsWindowVisible(hWnd)) ventanas.Add(hWnd); return true; }, IntPtr.Zero); if (ventanas.Count > 0) { foreach (IntPtr hWnd in ventanas) { ShowWindow(hWnd, SW_RESTORE); SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER); } break; } } });
        }

        private ConfiguracionSteamOS? CargarConfig() { string ruta = @"C:\ProgramData\SteamOS\config.json"; if (!File.Exists(ruta)) return null; return JsonSerializer.Deserialize<ConfiguracionSteamOS>(File.ReadAllText(ruta)); }

        private string ObtenerRutaSteam() { try { using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"); if (key != null) return Path.Combine(key.GetValue("InstallPath") as string ?? "", "steam.exe"); } catch { } return @"C:\Program Files (x86)\Steam\steam.exe"; }
    }
}