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
                // Intento 1: Leer el Linker Timestamp del PE Header (32-bit Unix timestamp en el header COFF)
                using (var stream = new FileStream(rutaArchivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length >= 64)
                    {
                        stream.Seek(0x3C, SeekOrigin.Begin); // Offset to PE Header pointer
                        int peHeaderOffset = reader.ReadInt32();

                        if (peHeaderOffset > 0 && stream.Length >= peHeaderOffset + 24)
                        {
                            stream.Seek(peHeaderOffset, SeekOrigin.Begin);
                            uint peSignature = reader.ReadUInt32(); // "PE\0\0" = 0x00004550

                            if (peSignature == 0x00004550)
                            {
                                stream.Seek(peHeaderOffset + 4 + 4, SeekOrigin.Begin); // Skip Machine, read TimeDateStamp
                                uint secondsSince1970 = reader.ReadUInt32();

                                if (secondsSince1970 > 0 && secondsSince1970 < 0xFFFFFFFF)
                                {
                                    var dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secondsSince1970);
                                    // Si el timestamp es razonable (entre año 2020 y 2040), lo usamos
                                    if (dt.Year >= 2020 && dt.Year <= 2040)
                                        return dt.ToLocalTime();
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // Intento 2: FileInfo.LastWriteTime
            try
            {
                var fi = new FileInfo(rutaArchivo);
                return fi.LastWriteTime;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        public static string FormatearFecha(DateTime fecha)
        {
            if (fecha == DateTime.MinValue) return "Desconocida";
            return fecha.ToString("dd/MM/yyyy HH:mm:ss");
        }
    }
}
