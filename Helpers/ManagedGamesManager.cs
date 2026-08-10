using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SteamOSConfigurator;
using WindowsLikeSteamOS.Services;

namespace SteamOSConfigurator.Helpers
{
    public class ManagedGame
    {
        public string Name { get; set; } = "";
        /// <summary>
        /// Directorio del juego donde se despliega dxgi.dll.
        /// Para juegos Steam = InstallDir. Para manuales = carpeta del .exe seleccionado.
        /// </summary>
        public string GameDir { get; set; } = "";
        public bool IsSteamGame { get; set; } = false;
        public string AppId { get; set; } = "";

        /// <summary>
        /// Determina si nuestro entorno (ReShade + WLSOS.addon) está instalado en la carpeta del juego.
        /// </summary>
        public bool IsPluginInstalled
        {
            get
            {
                if (string.IsNullOrEmpty(GameDir) || !Directory.Exists(GameDir)) return false;
                
                string dxgiPath = Path.Combine(GameDir, "dxgi.dll");
                string addonPath = Path.Combine(GameDir, "reshade-addons", "WLSOS.addon");

                return File.Exists(dxgiPath) && File.Exists(addonPath);
            }
        }
    }

    public static class ManagedGamesManager
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteamOS", "managed_games.json");

        /// <summary>
        /// Busca el archivo oficial ReShade64.dll en el repositorio local.
        /// </summary>
        public static string GetSourceReShadeDll()
        {
            string repoFile = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "files", "ReShade64.dll"));
            if (File.Exists(repoFile)) return repoFile;

            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReShade64.dll");
            if (File.Exists(local)) return local;

            return "";
        }

        /// <summary>
        /// Busca el addon WLSOS.addon.
        /// </summary>
        public static string GetSourceAddon()
        {
            string buildPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "SteamOSHooks64", "build_addon", "Release", "WLSOS.addon"));
            if (File.Exists(buildPath)) return buildPath;

            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WLSOS.addon");
            if (File.Exists(local)) return local;

            return "";
        }

        /// <summary>
        /// Busca la plantilla ReShade.ini.template.
        /// </summary>
        public static string GetSourceIniTemplate()
        {
            string repoFile = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "files", "ReShade.ini.template"));
            if (File.Exists(repoFile)) return repoFile;

            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReShade.ini.template");
            if (File.Exists(local)) return local;

            return "";
        }

        /// <summary>
        /// Carga la lista de juegos guardados + escanea Steam para nuevos.
        /// </summary>
        public static List<ManagedGame> GetGames()
        {
            var games = new List<ManagedGame>();
            var knownDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Cargar juegos previamente guardados
            if (File.Exists(ConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(ConfigPath);
                    var savedGames = JsonSerializer.Deserialize<List<ManagedGame>>(json);
                    if (savedGames != null)
                    {
                        foreach (var g in savedGames)
                        {
                            if (Directory.Exists(g.GameDir))
                            {
                                games.Add(g);
                                knownDirs.Add(g.GameDir);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ManagedGamesManager] Error leyendo {ConfigPath}: {ex.Message}");
                }
            }

            // 2. Escanear Steam e integrar juegos nuevos automáticamente
            try
            {
                string steamPath = FindSteamPath();
                if (!string.IsNullOrEmpty(steamPath))
                {
                    var steamGames = SteamAcfScanner.GetInstalledGames(steamPath);
                    foreach (var sg in steamGames)
                    {
                        if (string.IsNullOrEmpty(sg.InstallDir) || !Directory.Exists(sg.InstallDir))
                            continue;

                        if (!knownDirs.Contains(sg.InstallDir))
                        {
                            games.Add(new ManagedGame
                            {
                                Name = sg.Name,
                                GameDir = sg.InstallDir,
                                IsSteamGame = true,
                                AppId = sg.AppId
                            });
                            knownDirs.Add(sg.InstallDir);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ManagedGamesManager] Error escaneando Steam: {ex.Message}");
            }

            // Ordenar: primero los instalados, luego alfabéticamente
            games = games.OrderByDescending(g => g.IsPluginInstalled)
                         .ThenBy(g => g.Name)
                         .ToList();

            SaveGames(games);
            return games;
        }

        public static void SaveGames(List<ManagedGame> games)
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath) ?? "";
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(games, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ManagedGamesManager] Error guardando {ConfigPath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Añade un juego manualmente por ruta de .exe.
        /// </summary>
        public static ManagedGame? AddManualGame(string exePath)
        {
            if (!File.Exists(exePath)) return null;

            string gameDir = Path.GetDirectoryName(exePath) ?? "";
            if (string.IsNullOrEmpty(gameDir)) return null;

            var games = GetGames();
            var existing = games.FirstOrDefault(g => g.GameDir.Equals(gameDir, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing; // Ya existe

            var newGame = new ManagedGame
            {
                Name = Path.GetFileNameWithoutExtension(exePath),
                GameDir = gameDir,
                IsSteamGame = false
            };
            games.Add(newGame);
            SaveGames(games);
            return newGame;
        }

        /// <summary>
        /// Instala ReShade + Addon en la carpeta del juego.
        /// </summary>
        public static bool InstallPlugin(ManagedGame game)
        {
            if (string.IsNullOrEmpty(game.GameDir) || !Directory.Exists(game.GameDir))
                return false;

            string sourceReshade = GetSourceReShadeDll();
            if (string.IsNullOrEmpty(sourceReshade))
            {
                throw new FileNotFoundException("Falta ReShade64.dll. Debes colocar el DLL oficial de ReShade en la carpeta 'files' del repositorio o en la misma carpeta del ejecutable de la app para que funcione el auto-despliegue.");
            }

            string sourceAddon = GetSourceAddon();
            if (string.IsNullOrEmpty(sourceAddon))
            {
                throw new FileNotFoundException("Falta WLSOS.addon. Asegúrate de compilar el addon antes de intentar instalarlo.");
            }

            string sourceIni = GetSourceIniTemplate();
            
            string targetDxgi = Path.Combine(game.GameDir, "dxgi.dll");
            string targetAddonsDir = Path.Combine(game.GameDir, "reshade-addons");
            string targetAddon = Path.Combine(targetAddonsDir, "WLSOS.addon");
            string targetIni = Path.Combine(game.GameDir, "ReShade.ini");

            try
            {
                // Copiar ReShade64.dll como dxgi.dll
                File.Copy(sourceReshade, targetDxgi, true);

                // Copiar el Addon WLSOS.addon
                if (!Directory.Exists(targetAddonsDir))
                {
                    Directory.CreateDirectory(targetAddonsDir);
                }
                File.Copy(sourceAddon, targetAddon, true);

                // Copiar plantilla INI (solo si no existe, o forzamos sobrescritura parcial? Mejor forzamos para aplicar reglas)
                if (!string.IsNullOrEmpty(sourceIni) && File.Exists(sourceIni))
                {
                    // Si ya existe un ReShade.ini, podríamos no querer romper los shaders del usuario,
                    // pero si estamos instalando nuestro sistema por primera vez o re-aplicando, forzamos:
                    File.Copy(sourceIni, targetIni, true);
                }
                else
                {
                    // Fallback minimal
                    File.WriteAllText(targetIni, "[INPUT]\r\nKeyOverlay=0,0,0,0\r\n[OVERLAY]\r\nTutorialProgress=4\r\nShowFPS=0\r\nShowClock=0\r\nShowPresetName=0\r\n");
                }

                Logger.Log($"[ManagedGamesManager] Plugin y ReShade instalados exitosamente en {game.Name}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ManagedGamesManager] Error instalando plugin en {game.Name}: {ex.Message}");
                throw; 
            }
        }

        /// <summary>
        /// Desinstala nuestro entorno (Elimina dxgi.dll y el addon).
        /// </summary>
        public static bool UninstallPlugin(ManagedGame game)
        {
            if (string.IsNullOrEmpty(game.GameDir) || !Directory.Exists(game.GameDir))
                return false;

            string targetDxgi = Path.Combine(game.GameDir, "dxgi.dll");
            string targetAddon = Path.Combine(game.GameDir, "reshade-addons", "WLSOS.addon");

            try
            {
                if (File.Exists(targetAddon))
                    File.Delete(targetAddon);

                // Si eliminamos el addon, probablemente también queremos quitar el dxgi.dll para limpiar ReShade.
                // Ojo: si el usuario usaba ReShade por su cuenta, esto lo borrará.
                // Podríamos ser conservadores, pero nuestro QAM asume el control del DXGI.
                if (File.Exists(targetDxgi))
                    File.Delete(targetDxgi);

                Logger.Log($"[ManagedGamesManager] Plugin desinstalado de {game.Name}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ManagedGamesManager] Error desinstalando plugin en {game.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Elimina un juego de la lista gestionada (y desinstala el plugin si estaba).
        /// </summary>
        public static void RemoveGame(ManagedGame game)
        {
            UninstallPlugin(game);
            var games = GetGames();
            games.RemoveAll(g => g.GameDir.Equals(game.GameDir, StringComparison.OrdinalIgnoreCase));
            SaveGames(games);
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private static string FindSteamPath()
        {
            // Intentar rutas comunes
            string[] candidates = {
                @"C:\Program Files (x86)\Steam",
                @"D:\Steam",
                @"E:\Steam",
                @"D:\SteamLibrary",
            };

            foreach (var c in candidates)
            {
                if (File.Exists(Path.Combine(c, "steam.exe")))
                    return c;
            }

            // Intentar desde el registro
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                string? val = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                    return val;
            }
            catch { }

            return "";
        }
    }
}
