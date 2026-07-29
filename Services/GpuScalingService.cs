using System;

namespace SteamOSConfigurator.Services
{
    public interface IGpuScalingService
    {
        void ForzarEscaladoCompleto();
        void RestaurarEscaladoPorMonitor();
    }

    public class NvidiaGpuScalingService : IGpuScalingService
    {
        public void ForzarEscaladoCompleto()
        {
            try
            {
                NvidiaScaler.ForzarEscaladoCompleto((NvidiaScaler.NvScaling)2);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error en NvidiaGpuScalingService.ForzarEscaladoCompleto: {ex.Message}");
            }
        }

        public void RestaurarEscaladoPorMonitor()
        {
            try
            {
                // NvidiaScaler.RestaurarEscaladoPorMonitor();
                Logger.Log("[NvidiaGpuScalingService] RestaurarEscaladoPorMonitor llamado, pero desactivado por seguridad.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error en NvidiaGpuScalingService.RestaurarEscaladoPorMonitor: {ex.Message}");
            }
        }
    }
}
