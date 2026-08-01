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
        /// Determina si nuestro dxgi.dll está instalado en la carpeta del juego.
        /// </summary>
        public bool IsPluginInstalled
        {
            get
            {
                if (string.IsNullOrEmpty(GameDir) || !Directory.Exists(GameDir)) return false;
                string dxgiPath = Path.Combine(GameDir, "dxgi.dll");

                if (!File.Exists(dxgiPath)) return false;

                // Verificar que el dxgi.dll es el nuestro comparando tamaño con el source
                try
                {
                    string ourDxgi = GetSourceDllPath();
                    if (string.IsNullOrEmpty(ourDxgi) || !File.Exists(ourDxgi)) return true; // Asumimos que sí

                    return new FileInfo(dxgiPath).Length == new FileInfo(ourDxgi).Length;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static string GetSourceDllPath()
        {
            // Primero buscamos en ProgramData (post-deploy)
            string deployed = Path.Combine(@"C:\ProgramData\SteamOS", "dxgi.dll");
            if (File.Exists(deployed)) return deployed;

            // Luego en el directorio de la app
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SteamOSHooks64.dll");
            if (File.Exists(local)) return local;

            return "";
        }
    }

    public static class ManagedGamesManager
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteamOS", "managed_games.json");

        /// <summary>
        /// Obtiene la ruta al dxgi.dll fuente que copiamos a los juegos.
        /// </summary>
        public static string GetSourceDll()
        {
            // Prioridad 1: ProgramData (ya desplegado por el instalador)
            string deployed = Path.Combine(@"C:\ProgramData\SteamOS", "SteamOSHooks64.dll");
            if (File.Exists(deployed)) return deployed;

            // Prioridad 2: dxgi.dll junto al exe de la app
            string localDxgi = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SteamOSHooks64.dll");
            if (File.Exists(localDxgi)) return localDxgi;

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

        private static bool IsOurProxy(string dllPath)
        {
            try
            {
                var fileInfo = new FileInfo(dllPath);
                if (fileInfo.Length > 1024 * 1024) return false; // Nuesto proxy es de < 500KB. ReShade/DXVK son de varios MB.

                var bytes = File.ReadAllBytes(dllPath);
                string content = System.Text.Encoding.ASCII.GetString(bytes);
                return content.Contains("SteamOSHooks");
            }
            catch { return false; }
        }

        /// <summary>
        /// Instala nuestro dxgi.dll proxy en la carpeta del juego.
        /// Si hay un dxgi.dll de terceros (ReShade, DXVK), lanza una excepción para bloquear la instalación.
        /// </summary>
        public static bool InstallPlugin(ManagedGame game)
        {
            if (string.IsNullOrEmpty(game.GameDir) || !Directory.Exists(game.GameDir))
                return false;

            string sourceDll = GetSourceDll();
            if (string.IsNullOrEmpty(sourceDll))
            {
                Logger.Log("[ManagedGamesManager] No se encontró SteamOSHooks64.dll fuente para copiar.");
                return false;
            }

            string targetDll = Path.Combine(game.GameDir, "dxgi.dll");

            try
            {
                // Si ya hay un dxgi.dll y NO es nuestro, bloquear la instalación.
                if (File.Exists(targetDll) && !IsOurProxy(targetDll))
                {
                    Logger.Log($"[ManagedGamesManager] dxgi.dll de terceros detectado en {game.Name}. Instalación bloqueada por seguridad.");
                    throw new InvalidOperationException("Ya existe otra modificación (ReShade, DXVK, etc) instalada en este juego. Debes desinstalarla manualmente antes de activar el DLL Proxy.");
                }

                File.Copy(sourceDll, targetDll, true);
                Logger.Log($"[ManagedGamesManager] Plugin instalado exitosamente en {game.Name}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ManagedGamesManager] Error instalando plugin en {game.Name}: {ex.Message}");
                throw; // Rethrow para que la UI lo maneje
            }
        }

        /// <summary>
        /// Desinstala nuestro proxy.
        /// </summary>
        public static bool UninstallPlugin(ManagedGame game)
        {
            if (string.IsNullOrEmpty(game.GameDir) || !Directory.Exists(game.GameDir))
                return false;

            string targetDll = Path.Combine(game.GameDir, "dxgi.dll");

            try
            {
                // Solo borrar si es nuestro
                if (game.IsPluginInstalled && File.Exists(targetDll))
                {
                    File.Delete(targetDll);
                    Logger.Log($"[ManagedGamesManager] Plugin desinstalado de {game.Name}");
                }

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
