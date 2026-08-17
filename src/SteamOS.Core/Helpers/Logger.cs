using System;
using System.IO;

namespace SteamOSConfigurator
{
    public static class Logger
    {
        public static bool HabilitarDebug = true; 
        private static readonly string RutaLog = Helpers.AppPaths.LogFile;

        public static void Log(string mensaje) 
        { 
            if (!HabilitarDebug) return; 
            try 
            { 
                string dir = Path.GetDirectoryName(RutaLog) ?? "";
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string linea = $"[{DateTime.Now:HH:mm:ss.fff}] {mensaje}\n"; 
                File.AppendAllText(RutaLog, linea); 
            } 
            catch { } 
        }
    }
}
