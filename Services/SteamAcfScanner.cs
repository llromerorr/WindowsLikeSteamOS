using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WindowsLikeSteamOS.Services
{
    /// <summary>
    /// Escanea los archivos appmanifest_*.acf de Steam para mapear:
    ///   AppID → InstallDir → lista de EXEs
    /// </summary>
    public static class SteamAcfScanner
    {
        public record GameEntry(string AppId, string Name, string InstallDir, string LibraryPath);

        /// <summary>
        /// Devuelve todos los juegos instalados en Steam con sus metadatos.
        /// </summary>
        public static List<GameEntry> GetInstalledGames(string steamPath)
        {
            var results = new List<GameEntry>();

            // Carpetas de library (steamapps principales + bibliotecas adicionales)
            var libraryFolders = GetLibraryFolders(steamPath);

            foreach (var library in libraryFolders)
            {
                var acfFiles = Directory.GetFiles(library, "appmanifest_*.acf", SearchOption.TopDirectoryOnly);
                foreach (var acf in acfFiles)
                {
                    try
                    {
                        var content = File.ReadAllText(acf);
                        var appId    = ExtractVdfValue(content, "appid");
                        var name     = ExtractVdfValue(content, "name");
                        var installDir = ExtractVdfValue(content, "installdir");

                        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(installDir))
                            continue;

                        string fullInstall = Path.Combine(library, "common", installDir);
                        if (!Directory.Exists(fullInstall))
                            continue;

                        results.Add(new GameEntry(appId, name ?? installDir, fullInstall, library));
                    }
                    catch { /* ignorar ACFs corruptos */ }
                }
            }

            return results;
        }

        /// <summary>
        /// Dado un nombre de proceso (ej: "re3.exe"), busca el juego instalado que contiene ese exe.
        /// Devuelve el GameEntry si lo encuentra, null si no.
        /// </summary>
        public static GameEntry? FindByExeName(List<GameEntry> games, string exeName)
        {
            string nameOnly = Path.GetFileName(exeName).ToLowerInvariant();

            foreach (var game in games)
            {
                try
                {
                    var exes = Directory.GetFiles(game.InstallDir, "*.exe", SearchOption.AllDirectories);
                    if (exes.Any(e => Path.GetFileName(e).ToLowerInvariant() == nameOnly))
                        return game;
                }
                catch { }
            }
            return null;
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        private static List<string> GetLibraryFolders(string steamPath)
        {
            var folders = new List<string>();

            // Librería principal
            string mainLib = Path.Combine(steamPath, "steamapps");
            if (Directory.Exists(mainLib)) folders.Add(mainLib);

            // Librerías adicionales desde libraryfolders.vdf
            string vdfPath = Path.Combine(mainLib, "libraryfolders.vdf");
            if (!File.Exists(vdfPath)) return folders;

            try
            {
                var content = File.ReadAllText(vdfPath);
                // Las rutas están bajo claves "path" dentro de cada entrada numerada
                var matches = Regex.Matches(content, @"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase);
                foreach (Match m in matches)
                {
                    string rawPath = m.Groups[1].Value.Replace(@"\\", @"\");
                    string libSteamapps = Path.Combine(rawPath, "steamapps");
                    if (Directory.Exists(libSteamapps) && !folders.Contains(libSteamapps))
                        folders.Add(libSteamapps);
                }
            }
            catch { }

            return folders;
        }

        private static string? ExtractVdfValue(string vdf, string key)
        {
            var m = Regex.Match(vdf, $@"""{Regex.Escape(key)}""\s+""([^""]+)""", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
