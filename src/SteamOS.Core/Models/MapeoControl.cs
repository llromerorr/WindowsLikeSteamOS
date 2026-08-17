using System.Collections.Generic;

namespace SteamOSConfigurator
{
    public class MapeoControl
    {
        public string NombreControl { get; set; } = string.Empty;
        public int VendorID { get; set; }
        public int ProductID { get; set; }
        public Dictionary<string, int> Botones { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, string> Ejes { get; set; } = new Dictionary<string, string>();
    }
}
