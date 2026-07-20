using System;
using AudioSwitcher.AudioApi.CoreAudio;

namespace SteamOSConfigurator.Services
{
    public interface IAudioService
    {
        void EstablecerDispositivoPorDefecto(string nombreDispositivo);
    }

    public class AudioService : IAudioService
    {
        public void EstablecerDispositivoPorDefecto(string nombreDispositivo)
        {
            if (string.IsNullOrEmpty(nombreDispositivo) || nombreDispositivo == "Salida de audio por defecto") 
                return;

            try 
            { 
                using (CoreAudioController ctrl = new CoreAudioController())
                {
                    foreach (var dev in ctrl.GetPlaybackDevices()) 
                    {
                        if (dev.FullName == nombreDispositivo) 
                        { 
                            dev.SetAsDefault(); 
                            break; 
                        } 
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error al establecer dispositivo de audio: {ex.Message}");
            }
        }
    }
}
