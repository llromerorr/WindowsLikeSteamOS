using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace SteamOSConfigurator.Helpers.Profiles;

/// <summary>
/// Aplica el cambio ConfigEdit resuelto por ProfileResolver sobre un .ini real
/// (formato "[Seccion]" + "Clave=Valor"), preservando el resto del archivo.
/// Pensado para ejecutarse ANTES de lanzar el proceso del juego.
/// </summary>
public static class IniConfigEditor
{
    /// <summary>
    /// Aplica la edición descrita en <paramref name="action"/>. No hace nada
    /// si la estrategia resuelta no es ConfigEdit o faltan datos.
    /// Devuelve true si el archivo fue modificado.
    /// </summary>
    public static bool Apply(ResolvedAction action, bool backupOriginal = true)
    {
        if (action.Strategy != GameStrategy.ConfigEdit)
            return false;

        if (string.IsNullOrWhiteSpace(action.ExpandedConfigPath) ||
            string.IsNullOrWhiteSpace(action.ConfigKey) ||
            action.ConfigValue is null)
        {
            return false;
        }

        var path = action.ExpandedConfigPath;

        if (!File.Exists(path))
        {
            // Muchos juegos generan el .ini en su primer arranque; si aún no
            // existe no podemos parchearlo. El caller debe decidir si lanza
            // el juego una vez para generarlo y reintentar.
            return false;
        }

        if (backupOriginal)
        {
            var backupPath = path + ".steamos_bak";
            if (!File.Exists(backupPath))
                File.Copy(path, backupPath);
        }

        var lines = File.ReadAllLines(path).ToList();
        var delimiter = string.IsNullOrEmpty(action.KeyValueDelimiter) ? "=" : action.KeyValueDelimiter;

        int sectionStart = -1;
        int sectionEnd = lines.Count;

        if (!string.IsNullOrWhiteSpace(action.ConfigSection))
        {
            var sectionHeader = $"[{action.ConfigSection}]";
            sectionStart = lines.FindIndex(l => l.Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase));

            if (sectionStart >= 0)
            {
                for (int i = sectionStart + 1; i < lines.Count; i++)
                {
                    if (lines[i].TrimStart().StartsWith('['))
                    {
                        sectionEnd = i;
                        break;
                    }
                }
            }
        }
        else
        {
            sectionStart = 0;
        }

        bool keyFound = false;

        if (sectionStart >= 0)
        {
            var searchFrom = string.IsNullOrWhiteSpace(action.ConfigSection) ? 0 : sectionStart + 1;

            for (int i = searchFrom; i < sectionEnd; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                    continue;

                var idx = lines[i].IndexOf(delimiter, StringComparison.Ordinal);
                if (idx <= 0) continue;

                var key = lines[i][..idx].Trim();
                if (!string.Equals(key, action.ConfigKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                lines[i] = $"{action.ConfigKey}{delimiter}{action.ConfigValue}";
                keyFound = true;
                break;
            }
        }

        if (!keyFound)
        {
            // La clave no existía: la insertamos al final de la sección
            // (o del archivo, si no se especificó sección).
            if (!string.IsNullOrWhiteSpace(action.ConfigSection) && sectionStart < 0)
            {
                lines.Add(string.Empty);
                lines.Add($"[{action.ConfigSection}]");
                lines.Add($"{action.ConfigKey}{delimiter}{action.ConfigValue}");
            }
            else
            {
                var insertAt = string.IsNullOrWhiteSpace(action.ConfigSection) ? lines.Count : sectionEnd;
                lines.Insert(insertAt, $"{action.ConfigKey}{delimiter}{action.ConfigValue}");
            }
        }

        File.WriteAllLines(path, lines);
        return true;
    }
}
