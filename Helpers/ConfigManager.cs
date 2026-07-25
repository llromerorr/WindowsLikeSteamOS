using System;
using System.IO;
using System.Text.Json;
using SteamOSConfigurator.Helpers;
using SteamOSConfigurator.Models;

namespace SteamOSConfigurator.Helpers
{
    public static class ConfigManager
    {
        private static readonly JsonSerializerOptions _options = new JsonSerializerOptions { WriteIndented = true };

        public static ConfiguracionSteamOS CargarConfiguracion()
        {
            try
            {
                if (File.Exists(AppPaths.Config))
                {
                    string json = File.ReadAllText(AppPaths.Config);
                    return JsonSerializer.Deserialize<ConfiguracionSteamOS>(json, _options) ?? new ConfiguracionSteamOS();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error al cargar config: {ex.Message}");
            }
            return new ConfiguracionSteamOS();
        }

        public static void GuardarConfiguracion(ConfiguracionSteamOS config)
        {
            try
            {
                string dir = Path.GetDirectoryName(AppPaths.Config) ?? string.Empty;
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(config, _options);
                File.WriteAllText(AppPaths.Config, json);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error al guardar config: {ex.Message}");
            }
        }
    }
}
