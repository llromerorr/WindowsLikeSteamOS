using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System;

namespace SteamOSConfigurator.Helpers.Profiles;

/// <summary>
/// Estrategia a aplicar sobre la ventana del juego, decidida por perfil.
/// </summary>
public enum GameStrategy
{
    /// <summary>AppCompatFlags + SetWindowLongPtr/SetWindowPos (tu solución actual).</summary>
    Standard,

    /// <summary>Editar el .ini nativo del juego ANTES de lanzarlo (motores que ignoran AppCompatFlags, ej. RE Engine).</summary>
    ConfigEdit,

    /// <summary>Enviar Alt+Enter sintético vía SendInput para que el propio juego transicione (motores con swapchain frágil, ej. Havok/FromSoftware).</summary>
    SimulateAltEnter
}

/// <summary>
/// Representación 1:1 de una entrada del JSON de perfiles.
/// </summary>
public sealed class GameProfile
{
    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = "*";

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "GENERIC";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("strategy")]
    public GameStrategy Strategy { get; set; } = GameStrategy.Standard;

    // --- Campos usados solo por ConfigEdit ---
    [JsonPropertyName("configPath")]
    public string? ConfigPath { get; set; }

    [JsonPropertyName("configSection")]
    public string? ConfigSection { get; set; }

    [JsonPropertyName("configKey")]
    public string? ConfigKey { get; set; }

    [JsonPropertyName("configValue")]
    public string? ConfigValue { get; set; }

    [JsonPropertyName("keyValueDelimiter")]
    public string KeyValueDelimiter { get; set; } = "=";

    // --- Campos usados solo por SimulateAltEnter ---
    [JsonPropertyName("preDelayMs")]
    public int PreDelayMs { get; set; } = 2500;

    [JsonPropertyName("postAltEnterDelayMs")]
    public int PostAltEnterDelayMs { get; set; } = 500;

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

internal sealed class ProfileRoot
{
    [JsonPropertyName("profiles")]
    public List<GameProfile> Profiles { get; set; } = new();
}

/// <summary>
/// Resultado ya resuelto y listo para consumir por el orquestador de lanzamiento.
/// Las rutas ya vienen expandidas (variables de entorno resueltas).
/// </summary>
public sealed class ResolvedAction
{
    public required GameStrategy Strategy { get; init; }
    public required string DisplayName { get; init; }
    public required string Engine { get; init; }

    // ConfigEdit
    public string? ExpandedConfigPath { get; init; }
    public string? ConfigSection { get; init; }
    public string? ConfigKey { get; init; }
    public string? ConfigValue { get; init; }
    public string KeyValueDelimiter { get; init; } = "=";

    // SimulateAltEnter
    public int PreDelayMs { get; init; }
    public int PostAltEnterDelayMs { get; init; }
}

/// <summary>
/// Carga juegos_perfiles.json y resuelve, dado un exePath, qué técnica aplicar.
/// Uso típico:
///   var resolver = ProfileResolver.LoadFromFile("juegos_perfiles.json");
///   var accion = resolver.Resolve(@"C:\Games\DarkSoulsIII\Game\DarkSoulsIII.exe");
/// </summary>
public sealed class ProfileResolver
{
    private readonly List<GameProfile> _profiles;
    private readonly GameProfile _fallback;

    private ProfileResolver(List<GameProfile> profiles)
    {
        _profiles = profiles;
        _fallback = profiles.FirstOrDefault(p => p.ProcessName == "*")
                    ?? new GameProfile
                    {
                        ProcessName = "*",
                        Engine = "GENERIC",
                        DisplayName = "Fallback genérico",
                        Strategy = GameStrategy.Standard
                    };
    }

    /// <summary>Carga y parsea el JSON de perfiles desde disco.</summary>
    public static ProfileResolver LoadFromFile(string jsonPath)
    {
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException($"No se encontró el archivo de perfiles: {jsonPath}");

        var json = File.ReadAllText(jsonPath);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var root = JsonSerializer.Deserialize<ProfileRoot>(json, options)
                   ?? throw new InvalidDataException("El JSON de perfiles está vacío o mal formado.");

        return new ProfileResolver(root.Profiles);
    }

    /// <summary>
    /// Dado el path completo del ejecutable, determina la estrategia a aplicar
    /// y expande cualquier ruta de configuración (%USERPROFILE%, etc.).
    /// El match es por nombre de archivo, case-insensitive. Si no hay match
    /// específico, cae al perfil "*" (Standard).
    /// </summary>
    public ResolvedAction Resolve(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);

        var fileName = Path.GetFileName(exePath);

        var profile = _profiles.FirstOrDefault(p =>
                          !string.IsNullOrEmpty(p.ProcessName) &&
                          p.ProcessName != "*" &&
                          string.Equals(p.ProcessName, fileName, StringComparison.OrdinalIgnoreCase))
                      ?? _fallback;

        string? expandedPath = null;
        if (profile.Strategy == GameStrategy.ConfigEdit && !string.IsNullOrWhiteSpace(profile.ConfigPath))
        {
            expandedPath = Environment.ExpandEnvironmentVariables(profile.ConfigPath);
        }

        return new ResolvedAction
        {
            Strategy = profile.Strategy,
            DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? fileName : profile.DisplayName,
            Engine = profile.Engine,
            ExpandedConfigPath = expandedPath,
            ConfigSection = profile.ConfigSection,
            ConfigKey = profile.ConfigKey,
            ConfigValue = profile.ConfigValue,
            KeyValueDelimiter = profile.KeyValueDelimiter,
            PreDelayMs = profile.PreDelayMs,
            PostAltEnterDelayMs = profile.PostAltEnterDelayMs
        };
    }
}
