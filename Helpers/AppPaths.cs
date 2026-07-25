using System.IO;

namespace SteamOSConfigurator.Helpers
{
    /// <summary>
    /// Centraliza todas las rutas del sistema utilizadas por la aplicación.
    /// </summary>
    public static class AppPaths
    {
        // ── Raíz de datos ──
        public static readonly string RaizDatos = @"C:\ProgramData\SteamOS";

        // ── Archivos de configuración ──
        public static string Config => Path.Combine(RaizDatos, "config.json");
        public static string MapeoConfig => Path.Combine(RaizDatos, "mapeo_config.json");
        public static string LogFile => Path.Combine(RaizDatos, "debug_log.txt");
        public static string Avatar => Path.Combine(RaizDatos, "avatar.png");

        // ── Ejecutable desplegado ──
        public static string EjecutableDestino => Path.Combine(RaizDatos, "WindowsLikeSteamOS.exe");

        // ── Dependencias externas ──
        public static readonly string RutaInstaladorRTSS = Path.Combine(RaizDatos, @"Dependencias\RTSSSetup.exe");
        public static readonly string RutaExeRTSS = @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe";
        public static readonly string RutaExeMSIAfterburner = @"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe";
        public static readonly string RutaPerfilGlobalRTSS = @"C:\Program Files (x86)\RivaTuner Statistics Server\Profiles\Global";

        // ── Drivers ──
        public static readonly string DriverViGEm = @"C:\Windows\System32\drivers\ViGEmBus.sys";
        public static readonly string DriverHidHide = @"C:\Windows\System32\drivers\HidHide.sys";

        // ── Steam ──
        public static readonly string SteamFallback = @"C:\Program Files (x86)\Steam\steam.exe";
        public static readonly string SteamRegistryKey = @"SOFTWARE\WOW6432Node\Valve\Steam";
    }
}
