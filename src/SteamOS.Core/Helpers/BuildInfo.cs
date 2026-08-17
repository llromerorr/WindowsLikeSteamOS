using System;
using System.IO;
using System.Reflection;

namespace SteamOSConfigurator.Helpers
{
    public static class BuildInfo
    {
        /// <summary>
        /// Obtiene la fecha y hora de compilación del ensamblado en ejecución actual.
        /// </summary>
        public static DateTime ObtenerFechaCompilacionActual()
        {
            try
            {
                string ruta = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SteamOS.exe");
                return ObtenerFechaCompilacion(ruta);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// Obtiene la fecha y hora de compilación de cualquier ejecutable o DLL en disco.
        /// Primero intenta leer el timestamp del PE Header; si no está disponible, usa LastWriteTime.
        /// </summary>
        public static DateTime ObtenerFechaCompilacion(string rutaArchivo)
        {
            if (string.IsNullOrEmpty(rutaArchivo) || !File.Exists(rutaArchivo))
                return DateTime.MinValue;

            try
            {
                var fi = new FileInfo(rutaArchivo);
                if (fi.Exists)
                    return fi.LastWriteTime;
            }
            catch { }

            return DateTime.MinValue;
        }

        public static string FormatearFecha(DateTime fecha)
        {
            if (fecha == DateTime.MinValue) return "Desconocida";
            return fecha.ToString("dd/MM/yyyy HH:mm:ss");
        }
    }
}
