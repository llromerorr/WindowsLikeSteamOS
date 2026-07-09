using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SteamOSConfigurator.Helpers;
using SteamOSConfigurator.Models;

namespace SteamOSConfigurator.Services
{
    public interface IDisplayService
    {
        void AislarPantalla(ConfiguracionSteamOS config, IGpuScalingService gpuScalingService);
        void RestaurarEntornoOriginal(IGpuScalingService gpuScalingService);
        bool AislamientoActivo { get; }
    }

    public class DisplayService : IDisplayService
    {
        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Ansi)]
        public struct DEVMODE_ANSI
        {
            [FieldOffset(0)][MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
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
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool EnumDisplayDevices(string? lpDevice, int iDevNum, ref DisplayHelper.DISPLAY_DEVICE lpDisplayDevice, int dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern int EnumDisplaySettingsA(string? deviceName, int modeNum, ref DEVMODE_ANSI devMode);

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

        private Dictionary<string, DEVMODE_ANSI> _monitoresOriginales = new();
        private bool _aislamientoActivo = false;

        public bool AislamientoActivo => _aislamientoActivo;

        public void AislarPantalla(ConfiguracionSteamOS config, IGpuScalingService gpuScalingService)
        {
            try
            {
                Logger.Log("[AislarPantalla] Iniciando aislamiento de pantalla...");
                int id = 0;
                DisplayHelper.DISPLAY_DEVICE dd = new DisplayHelper.DISPLAY_DEVICE { cb = Marshal.SizeOf<DisplayHelper.DISPLAY_DEVICE>() };
                List<string> activos = new();
                string? monitorPrincipalSistema = null;

                while (EnumDisplayDevices(null, id, ref dd, 0))
                {
                    Logger.Log($"[AislarPantalla] Monitor {id}: Name={dd.DeviceName}, Friendly={dd.DeviceString}, StateFlags={dd.StateFlags:X}");
                    if ((dd.StateFlags & 0x1) != 0) // DISPLAY_DEVICE_ACTIVE
                    {
                        activos.Add(dd.DeviceName);
                        if ((dd.StateFlags & 0x4) != 0) // DISPLAY_DEVICE_PRIMARY_DEVICE
                            monitorPrincipalSistema = dd.DeviceName;

                        if (!_monitoresOriginales.ContainsKey(dd.DeviceName))
                        {
                            DEVMODE_ANSI modeOrig = new DEVMODE_ANSI { dmSize = (short)Marshal.SizeOf<DEVMODE_ANSI>() };
                            if (EnumDisplaySettingsA(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref modeOrig) != 0)
                            {
                                _monitoresOriginales[dd.DeviceName] = modeOrig;
                                Logger.Log($"[AislarPantalla] Guardado modo original para {dd.DeviceName}: {modeOrig.dmPelsWidth}x{modeOrig.dmPelsHeight}@{modeOrig.dmDisplayFrequency}Hz");
                            }
                        }
                    }
                    id++;
                    dd = new DisplayHelper.DISPLAY_DEVICE { cb = Marshal.SizeOf<DisplayHelper.DISPLAY_DEVICE>() };
                }

                if (activos.Count == 0)
                {
                    Logger.Log("[AislarPantalla] ERROR: No se encontraron pantallas activas.");
                    return;
                }

                string monitorObjetivo = "";
                bool monitorEncontrado = false;

                foreach (string deviceName in activos)
                {
                    string idFisico = DisplayHelper.ObtenerDeviceIdFisico(deviceName);
                    Logger.Log($"[AislarPantalla] Pantalla activa: {deviceName}, ID Físico: '{idFisico}'");
                    if (!string.IsNullOrEmpty(config.MonitorDeviceId) && idFisico == config.MonitorDeviceId)
                    {
                        monitorObjetivo = deviceName;
                        monitorEncontrado = true;
                        Logger.Log($"[AislarPantalla] Coincidencia por ID Físico: '{idFisico}' -> {deviceName}");
                        break;
                    }
                    else if (deviceName == config.MonitorDeviceName)
                    {
                        monitorObjetivo = deviceName;
                        monitorEncontrado = true;
                        Logger.Log($"[AislarPantalla] Coincidencia por nombre: '{deviceName}'");
                    }
                }

                if (!monitorEncontrado)
                {
                    monitorObjetivo = monitorPrincipalSistema ?? activos[0];
                    Logger.Log($"[AislarPantalla] Pantalla configurada no encontrada. Usando por defecto: {monitorObjetivo}");
                }

                Logger.Log($"[AislarPantalla] Pantalla objetivo final: {monitorObjetivo}");

                foreach (string deviceName in activos)
                {
                    if (deviceName == monitorObjetivo)
                    {
                        DEVMODE_ANSI mode = new DEVMODE_ANSI { dmSize = (short)Marshal.SizeOf<DEVMODE_ANSI>() };
                        EnumDisplaySettingsA(deviceName, ENUM_CURRENT_SETTINGS, ref mode);

                        Logger.Log($"[AislarPantalla] Configurando pantalla principal {deviceName}: {config.ResolucionWidth}x{config.ResolucionHeight}@{config.RefreshRate}Hz...");
                        mode.dmPelsWidth = (uint)config.ResolucionWidth;
                        mode.dmPelsHeight = (uint)config.ResolucionHeight;
                        mode.dmBitsPerPel = 32;
                        mode.dmDisplayFrequency = (uint)config.RefreshRate;
                        mode.dmPositionX = 0;
                        mode.dmPositionY = 0;
                        mode.dmDisplayFixedOutput = DMDFO_DEFAULT;
                        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY | DM_POSITION | DM_DISPLAYFIXEDOUTPUT;

                        int res = ChangeDisplaySettingsExA(deviceName, ref mode, IntPtr.Zero, CDS_SET_PRIMARY | CDS_UPDATEREGISTRY | CDS_NORESET | CDS_GLOBAL, IntPtr.Zero);
                        Logger.Log($"[AislarPantalla] Resultado ChangeDisplaySettingsExA para {deviceName}: {res}");
                    }
                    else
                    {
                        Logger.Log($"[AislarPantalla] Desconectando pantalla secundaria {deviceName}...");
                        DEVMODE_ANSI modeDetach = new DEVMODE_ANSI { dmSize = (short)Marshal.SizeOf<DEVMODE_ANSI>() };
                        modeDetach.dmPelsWidth = 0;
                        modeDetach.dmPelsHeight = 0;
                        modeDetach.dmPositionX = 0;
                        modeDetach.dmPositionY = 0;
                        modeDetach.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;

                        int res = ChangeDisplaySettingsExA(deviceName, ref modeDetach, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET | CDS_GLOBAL, IntPtr.Zero);
                        Logger.Log($"[AislarPantalla] Resultado ChangeDisplaySettingsExA (Desconexión) para {deviceName}: {res}");
                    }
                }

                Logger.Log("[AislarPantalla] Aplicando cambios finales (ChangeDisplaySettingsExReset)...");
                int resReset = ChangeDisplaySettingsExReset(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                Logger.Log($"[AislarPantalla] Resultado final Reset: {resReset}");
                _aislamientoActivo = true;

                Logger.Log("[AislarPantalla] Forzando escalado completo GPU en segundo plano...");
                Task.Run(() => 
                { 
                    Thread.Sleep(300); 
                    gpuScalingService.ForzarEscaladoCompleto(); 
                    Logger.Log("[AislarPantalla] Tarea de forzado de escalado completo ejecutada."); 
                });
            }
            catch (Exception ex) { Logger.Log($"[AislarPantalla] Error al aislar pantalla: {ex.Message}"); }
        }

        public void RestaurarEntornoOriginal(IGpuScalingService gpuScalingService)
        {
            if (!_aislamientoActivo) return;
            try
            {
                Logger.Log("[RestaurarEntornoOriginal] Iniciando restauración de pantallas originales...");
                if (_monitoresOriginales.Count > 0)
                {
                    foreach (var kvp in _monitoresOriginales)
                    {
                        DEVMODE_ANSI mode = kvp.Value;
                        if (mode.dmPositionX == 0 && mode.dmPositionY == 0)
                        {
                            Logger.Log($"[RestaurarEntornoOriginal] Restaurando pantalla principal {kvp.Key}: {mode.dmPelsWidth}x{mode.dmPelsHeight}@{mode.dmDisplayFrequency}Hz...");
                            mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY | DM_POSITION;
                            int res = ChangeDisplaySettingsExA(kvp.Key, ref mode, IntPtr.Zero, CDS_SET_PRIMARY | CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
                            Logger.Log($"[RestaurarEntornoOriginal] Resultado para {kvp.Key}: {res}");
                        }
                    }

                    foreach (var kvp in _monitoresOriginales)
                    {
                        DEVMODE_ANSI mode = kvp.Value;
                        if (mode.dmPositionX == 0 && mode.dmPositionY == 0) continue;
                        Logger.Log($"[RestaurarEntornoOriginal] Restaurando pantalla secundaria {kvp.Key}: {mode.dmPelsWidth}x{mode.dmPelsHeight} en ({mode.dmPositionX},{mode.dmPositionY})...");
                        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY | DM_POSITION;
                        int res = ChangeDisplaySettingsExA(kvp.Key, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
                        Logger.Log($"[RestaurarEntornoOriginal] Resultado para {kvp.Key}: {res}");
                    }
                }

                Logger.Log("[RestaurarEntornoOriginal] Aplicando cambios finales (ChangeDisplaySettingsExReset)...");
                int resReset = ChangeDisplaySettingsExReset(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                Logger.Log($"[RestaurarEntornoOriginal] Resultado final Reset: {resReset}");
                
                _aislamientoActivo = false;
                Logger.Log("[RestaurarEntornoOriginal] Restaurando escalado por monitor en segundo plano...");
                Task.Run(() => 
                { 
                    gpuScalingService.RestaurarEscaladoPorMonitor(); 
                    Logger.Log("[RestaurarEntornoOriginal] Tarea de restauración de escalado ejecutada."); 
                });
            }
            catch (Exception ex) { Logger.Log($"[RestaurarEntornoOriginal] Error al restaurar entorno original: {ex.Message}"); }
        }
    }
}
