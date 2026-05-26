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
        // --- INICIO ESTRUCTURAS HARDWARE NATIVAS ---
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
        public static extern bool EnumDisplayDevices(string? lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);
        [DllImport("user32.dll")]
        public static extern int EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);
        [DllImport("user32.dll")]
        public static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

        const int ENUM_CURRENT_SETTINGS = -1;
        const int DM_PELSWIDTH = 0x00080000;
        const int DM_PELSHEIGHT = 0x00100000;
        const int DM_DISPLAYFREQUENCY = 0x00400000;
        const int DM_POSITION = 0x00000020;
        const int CDS_NORESET = 0x10000000; // Encola los cambios en memoria sin romper el registro de Windows
        // --- FIN ESTRUCTURAS HARDWARE ---

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (e.Args.Length > 0 && e.Args[0] == "-shell") EjecutarModoConsola();
            else { new MainWindow().Show(); }
        }

        private void EjecutarModoConsola()
        {
            try
            {
                // 1. Apagar pantallas secundarias en memoria RAM y configurar audio
                AplicarConfiguracionHardware();

                string rutaSteam = ObtenerRutaSteam();
                if (string.IsNullOrEmpty(rutaSteam)) return;

                ProcessStartInfo psi = new ProcessStartInfo { FileName = rutaSteam, Arguments = "-gamepadui", UseShellExecute = true };
                Process.Start(psi);
                Thread.Sleep(10000);

                // 2. Bucle de Monitorización
                while (true)
                {
                    if (Process.GetProcessesByName("steam").Length == 0) break;
                    Thread.Sleep(2000);
                }
            }
            catch { }
            finally
            {
                // 3. Forzar salida segura de la sesión
                CerrarSesion();
            }
        }

        private void AplicarConfiguracionHardware()
        {
            try
            {
                string rutaConfig = @"C:\ProgramData\SteamOS\config.json";
                if (!File.Exists(rutaConfig)) return;
                var config = JsonSerializer.Deserialize<ConfiguracionSteamOS>(File.ReadAllText(rutaConfig));
                if (config == null) return;

                // --- ENRUTAMIENTO DE AUDIO EXCLUSIVO ---
                if (!string.IsNullOrEmpty(config.AudioDispositivo) && config.AudioDispositivo != "Salida de audio por defecto")
                {
                    CoreAudioController audioController = new CoreAudioController();
                    foreach (var dev in audioController.GetPlaybackDevices())
                    {
                        if (dev.FullName == config.AudioDispositivo) { dev.SetAsDefault(); break; }
                    }
                }

                // --- ANTES DE ARRANCAR STEAM: AISLAMIENTO RIGIDO DE MONITORES ---
                int deviceId = 0;
                DISPLAY_DEVICE displayDevice = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                List<string> monitoresActivos = new List<string>();

                while (EnumDisplayDevices(null, deviceId, ref displayDevice, 0))
                {
                    if ((displayDevice.StateFlags & 1) != 0) 
                    {
                        monitoresActivos.Add(displayDevice.DeviceName);
                    }
                    deviceId++;
                }

                // Desactivar monitores no seleccionados temporalmente para esta sesión
                foreach (string deviceName in monitoresActivos)
                {
                    DEVMODE mode = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
                    EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref mode);

                    if (deviceName == config.MonitorDeviceName)
                    {
                        // Monitor Seleccionado: Forzar Resolución, Hz y fijar coordenada Cero (Primary)
                        mode.dmPelsWidth = config.ResolucionWidth; 
                        mode.dmPelsHeight = config.ResolucionHeight;
                        mode.dmDisplayFrequency = config.RefreshRate; 
                        mode.dmPositionX = 0; 
                        mode.dmPositionY = 0;
                        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_POSITION;
                        ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CDS_NORESET, IntPtr.Zero);
                    }
                    else
                    {
                        // Cortar señal eléctrica del hardware secundario (Apagar monitor por software)
                        mode.dmPelsWidth = 0; 
                        mode.dmPelsHeight = 0;
                        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
                        ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CDS_NORESET, IntPtr.Zero);
                    }
                }
                
                // Ejecutar el apagón y reconfiguración masiva en la GPU de un solo golpe
                ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            }
            catch { }
        }

        private string ObtenerRutaSteam()
        {
            try
            {
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                {
                    if (key != null) return Path.Combine(key.GetValue("InstallPath") as string ?? "", "steam.exe");
                }
            }
            catch { }
            return @"C:\Program Files (x86)\Steam\steam.exe"; 
        }

        private void CerrarSesion()
        {
            // Nota: Al usar flags puramente dinámicos en ChangeDisplaySettingsEx, al cerrarse la sesión,
            // Windows destruye los cambios en la RAM y recarga tu escritorio original intacto para el siguiente Login.
            Process.Start("shutdown", "/l /f");
            Shutdown();
        }
    }
}