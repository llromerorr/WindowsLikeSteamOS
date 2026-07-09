using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Win32;
using SteamOSConfigurator.Helpers;

namespace SteamOSConfigurator.Services
{
    public interface IDependencyService
    {
        bool SteamInstalado { get; }
        bool RtssInstalado { get; }
        bool ViGEmInstalado { get; }
        bool HidHideInstalado { get; }
        Task<bool> InstalarSteamAsync(Action<string>? onProgreso = null);
        Task<bool> InstalarRtssAsync(Action<string>? onProgreso = null);
    }

    public class DependencyService : IDependencyService
    {
        private const string STEAM_CDN_URL = "https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe";
        private const string WINGET_STEAM_ID = "Valve.Steam";
        private const string WINGET_RTSS_ID = "RivaTuner.RTSS";

        public bool SteamInstalado
        {
            get
            {
                try
                {
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(AppPaths.SteamRegistryKey);
                    if (key != null)
                    {
                        string? path = key.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "steam.exe")))
                            return true;
                    }
                }
                catch { }
                return File.Exists(AppPaths.SteamFallback);
            }
        }

        public bool RtssInstalado => File.Exists(AppPaths.RutaExeRTSS);
        public bool ViGEmInstalado => File.Exists(AppPaths.DriverViGEm);
        public bool HidHideInstalado => File.Exists(AppPaths.DriverHidHide);

        public async Task<bool> InstalarSteamAsync(Action<string>? onProgreso = null)
        {
            // Intentar winget primero
            if (await IntentarWingetAsync(WINGET_STEAM_ID, onProgreso))
                return true;

            // Fallback: descargar desde CDN oficial
            onProgreso?.Invoke("Descargando Steam desde el servidor oficial...");
            Logger.Log("winget no disponible. Descargando Steam desde CDN...");

            try
            {
                string rutaTemp = Path.Combine(Path.GetTempPath(), "SteamSetup.exe");
                using (var http = new HttpClient())
                {
                    var bytes = await http.GetByteArrayAsync(STEAM_CDN_URL);
                    await File.WriteAllBytesAsync(rutaTemp, bytes);
                }

                onProgreso?.Invoke("Instalando Steam...");
                var psi = new ProcessStartInfo(rutaTemp, "/S")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var proc = Process.Start(psi);
                if (proc != null) await proc.WaitForExitAsync();

                // Limpiar instalador temporal
                try { File.Delete(rutaTemp); } catch { }

                bool exito = SteamInstalado;
                Logger.Log(exito ? "Steam instalado correctamente desde CDN." : "Error: Steam no se detecta tras instalación.");
                return exito;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error al descargar/instalar Steam: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> InstalarRtssAsync(Action<string>? onProgreso = null)
        {
            if (await IntentarWingetAsync(WINGET_RTSS_ID, onProgreso))
                return true;

            // RTSS no tiene URL de CDN estable para fallback
            Logger.Log("No se pudo instalar RTSS. winget no disponible.");
            onProgreso?.Invoke("No se pudo instalar RTSS. Instala winget o descárgalo manualmente.");
            return false;
        }

        private async Task<bool> IntentarWingetAsync(string packageId, Action<string>? onProgreso)
        {
            try
            {
                // Verificar que winget existe
                string? wingetPath = BuscarWinget();
                if (wingetPath == null)
                {
                    Logger.Log("winget no encontrado en el sistema.");
                    return false;
                }

                onProgreso?.Invoke($"Instalando {packageId} con winget...");
                Logger.Log($"Intentando instalar {packageId} con winget...");

                var psi = new ProcessStartInfo(wingetPath, $"install --id {packageId} --silent --accept-package-agreements --accept-source-agreements")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var proc = Process.Start(psi);
                if (proc == null) return false;

                await proc.WaitForExitAsync();
                bool exito = proc.ExitCode == 0;
                Logger.Log(exito ? $"{packageId} instalado via winget." : $"winget devolvió código {proc.ExitCode} para {packageId}.");
                return exito;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error al usar winget para {packageId}: {ex.Message}");
                return false;
            }
        }

        private string? BuscarWinget()
        {
            // winget suele estar en el PATH del sistema
            try
            {
                var psi = new ProcessStartInfo("where", "winget")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                if (proc == null) return null;
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    return output.Split('\n')[0].Trim();
            }
            catch { }

            // Fallback: ruta conocida
            string known = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WindowsApps\winget.exe");
            if (File.Exists(known)) return known;

            return null;
        }
    }
}
