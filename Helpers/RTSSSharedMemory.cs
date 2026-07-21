using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace SteamOSConfigurator.Helpers
{
    public static class RTSSSharedMemory
    {
        private const string MapName = "RTSSSharedMemoryV2";
        
        public static void SetFramerateLimit(uint limit)
        {
            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting(MapName))
                using (var accessor = mmf.CreateViewAccessor())
                {
                    uint signature = accessor.ReadUInt32(0);
                    if (signature != 0x53535452) return; // 'RTSS'

                    uint appArrOffset = accessor.ReadUInt32(12);
                    uint appArrSize = accessor.ReadUInt32(16);
                    uint appEntrySize = accessor.ReadUInt32(8);

                    for (uint i = 0; i < appArrSize; i++)
                    {
                        uint entryOffset = appArrOffset + (i * appEntrySize);
                        uint processId = accessor.ReadUInt32(entryOffset);
                        
                        if (processId > 0)
                        {
                            // dwFramerateLimit offset is 888 inside RTSS_SHARED_MEMORY_APP_ENTRY
                            accessor.Write(entryOffset + 888, limit);
                        }
                    }
                    Logger.Log($"[RTSSSharedMemory] Limite de FPS fijado dinámicamente a {limit}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[RTSSSharedMemory] Error: {ex.Message}");
            }
        }

        public static void UpdateOSD(string text)
        {
            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting(MapName))
                using (var accessor = mmf.CreateViewAccessor())
                {
                    uint signature = accessor.ReadUInt32(0);
                    if (signature != 0x53535452) return; // 'RTSS'

                    uint osdEntrySize = accessor.ReadUInt32(20);
                    uint osdArrOffset = accessor.ReadUInt32(24);
                    uint osdArrSize = accessor.ReadUInt32(28);

                    if (osdArrSize == 0 || osdEntrySize == 0) return;

                    // Slot 1 (reserved for custom plugins/apps)
                    uint slot = osdArrSize > 1 ? 1u : 0u;
                    uint entryOffset = osdArrOffset + (slot * osdEntrySize);

                    // Escribir texto szOSD
                    byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
                    int maxLen = osdEntrySize >= 8192 ? 4096 : 256;
                    int lenToWrite = Math.Min(textBytes.Length, maxLen);
                    accessor.WriteArray(entryOffset, textBytes, 0, lenToWrite);

                    // Escribir identificador szOSDOwner
                    byte[] ownerBytes = System.Text.Encoding.ASCII.GetBytes("WLSteamOS\0");
                    uint ownerOffset = osdEntrySize >= 8192 ? entryOffset + 4096 : entryOffset + 256;
                    accessor.WriteArray(ownerOffset, ownerBytes, 0, Math.Min(ownerBytes.Length, 64));
                }
            }
            catch { }
        }
    }
}
