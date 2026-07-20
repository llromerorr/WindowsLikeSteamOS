using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamOSConfigurator.Services
{
    public interface IPowerService
    {
        void ActivarPlanMaximoRendimiento();
        void RestaurarPlanEnergia();
        void PrevenirSuspensionAutomatica();
        void PermitirSuspension();
    }

    public class PowerService : IPowerService
    {
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        // GUID conocido de Windows para el plan "Alto rendimiento"
        private const string HIGH_PERFORMANCE_GUID = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        private string? _planOriginalGuid;

        public void ActivarPlanMaximoRendimiento()
        {
            try
            {
                // Guardar el plan de energía actual
                _planOriginalGuid = ObtenerPlanActivoGuid();
                Logger.Log($"Plan de energía original guardado: {_planOriginalGuid}");

                // Activar plan de alto rendimiento
                var psi = new ProcessStartInfo("powercfg", $"/setactive {HIGH_PERFORMANCE_GUID}")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using (var proc = Process.Start(psi))
                {
                    proc?.WaitForExit();
                }
                Logger.Log("Plan de energía 'Alto Rendimiento' activado.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error al activar plan de máximo rendimiento: {ex.Message}");
            }
        }

        public void RestaurarPlanEnergia()
        {
            if (string.IsNullOrEmpty(_planOriginalGuid)) return;
            try
            {
                var psi = new ProcessStartInfo("powercfg", $"/setactive {_planOriginalGuid}")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using (var proc = Process.Start(psi))
                {
                    proc?.WaitForExit();
                }
                Logger.Log($"Plan de energía restaurado a: {_planOriginalGuid}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error al restaurar plan de energía: {ex.Message}");
            }
        }

        public void PrevenirSuspensionAutomatica()
        {
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
            Logger.Log("Suspensión automática deshabilitada.");
        }

        public void PermitirSuspension()
        {
            SetThreadExecutionState(ES_CONTINUOUS);
            Logger.Log("Suspensión automática restaurada.");
        }

        private string? ObtenerPlanActivoGuid()
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return null;
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    int guidStart = output.IndexOf(':');
                    if (guidStart < 0) return null;
                    string afterColon = output.Substring(guidStart + 1).Trim();
                    int guidEnd = afterColon.IndexOf(' ');
                    if (guidEnd < 0) guidEnd = afterColon.Length;
                    return afterColon.Substring(0, guidEnd).Trim();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error al obtener plan de energía activo: {ex.Message}");
                return null;
            }
        }
    }
}
