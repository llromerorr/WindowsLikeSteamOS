using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace SteamOSConfigurator.Helpers
{
    public class RendimientoRTSS
    {
        public float Fps { get; set; } = 0;
        public float FrametimeMs { get; set; } = 0;
        public uint FpsMin { get; set; } = 0;
        public uint FpsAvg { get; set; } = 0;
        public uint FpsMax { get; set; } = 0;
        public string Resolution { get; set; } = string.Empty;
        public string GameName { get; set; } = string.Empty;
        public string ProcessPath { get; set; } = string.Empty;
        public bool DatosValidos { get; set; } = false;
    }

    public static class RTSSSharedMemory
    {
        private const string MapName = "RTSSSharedMemoryV2";
        
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        public static RendimientoRTSS ObtenerRendimientoJuegoActual()
        {
            var resultado = new RendimientoRTSS();

            try
            {
                IntPtr fgWindow = GetForegroundWindow();
                uint fgPid = 0;
                if (fgWindow != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(fgWindow, out fgPid);
                    if (GetClientRect(fgWindow, out RECT rect))
                    {
                        int width = rect.Right - rect.Left;
                        int height = rect.Bottom - rect.Top;
                        if (width > 0 && height > 0)
                        {
                            resultado.Resolution = $"{width}x{height}";
                        }
                    }
                }

                using (var mmf = MemoryMappedFile.OpenExisting(MapName))
                using (var accessor = mmf.CreateViewAccessor())
                {
                    uint signature = accessor.ReadUInt32(0);
                    if (signature != 0x53535452 && signature != 0x52545353) return resultado; // 'RTSS'

                    uint appArrOffset = accessor.ReadUInt32(12);
                    uint appArrSize = accessor.ReadUInt32(16);
                    uint appEntrySize = accessor.ReadUInt32(8);

                    RendimientoRTSS mejorCandidato = null;

                    for (uint i = 0; i < appArrSize; i++)
                    {
                        uint entryOffset = appArrOffset + (i * appEntrySize);
                        uint processId = accessor.ReadUInt32(entryOffset);
                        
                        if (processId > 0)
                        {
                            // Leer nombre del juego y limpiar la ruta completa
                            byte[] nameBytes = new byte[260];
                            accessor.ReadArray(entryOffset + 4, nameBytes, 0, 260);
                            string rawName = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                            string gameName = System.IO.Path.GetFileNameWithoutExtension(rawName);

                            // Leer campos de tiempo y frames para formula oficial
                            uint time0 = accessor.ReadUInt32(entryOffset + 268);
                            uint time1 = accessor.ReadUInt32(entryOffset + 272);
                            uint frames = accessor.ReadUInt32(entryOffset + 276);
                            uint rawFrametime = accessor.ReadUInt32(entryOffset + 280);

                            // Leer stats si la estructura es suficientemente grande
                            uint fpsMin = 0, fpsAvg = 0, fpsMax = 0;
                            if (appEntrySize >= 316)
                            {
                                fpsMin = accessor.ReadUInt32(entryOffset + 304);
                                fpsAvg = accessor.ReadUInt32(entryOffset + 308);
                                fpsMax = accessor.ReadUInt32(entryOffset + 312);
                            }

                            float calculatedFps = 0;
                            if (time1 > time0)
                            {
                                calculatedFps = 1000.0f * frames / (time1 - time0);
                            }
                            else if (rawFrametime > 0 && rawFrametime < 1000000)
                            {
                                // Fallback a calculo por frametime
                                calculatedFps = 1000000f / rawFrametime;
                            }

                            float calculatedFrametimeMs = rawFrametime / 1000f;

                            if (calculatedFps > 0 && calculatedFps < 2000)
                            {
                                var cand = new RendimientoRTSS
                                {
                                    Fps = calculatedFps,
                                    FrametimeMs = calculatedFrametimeMs,
                                    FpsMin = fpsMin,
                                    FpsAvg = fpsAvg,
                                    FpsMax = fpsMax,
                                    Resolution = resultado.Resolution,
                                    GameName = gameName,
                                    ProcessPath = rawName,
                                    DatosValidos = true
                                };

                                // Prioridad absoluta: proceso en primer plano
                                if (fgPid > 0 && processId == fgPid)
                                {
                                    return cand;
                                }

                                // Si no, guardamos el que tenga mas FPS como candidato
                                if (mejorCandidato == null || cand.Fps > mejorCandidato.Fps)
                                {
                                    mejorCandidato = cand;
                                }
                            }
                        }
                    }

                    if (mejorCandidato != null)
                    {
                        return mejorCandidato;
                    }
                }
            }
            catch { }
            
            return resultado;
        }

        public static void SetFramerateLimit(uint limit)
        {
            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting(MapName))
                using (var accessor = mmf.CreateViewAccessor())
                {
                    uint signature = accessor.ReadUInt32(0);
                    if (signature != 0x53535452 && signature != 0x52545353) return; // 'RTSS'

                    uint appArrOffset = accessor.ReadUInt32(12);
                    uint appArrSize = accessor.ReadUInt32(16);
                    uint appEntrySize = accessor.ReadUInt32(8);

                    for (uint i = 0; i < appArrSize; i++)
                    {
                        uint entryOffset = appArrOffset + (i * appEntrySize);
                        uint processId = accessor.ReadUInt32(entryOffset);
                        
                        if (processId > 0)
                        {
                            accessor.Write(entryOffset + 888, limit);
                        }
                    }
                }
            }
            catch { }
        }

        public static void UpdateOSD(string text)
        {
            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting(MapName))
                using (var accessor = mmf.CreateViewAccessor())
                {
                    uint signature = accessor.ReadUInt32(0);
                    if (signature != 0x53535452 && signature != 0x52545353) return; // 'RTSS'

                    uint osdEntrySize = accessor.ReadUInt32(20);
                    uint osdArrOffset = accessor.ReadUInt32(24);
                    uint osdArrSize = accessor.ReadUInt32(28);

                    if (osdArrSize == 0 || osdEntrySize == 0) return;

                    uint slot = osdArrSize > 1 ? 1u : 0u;
                    uint entryOffset = osdArrOffset + (slot * osdEntrySize);

                    byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
                    int maxLen = osdEntrySize >= 8192 ? 4096 : 256;
                    int lenToWrite = Math.Min(textBytes.Length, maxLen);
                    accessor.WriteArray(entryOffset, textBytes, 0, lenToWrite);

                    byte[] ownerBytes = System.Text.Encoding.ASCII.GetBytes("WLSteamOS\0");
                    uint ownerOffset = osdEntrySize >= 8192 ? entryOffset + 4096 : entryOffset + 256;
                    accessor.WriteArray(ownerOffset, ownerBytes, 0, Math.Min(ownerBytes.Length, 64));
                }
            }
            catch { }
        }
    }
}
