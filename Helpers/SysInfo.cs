using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace SteamOSConfigurator.Helpers
{
    public class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) { computer.Traverse(this); }
        public void VisitHardware(IHardware hardware) { hardware.Update(); foreach (IHardware subHardware in hardware.SubHardware) subHardware.Accept(this); }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    public static class SysInfo
    {
        private static readonly Computer _computer;
        private static readonly UpdateVisitor _updateVisitor;
        private static PerformanceCounter? _diskReadCounter;
        private static PerformanceCounter? _diskWriteCounter;
        
        private static float _cpuTemp;
        private static float _cpuLoad;
        private static float _cpuClock;
        private static float _cpuPower;

        private static float _gpuTemp;
        private static float _gpuLoad;
        private static float _gpuClock;
        private static float _gpuVramClock;
        private static float _gpuPower;
        private static float _gpuVramUsedMb;
        private static float _gpuVramTotalMb;
        
        private static float _ramUsedGb;
        private static float _ramTotalGb;

        private static float _netSpeed;
        private static float _cpuFanRPM;
        private static float _gpuFanRPM;
        private static bool _gpuFanIsPercent;
        private static string _gpuName = "";

        static SysInfo()
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = false
            };
            _updateVisitor = new UpdateVisitor();

            // Abrir computadora asíncronamente para no congelar la UI mientras escanea I/O chips
            Task.Run(() =>
            {
                try { _computer.Open(); } catch { }
            });

            try { _diskReadCounter = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", "_Total"); } catch { }
            try { _diskWriteCounter = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", "_Total"); } catch { }

            MAHMSharedMemory.IniciarMonitoreoBackground();
            Task.Run(() => HardwarePollLoop());
        }

        private static void HardwarePollLoop()
        {
            while (true)
            {
                try
                {
                    _computer.Accept(_updateVisitor);

                    // CPU
                    var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                    if (cpu != null)
                    {
                        var tCpu = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && (s.Name.Contains("Package") || s.Name.Contains("Max") || s.Name.Contains("Core")));
                        if (tCpu != null) _cpuTemp = tCpu.Value ?? 0f;

                        var lCpu = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Total"));
                        if (lCpu != null) _cpuLoad = lCpu.Value ?? 0f;

                        var cCpu = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && (s.Name.Contains("Core #1") || s.Name.Contains("Core 1")));
                        if (cCpu != null) _cpuClock = cCpu.Value ?? 0f;

                        var pCpu = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name.Contains("Package"));
                        if (pCpu != null) _cpuPower = pCpu.Value ?? 0f;
                    }

                    // VENTILADORES (CPU, GPU, Motherboard, SuperIO, Cooler & Controllers)
                    float bestCpuFan = 0;
                    float bestGpuFan = 0;

                    foreach (var hw in _computer.Hardware)
                    {
                        try
                        {
                            hw.Update();
                            foreach (var sensor in hw.Sensors)
                            {
                                if (sensor.SensorType == SensorType.Fan && sensor.Value.HasValue && sensor.Value.Value > 0)
                                {
                                    string name = sensor.Name.ToLower();
                                    if (name.Contains("gpu") || name.Contains("graphics"))
                                        bestGpuFan = Math.Max(bestGpuFan, sensor.Value.Value);
                                    else if (name.Contains("cpu") || name.Contains("fan 1") || name.Contains("fan1"))
                                        bestCpuFan = Math.Max(bestCpuFan, sensor.Value.Value);
                                    else if (bestCpuFan == 0)
                                        bestCpuFan = sensor.Value.Value;
                                    else if (bestGpuFan == 0)
                                        bestGpuFan = sensor.Value.Value;
                                }
                            }

                            foreach (var sub in hw.SubHardware)
                            {
                                sub.Update();
                                foreach (var sensor in sub.Sensors)
                                {
                                    if (sensor.SensorType == SensorType.Fan && sensor.Value.HasValue && sensor.Value.Value > 0)
                                    {
                                        string name = sensor.Name.ToLower();
                                        if (name.Contains("gpu") || name.Contains("graphics"))
                                            bestGpuFan = Math.Max(bestGpuFan, sensor.Value.Value);
                                        else if (name.Contains("cpu") || name.Contains("fan 1") || name.Contains("fan1"))
                                            bestCpuFan = Math.Max(bestCpuFan, sensor.Value.Value);
                                        else if (bestCpuFan == 0)
                                            bestCpuFan = sensor.Value.Value;
                                        else if (bestGpuFan == 0)
                                            bestGpuFan = sensor.Value.Value;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (bestCpuFan > 0) _cpuFanRPM = bestCpuFan;
                    if (bestGpuFan > 0) _gpuFanRPM = bestGpuFan;
                    
                    // GPU
                    var gpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia || h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuIntel);
                    if (gpu != null)
                    {
                        if (!string.IsNullOrEmpty(gpu.Name)) _gpuName = gpu.Name;
                        var tempGpu = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && (s.Name.Contains("Core") || s.Name.Contains("GPU")));
                        if (tempGpu != null) _gpuTemp = tempGpu.Value ?? 0f;

                        var loadGpu = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && (s.Name.Contains("Core") || s.Name.Contains("GPU")));
                        if (loadGpu != null) _gpuLoad = loadGpu.Value ?? 0f;

                        var clockGpu = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && (s.Name.Contains("Core") || s.Name.Contains("GPU")));
                        if (clockGpu != null) _gpuClock = clockGpu.Value ?? 0f;

                        var vramClock = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && (s.Name.Contains("Memory") || s.Name.Contains("VRAM")));
                        if (vramClock != null) _gpuVramClock = vramClock.Value ?? 0f;

                        var powerGpu = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && (s.Name.ToLower().Contains("gpu") || s.Name.ToLower().Contains("board") || s.Name.ToLower().Contains("package") || s.Name.ToLower().Contains("core") || s.Name.ToLower().Contains("total"))) 
                                    ?? gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power);
                        if (powerGpu != null && powerGpu.Value.HasValue && powerGpu.Value.Value > 0)
                        {
                            _gpuPower = powerGpu.Value.Value;
                        }

                        // Buscar en SubHardware de la GPU si no se encontró en primer nivel
                        if (_gpuPower == 0)
                        {
                            foreach (var sub in gpu.SubHardware)
                            {
                                sub.Update();
                                var subPower = sub.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Value.HasValue && s.Value.Value > 0);
                                if (subPower != null && subPower.Value.HasValue)
                                {
                                    _gpuPower = subPower.Value.Value;
                                    break;
                                }
                            }
                        }

                        // Fallback para iGPU / APU (donde el consumo GPU está integrado en el Package Power de la CPU)
                        if (_gpuPower == 0 && _cpuPower > 0 && _gpuLoad > 5)
                        {
                            _gpuPower = (_gpuLoad / 100f) * _cpuPower;
                        }

                        var memUsed = gpu.Sensors.FirstOrDefault(s => (s.SensorType == SensorType.SmallData || s.SensorType == SensorType.Data) && (s.Name.Contains("Memory Used") || s.Name.Contains("GPU Memory Used") || s.Name.Contains("VRAM Used")));
                        if (memUsed != null) _gpuVramUsedMb = memUsed.Value ?? 0f;

                        var memTotal = gpu.Sensors.FirstOrDefault(s => (s.SensorType == SensorType.SmallData || s.SensorType == SensorType.Data) && (s.Name.Contains("Memory Total") || s.Name.Contains("GPU Memory Total") || s.Name.Contains("VRAM Total")));
                        if (memTotal != null) _gpuVramTotalMb = memTotal.Value ?? 0f;

                        var fGpuFan = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
                        var fGpuCtrl = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control && s.Name.ToLower().Contains("fan"));

                        if (fGpuFan != null && fGpuFan.Value.HasValue && fGpuFan.Value.Value > 0)
                        {
                            _gpuFanRPM = fGpuFan.Value.Value;
                            _gpuFanIsPercent = false;
                        }
                        else if (fGpuCtrl != null && fGpuCtrl.Value.HasValue && fGpuCtrl.Value.Value >= 0)
                        {
                            _gpuFanRPM = fGpuCtrl.Value.Value;
                            _gpuFanIsPercent = true;
                        }
                        else if (bestGpuFan > 0)
                        {
                            _gpuFanRPM = bestGpuFan;
                        }
                    }

                    // RAM (Uso exacto por API de Windows GlobalMemoryStatusEx)
                    var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                    if (GlobalMemoryStatusEx(ref memStatus))
                    {
                        _ramTotalGb = memStatus.ullTotalPhys / (1024f * 1024f * 1024f);
                        _ramUsedGb = (memStatus.ullTotalPhys - memStatus.ullAvailPhys) / (1024f * 1024f * 1024f);
                    }

                    // Network
                    float net = 0;
                    foreach (var n in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Network))
                    {
                        var up = n.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name.Contains("Upload"));
                        var down = n.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name.Contains("Download"));
                        net += (up?.Value ?? 0f) + (down?.Value ?? 0f);
                    }
                    _netSpeed = net / 1024f; // KB/s
                }
                catch { }

                Thread.Sleep(1000);
            }
        }

        public static float GetCpuTemp() => _cpuTemp;
        public static float GetCpuLoad() => _cpuLoad;
        public static float GetCpuClock() => _cpuClock;
        public static float GetCpuPower() => _cpuPower;
        public static float GetCpuFanRPM() => _cpuFanRPM;

        public static float GetGpuTemp() 
        {
            float msiTemp = MAHMSharedMemory.GetGpuTemp();
            return msiTemp > 0 ? msiTemp : _gpuTemp;
        }

        public static float GetGpuLoad() => _gpuLoad;
        public static float GetGpuClock() => _gpuClock;
        public static float GetGpuVramClock() => _gpuVramClock;

        public static float GetGpuPower()
        {
            float msiPower = MAHMSharedMemory.GetGpuPower();
            return msiPower > 0 ? msiPower : _gpuPower;
        }

        public static float GetGpuVramUsedMb() => _gpuVramUsedMb;
        public static float GetGpuVramTotalMb() => _gpuVramTotalMb;

        public static float GetGpuFanRPM()
        {
            float msiFan = MAHMSharedMemory.GetGpuFanRPM();
            return msiFan > 0 ? msiFan : _gpuFanRPM;
        }

        public static bool IsGpuFanPercentage()
        {
            if (MAHMSharedMemory.IsFanPercentage()) return true;
            return _gpuFanIsPercent;
        }

        public static string GetGpuName()
        {
            string msiGpu = MAHMSharedMemory.GetGpuName();
            if (!string.IsNullOrEmpty(msiGpu) && msiGpu != "GPU") return msiGpu;
            return _gpuName;
        }

        public static string GetGpuDriverVersion()
        {
            return MAHMSharedMemory.GetGpuDriverVersion();
        }

        public static float GetRamUsedGb() => _ramUsedGb;
        public static float GetRamTotalGb() => _ramTotalGb;

        public static float GetNetworkSpeedKbps() => _netSpeed;

        public static float GetDiskReadWriteMBps()
        {
            try
            {
                float r = _diskReadCounter?.NextValue() ?? 0f;
                float w = _diskWriteCounter?.NextValue() ?? 0f;
                return (r + w) / (1024f * 1024f); // MB/s
            }
            catch { return 0f; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public class SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemPowerStatus([In, Out] SYSTEM_POWER_STATUS systemPowerStatus);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
            public ulong ToULong() { return ((ulong)dwHighDateTime << 32) | dwLowDateTime; }
        }

        private static ulong _prevIdleTime;
        private static ulong _prevKernelTime;
        private static ulong _prevUserTime;

        public static double GetCpuUsage()
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                ulong idleTime = idle.ToULong();
                ulong kernelTime = kernel.ToULong();
                ulong userTime = user.ToULong();

                if (_prevIdleTime != 0)
                {
                    ulong idleDiff = idleTime - _prevIdleTime;
                    ulong kernelDiff = kernelTime - _prevKernelTime;
                    ulong userDiff = userTime - _prevUserTime;
                    ulong sysDiff = kernelDiff + userDiff;

                    if (sysDiff > 0)
                    {
                        double cpu = ((sysDiff - idleDiff) * 100.0) / sysDiff;
                        if (cpu < 0) cpu = 0;
                        if (cpu > 100) cpu = 100;
                        _prevIdleTime = idleTime;
                        _prevKernelTime = kernelTime;
                        _prevUserTime = userTime;
                        return cpu;
                    }
                }
                
                _prevIdleTime = idleTime;
                _prevKernelTime = kernelTime;
                _prevUserTime = userTime;
            }
            return 0;
        }

        public static double GetRamUsage()
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem)) return mem.dwMemoryLoad;
            return 0;
        }

        public static (bool IsCharging, int BatteryPercent) GetBatteryStatus()
        {
            var power = new SYSTEM_POWER_STATUS();
            if (GetSystemPowerStatus(power))
            {
                if (power.BatteryLifePercent == 255 || power.BatteryFlag == 128)
                {
                    return (true, -1); // Sin batería (PC de Escritorio / Mini PC)
                }
                return (power.ACLineStatus == 1, power.BatteryLifePercent);
            }
            return (true, -1);
        }
    }
}
