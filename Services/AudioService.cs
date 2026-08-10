using System;
using AudioSwitcher.AudioApi.CoreAudio;

namespace SteamOSConfigurator.Services
{
    public interface IAudioService : IDisposable
    {
        void EstablecerDispositivoPorDefecto(string nombreDispositivo);
        int ObtenerVolumenActual();
        int AjustarVolumen(int cambio);
        bool AlternarSilencio();
        bool EstaSilenciado();
    }

    public class AudioService : IAudioService
    {
        private readonly CoreAudioController _ctrl;

        public AudioService()
        {
            _ctrl = new CoreAudioController();
        }

        public void EstablecerDispositivoPorDefecto(string nombreDispositivo)
        {
            if (string.IsNullOrEmpty(nombreDispositivo) || nombreDispositivo == "Salida de audio por defecto") 
                return;

            try 
            { 
                foreach (var dev in _ctrl.GetPlaybackDevices()) 
                {
                    if (dev.FullName == nombreDispositivo) 
                    { 
                        dev.SetAsDefault(); 
                        break; 
                    } 
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error al establecer dispositivo de audio: {ex.Message}");
            }
        }

        public int ObtenerVolumenActual()
        {
            try
            {
                var defaultDev = _ctrl.DefaultPlaybackDevice;
                if (defaultDev != null)
                {
                    return (int)Math.Round(defaultDev.Volume);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[AudioService] Error obteniendo volumen actual: {ex.Message}");
            }
            return 100;
        }

        public int AjustarVolumen(int cambio)
        {
            try
            {
                var defaultDev = _ctrl.DefaultPlaybackDevice;
                if (defaultDev != null)
                {
                    double volActual = defaultDev.Volume;
                    double nuevoVol = Math.Clamp(volActual + cambio, 0, 100);
                    defaultDev.Volume = nuevoVol;
                    return (int)Math.Round(nuevoVol);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[AudioService] Error ajustando volumen: {ex.Message}");
            }
            return 100;
        }

        public void EstablecerVolumen(int nuevoVolumen)
        {
            try
            {
                var defaultDev = _ctrl.DefaultPlaybackDevice;
                if (defaultDev != null)
                {
                    defaultDev.Volume = Math.Clamp(nuevoVolumen, 0, 100);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[AudioService] Error estableciendo volumen: {ex.Message}");
            }
        }

        public bool AlternarSilencio()
        {
            try
            {
                var defaultDev = _ctrl.DefaultPlaybackDevice;
                if (defaultDev != null)
                {
                    bool nuevoEstado = !defaultDev.IsMuted;
                    defaultDev.Mute(nuevoEstado);
                    return nuevoEstado;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[AudioService] Error alternando silencio: {ex.Message}");
            }
            return false;
        }

        public bool EstaSilenciado()
        {
            try
            {
                var defaultDev = _ctrl.DefaultPlaybackDevice;
                if (defaultDev != null)
                {
                    return defaultDev.IsMuted;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[AudioService] Error consultando estado de silencio: {ex.Message}");
            }
            return false;
        }

        public void Dispose()
        {
            try
            {
                _ctrl?.Dispose();
            }
            catch { }
        }
    }
}
