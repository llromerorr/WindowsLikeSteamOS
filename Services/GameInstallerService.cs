using System;
using System.IO;

namespace WindowsLikeSteamOS.Services
{
    public class GameInstallerService
    {
        private const string ASSETS_DIR = @"C:\ProgramData\SteamOS\Assets\ShaderPacks\";

        public static bool InstalarEnJuego(string gameExePath, out string errorMsg)
        {
            errorMsg = string.Empty;
            try
            {
                if (string.IsNullOrEmpty(gameExePath) || !File.Exists(gameExePath))
                {
                    errorMsg = "El ejecutable del juego no existe.";
                    return false;
                }

                string gameDir = Path.GetDirectoryName(gameExePath)!;

                // 1. BACKUP AUTOMÁTICO
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string backupDir = Path.Combine(gameDir, "wlsos_backup", timestamp);
                Directory.CreateDirectory(backupDir);

                string[] targetFiles = { "dxgi.dll", "ReShade.ini", "ReShadePreset.ini" };
                foreach (var f in targetFiles)
                {
                    string src = Path.Combine(gameDir, f);
                    if (File.Exists(src))
                    {
                        File.Copy(src, Path.Combine(backupDir, f), true);
                    }
                }

                string[] targetDirs = { "reshade-addons", "reshade-shaders" };
                foreach (var d in targetDirs)
                {
                    string src = Path.Combine(gameDir, d);
                    if (Directory.Exists(src))
                    {
                        CopiarDirectorio(src, Path.Combine(backupDir, d));
                    }
                }

                // 2. COPIA DE COMPONENTES WLSOS (ReShade NoUI + WLSOS Addon + Shaders)
                string programData = @"C:\ProgramData\SteamOS\";
                
                // dxgi.dll
                string reshadeDllSrc = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "files", "ReShade64.dll");
                if (!File.Exists(reshadeDllSrc)) reshadeDllSrc = Path.Combine(programData, "files", "ReShade64.dll");
                
                if (File.Exists(reshadeDllSrc))
                {
                    File.Copy(reshadeDllSrc, Path.Combine(gameDir, "dxgi.dll"), true);
                }

                // ReShade_NoUI.ini -> ReShade.ini
                string noUiSrc = Path.Combine(ASSETS_DIR, "templates", "ReShade_NoUI.ini");
                if (File.Exists(noUiSrc))
                {
                    File.Copy(noUiSrc, Path.Combine(gameDir, "ReShade.ini"), true);
                }

                // ReShadePreset_WLSOS.ini -> ReShadePreset.ini
                string presetSrc = Path.Combine(ASSETS_DIR, "templates", "ReShadePreset_WLSOS.ini");
                if (File.Exists(presetSrc))
                {
                    File.Copy(presetSrc, Path.Combine(gameDir, "ReShadePreset.ini"), true);
                }

                // WLSOS.addon
                string addonSrc = Path.Combine(programData, "reshade-addons", "WLSOS.addon");
                string addonDestDir = Path.Combine(gameDir, "reshade-addons");
                Directory.CreateDirectory(addonDestDir);
                if (File.Exists(addonSrc))
                {
                    File.Copy(addonSrc, Path.Combine(addonDestDir, "WLSOS.addon"), true);
                }

                // reshade-shaders
                string shadersSrc = Path.Combine(ASSETS_DIR, "packs");
                string shadersDest = Path.Combine(gameDir, "reshade-shaders");
                if (Directory.Exists(shadersSrc))
                {
                    CopiarDirectorio(shadersSrc, shadersDest);
                }

                // 3. VERIFICACIÓN POST-INSTALACIÓN
                bool valid = File.Exists(Path.Combine(gameDir, "dxgi.dll")) &&
                             File.Exists(Path.Combine(gameDir, "ReShade.ini")) &&
                             File.Exists(Path.Combine(gameDir, "reshade-addons", "WLSOS.addon"));

                if (!valid)
                {
                    errorMsg = "La verificación post-instalación falló. Algunos archivos críticos no pudieron copiarse.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return false;
            }
        }

        private static void CopiarDirectorio(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string dest = Path.Combine(destinationDir, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                string dest = Path.Combine(destinationDir, Path.GetFileName(subDir));
                CopiarDirectorio(subDir, dest);
            }
        }
    }
}
