using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using Microsoft.Win32;

namespace WindowsLikeSteamOS.Services
{
    public class PrerequisiteResult
    {
        public bool SteamInstalled { get; set; }
        public bool AfterburnerInstalled { get; set; }
        public bool RtssInstalled { get; set; }
        public List<string> MissingList { get; set; } = new List<string>();
        public bool IsValid => MissingList.Count == 0;
    }

    public class PrerequisiteService
    {
        public static PrerequisiteResult ValidarRequisitos()
        {
            var result = new PrerequisiteResult();

            // 1. Steam
            result.SteamInstalled = EstaInstaladoPorRegistro(@"SOFTWARE\Valve\Steam") ||
                                    File.Exists(@"C:\Program Files (x86)\Steam\steam.exe");
            if (!result.SteamInstalled) result.MissingList.Add("Steam");

            // 2. MSI Afterburner
            result.AfterburnerInstalled = File.Exists(@"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe") ||
                                          File.Exists(@"C:\Program Files\MSI Afterburner\MSIAfterburner.exe");
            if (!result.AfterburnerInstalled) result.MissingList.Add("MSI Afterburner");

            // 3. RTSS (RivaTuner Statistics Server)
            result.RtssInstalled = File.Exists(@"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe") ||
                                   File.Exists(@"C:\Program Files\RivaTuner Statistics Server\RTSS.exe");
            if (!result.RtssInstalled) result.MissingList.Add("RTSS (RivaTuner Statistics Server)");

            return result;
        }

        public static void InstalarConWinget(string packageId)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = $"install --id {packageId} --silent --accept-package-agreements --accept-source-agreements",
                    UseShellExecute = true,
                    CreateNoWindow = false
                });
            }
            catch { }
        }

        private static bool EstaInstaladoPorRegistro(string keyPath)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                return key != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
