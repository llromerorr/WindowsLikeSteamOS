using System;

namespace SteamOSConfigurator.Models
{
    /// <summary>
    /// Modelo de configuración persistente del entorno SteamOS.
    /// Se serializa/deserializa desde config.json.
    /// </summary>
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
}
