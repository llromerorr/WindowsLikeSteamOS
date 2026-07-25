using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SteamOSConfigurator.Helpers
{
    public static class MAHMSharedMemory
    {
        private static MemoryMappedFile? _mmf;
        private static MemoryMappedViewAccessor? _accessor;
        private static readonly Mutex _mutex = new Mutex();
        
        private static float _gpuTemp;
        private static float _gpuPower;
        private static float _gpuFanRPM;

        public static void IniciarMonitoreoBackground()
        {
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        LeerSensores();
                    }
                    catch { }
                    Thread.Sleep(1000);
                }
            })
            { IsBackground = true, Name = "MAHMSharedMemoryPoller" }.Start();
        }

        private static void LeerSensores()
        {
            _mutex.WaitOne();
            try
            {
                if (_mmf == null)
                {
                    try { _mmf = MemoryMappedFile.OpenExisting("MAHMSharedMemory", MemoryMappedFileRights.Read); }
                    catch { return; } // MSI Afterburner not running or not providing shared memory
                    
                    _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                }

                if (_accessor == null) return;

                uint signature = _accessor.ReadUInt32(0);
                if (signature != 0x4D48414D) // 'MAHM'
                {
                    _accessor.Dispose();
                    _mmf.Dispose();
                    _accessor = null;
                    _mmf = null;
                    return;
                }

                uint headerSize = _accessor.ReadUInt32(8);
                uint entryCount = _accessor.ReadUInt32(12);
                uint entrySize = _accessor.ReadUInt32(16);

                float newTemp = 0;
                float newPower = 0;
                float newFanSpeed = 0;
                float newFanTachometer = 0;

                byte[] nameBuffer = new byte[260];

                for (int i = 0; i < entryCount; i++)
                {
                    long offset = headerSize + (i * entrySize);
                    _accessor.ReadArray(offset, nameBuffer, 0, 260);
                    
                    string name = Encoding.ASCII.GetString(nameBuffer);
                    int nullIndex = name.IndexOf('\0');
                    if (nullIndex >= 0) name = name.Substring(0, nullIndex);

                    float data = _accessor.ReadSingle(offset + (260 * 5));
                    string lowerName = name.ToLower();

                    // El nombre en offset 0 (szSrcName) es SIEMPRE el nombre interno en inglés (ej. "GPU1 temperature").
                    // Usamos Contains para ignorar el número de la GPU (ej. GPU1, GPU2) y ser independientes del idioma de la UI.
                    if (lowerName.Contains("gpu") && lowerName.Contains("temperature")) newTemp = data;
                    else if (lowerName.Contains("gpu") && lowerName.Contains("power")) newPower = data;
                    else if (lowerName.Contains("fan speed")) newFanSpeed = data;
                    else if (lowerName.Contains("fan tachometer")) newFanTachometer = data;
                }

                _gpuTemp = newTemp;
                _gpuPower = newPower;
                _gpuFanRPM = newFanTachometer > 0 ? newFanTachometer : newFanSpeed;
                _isFanPercentage = (newFanTachometer == 0 && newFanSpeed > 0);

                uint gpuEntryCount = _accessor.ReadUInt32(24);
                uint gpuEntrySize = _accessor.ReadUInt32(28);
                long gpuOffset = headerSize + (entryCount * entrySize);
                
                if (gpuEntryCount > 0)
                {
                    byte[] deviceBuf = new byte[260];
                    byte[] driverBuf = new byte[260];

                    _accessor.ReadArray(gpuOffset + 520, deviceBuf, 0, 260); // szDevice
                    _accessor.ReadArray(gpuOffset + 780, driverBuf, 0, 260); // szDriver

                    string devName = Encoding.ASCII.GetString(deviceBuf);
                    int devNullIndex = devName.IndexOf('\0');
                    if (devNullIndex >= 0) devName = devName.Substring(0, devNullIndex);

                    string drvName = Encoding.ASCII.GetString(driverBuf);
                    int drvNullIndex = drvName.IndexOf('\0');
                    if (drvNullIndex >= 0) drvName = drvName.Substring(0, drvNullIndex);

                    if (!string.IsNullOrEmpty(devName)) _gpuName = devName;
                    if (!string.IsNullOrEmpty(drvName)) _gpuDriverVersion = drvName;
                }
            }
            catch
            {
                if (_accessor != null) { _accessor.Dispose(); _accessor = null; }
                if (_mmf != null) { _mmf.Dispose(); _mmf = null; }
            }
            finally
            {
                _mutex.ReleaseMutex();
            }
        }

        private static bool _isFanPercentage;
        private static string _gpuName = "GPU";
        private static string _gpuDriverVersion = "";

        public static float GetGpuTemp() => _gpuTemp;
        public static float GetGpuPower() => _gpuPower;
        public static float GetGpuFanRPM() => _gpuFanRPM;
        public static bool IsFanPercentage() => _isFanPercentage;
        public static string GetGpuName() => _gpuName;
        public static string GetGpuDriverVersion() => _gpuDriverVersion;
    }
}
