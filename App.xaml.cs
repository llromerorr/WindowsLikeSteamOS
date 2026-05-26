using System;
using System.Windows;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using AudioSwitcher.AudioApi.CoreAudio;

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
        // ── P/Invoke: Topología de vídeo ─────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]  public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra;
            public int dmFields; public int dmPositionX; public int dmPositionY; public int dmDisplayOrientation;
            public int dmDisplayFixedOutput; public short dmColor; public short dmDuplex; public short dmYResolution;
            public short dmTTOption; public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight;
            public int dmDisplayFlags; public int dmDisplayFrequency; public int dmICMMethod; public int dmICMIntent;
            public int dmMediaType; public int dmDitherType; public int dmReserved1; public int dmReserved2;
            public int dmPanningWidth; public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern bool EnumDisplayDevices(string? lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);
        [DllImport("user32.dll")]
        static extern int EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);
        [DllImport("user32.dll")]
        static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

        // ── P/Invoke: Manipulación de ventanas ───────────────────────────────────
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const uint SWP_NOSIZE   = 0x0001;
        const uint SWP_NOZORDER = 0x0004;
        const int  SW_RESTORE   = 9;

        // ── Flags de DEVMODE ──────────────────────────────────────────────────────
        const int DM_POSITION         = 0x00000020;
        const int DM_BITSPERPEL       = 0x00040000;
        const int DM_PELSWIDTH        = 0x00080000;
        const int DM_PELSHEIGHT       = 0x00100000;
        const int DM_DISPLAYFREQUENCY = 0x00400000;

        // ── Flags de ChangeDisplaySettingsEx ─────────────────────────────────────
        const int CDS_UPDATEREGISTRY = 0x00000001;
        const int CDS_SET_PRIMARY    = 0x00000010;
        const int CDS_NORESET        = 0x10000000;

        const int ENUM_CURRENT_SETTINGS = -1;

        // Estado original de cada monitor (para restaurar al salir)
        private Dictionary<string, DEVMODE> _monitoresOriginales = new();

        // ─────────────────────────────────────────────────────────────────────────

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (e.Args.Length > 0 && e.Args[0] == "-shell") EjecutarModoConsola();
            else new MainWindow().Show();
        }

        private void EjecutarModoConsola()
        {
            try
            {
                AislarPantallaYAudio();

                string rutaSteam = ObtenerRutaSteam();
                if (string.IsNullOrEmpty(rutaSteam)) return;

                // Forzar posición de ventana Steam en el monitor correcto ANTES de lanzarlo
                LimpiarPosicionVentanaSteam();

                Process? steam = Process.Start(new ProcessStartInfo
                {
                    FileName       = rutaSteam,
                    Arguments      = "-gamepadui",
                    UseShellExecute = true
                });

                // Esperar a que Steam arranque y mover su ventana al monitor correcto por si acaso
                if (steam != null)
                    MoverVentanaSteamAlMonitorPrincipal(steam.Id, intentos: 20);

                while (true)
                {
                    if (Process.GetProcessesByName("steam").Length == 0) break;
                    Thread.Sleep(2000);
                }
            }
            catch { }
            finally
            {
                RestaurarEntornoOriginal();
                CerrarSesion();
            }
        }

        // ── AISLAMIENTO ───────────────────────────────────────────────────────────

        private void AislarPantallaYAudio()
        {
            try
            {
                var config = CargarConfig();
                if (config == null) return;

                AplicarAudio(config);
                EscanearYGuardarMonitores();

                bool encontrado = ConfigurarMonitorPrincipal(config);
                if (!encontrado) return;

                DesconectarMonitoresSecundarios(config);

                // Commit global: aplica todos los cambios encolados con CDS_NORESET a la GPU
                ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

                // Esperar a que los drivers de la GPU procesen el nuevo mapa de pantallas
                Thread.Sleep(2000);
            }
            catch { }
        }

        private bool ConfigurarMonitorPrincipal(ConfiguracionSteamOS config)
        {
            // Busca el monitor elegido, lo mueve a (0,0) y lo convierte en principal
            // Debe ocurrir ANTES de desconectar los demás para que Windows nunca pierda la coordenada raíz
            foreach (string deviceName in _monitoresOriginales.Keys)
            {
                if (deviceName != config.MonitorDeviceName) continue;

                DEVMODE mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref mode);

                mode.dmPelsWidth        = config.ResolucionWidth;
                mode.dmPelsHeight       = config.ResolucionHeight;
                mode.dmDisplayFrequency = config.RefreshRate;
                mode.dmPositionX        = 0;
                mode.dmPositionY        = 0;
                mode.dmFields           = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_POSITION;

                ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero,
                    CDS_UPDATEREGISTRY | CDS_SET_PRIMARY | CDS_NORESET, IntPtr.Zero);

                return true;
            }
            return false;
        }

        private void DesconectarMonitoresSecundarios(ConfiguracionSteamOS config)
        {
            // ⚠️ CRÍTICO: para desconectar un monitor la API Win32 exige un DEVMODE con
            // dmPelsWidth = 0 y dmPelsHeight = 0. Pasar IntPtr.Zero (NULL) como DEVMODE
            // NO desconecta — le dice a Windows "usa el valor del registro", y el monitor
            // permanece activo mostrando pantalla negra en el escritorio extendido.
            foreach (string deviceName in _monitoresOriginales.Keys)
            {
                if (deviceName == config.MonitorDeviceName) continue;

                DEVMODE modeDetach = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                modeDetach.dmPelsWidth  = 0;
                modeDetach.dmPelsHeight = 0;
                modeDetach.dmPositionX  = 0;
                modeDetach.dmPositionY  = 0;
                // Solo estos tres flags: indican a Windows que la pantalla no ocupa espacio en el escritorio
                modeDetach.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;

                ChangeDisplaySettingsEx(deviceName, ref modeDetach, IntPtr.Zero,
                    CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            }
        }

        // ── POSICIÓN DE VENTANA DE STEAM ──────────────────────────────────────────

        private void LimpiarPosicionVentanaSteam()
        {
            // Steam guarda la posición de su última ventana en el registro.
            // Si esas coordenadas apuntan al monitor secundario (ahora desconectado),
            // Steam abrirá fuera del área visible. Forzamos (0,0) antes de lanzarlo.
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true);
                if (key == null) return;

                key.SetValue("SteamWindowX", 0, RegistryValueKind.DWord);
                key.SetValue("SteamWindowY", 0, RegistryValueKind.DWord);
                key.SetValue("SteamWindowW", 1280, RegistryValueKind.DWord);
                key.SetValue("SteamWindowH", 720,  RegistryValueKind.DWord);
            }
            catch { }
        }

        private void MoverVentanaSteamAlMonitorPrincipal(int steamPid, int intentos)
        {
            // Espera a que aparezca la ventana principal de Steam y la mueve a (0,0)
            // para garantizar que esté en el monitor gaming aunque Steam ignore el registro.
            for (int i = 0; i < intentos; i++)
            {
                Thread.Sleep(1500);

                List<IntPtr> ventanas = new();
                EnumWindows((hWnd, _) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == (uint)steamPid && IsWindowVisible(hWnd))
                        ventanas.Add(hWnd);
                    return true;
                }, IntPtr.Zero);

                if (ventanas.Count > 0)
                {
                    foreach (IntPtr hWnd in ventanas)
                    {
                        ShowWindow(hWnd, SW_RESTORE);
                        // Mueve la ventana a (0,0) sin cambiar su tamaño ni su Z-order
                        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
                    }
                    break; // Ventanas encontradas y movidas: salir del bucle
                }
            }
        }

        // ── RESTAURACIÓN ──────────────────────────────────────────────────────────

        private void RestaurarEntornoOriginal()
        {
            try
            {
                if (_monitoresOriginales.Count == 0) return;

                // Paso 1: Reconectar primero el que era principal original (posición 0,0)
                // para que Windows recupere la coordenada raíz antes de reubicar los demás
                foreach (var kvp in _monitoresOriginales)
                {
                    DEVMODE mode = kvp.Value;
                    if (mode.dmPositionX == 0 && mode.dmPositionY == 0)
                    {
                        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY | DM_POSITION;
                        ChangeDisplaySettingsEx(kvp.Key, ref mode, IntPtr.Zero,
                            CDS_UPDATEREGISTRY | CDS_SET_PRIMARY | CDS_NORESET, IntPtr.Zero);
                    }
                }

                // Paso 2: Reconectar todos los monitores secundarios en sus posiciones originales
                foreach (var kvp in _monitoresOriginales)
                {
                    DEVMODE mode = kvp.Value;
                    if (mode.dmPositionX == 0 && mode.dmPositionY == 0) continue;

                    mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY | DM_POSITION;
                    ChangeDisplaySettingsEx(kvp.Key, ref mode, IntPtr.Zero,
                        CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
                }

                // Commit final
                ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            }
            catch { }
        }

        // ── HELPERS ───────────────────────────────────────────────────────────────

        private void EscanearYGuardarMonitores()
        {
            int id = 0;
            DISPLAY_DEVICE dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };

            while (EnumDisplayDevices(null, id, ref dd, 0))
            {
                // DISPLAY_DEVICE_ACTIVE (flag 0x1): monitor activo y parte del escritorio
                if ((dd.StateFlags & 0x1) != 0)
                {
                    DEVMODE modeOrig = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                    if (EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref modeOrig) != 0)
                        _monitoresOriginales[dd.DeviceName] = modeOrig;
                }
                id++;
                dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() }; // reset obligatorio
            }
        }

        private void AplicarAudio(ConfiguracionSteamOS config)
        {
            if (string.IsNullOrEmpty(config.AudioDispositivo) ||
                config.AudioDispositivo == "Salida de audio por defecto") return;
            try
            {
                CoreAudioController ctrl = new CoreAudioController();
                foreach (var dev in ctrl.GetPlaybackDevices())
                    if (dev.FullName == config.AudioDispositivo) { dev.SetAsDefault(); break; }
            }
            catch { }
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
                if (key != null)
                    return Path.Combine(key.GetValue("InstallPath") as string ?? "", "steam.exe");
            }
            catch { }
            return @"C:\Program Files (x86)\Steam\steam.exe";
        }

        private void CerrarSesion()
        {
            Process.Start("shutdown", "/l /f");
            Shutdown();
        }
    }
}