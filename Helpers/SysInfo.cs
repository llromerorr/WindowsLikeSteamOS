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
        private static float _gpuTemp;
        private static float _gpuLoad;
        private static float _netSpeed;

        static SysInfo()
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsNetworkEnabled = true,
                IsStorageEnabled = false
            };
            try { _computer.Open(); } catch { }
            _updateVisitor = new UpdateVisitor();

            try { _diskReadCounter = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", "_Total"); } catch { }
            try { _diskWriteCounter = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", "_Total"); } catch { }

            Task.Run(() => HardwarePollLoop());
        }

        private static void HardwarePollLoop()
        {
            while (true)
            {
                try
                {
                    _computer.Accept(_updateVisitor);

                    // CPU Temp
                    var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                    var tCpu = cpu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && (s.Name.Contains("Package") || s.Name.Contains("Max") || s.Name.Contains("Core")));
                    if (tCpu != null) _cpuTemp = tCpu.Value ?? 0f;

                    // GPU
                    var gpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia || h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuIntel);
                    var tGpu = gpu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && (s.Name.Contains("Core") || s.Name.Contains("GPU")));
                    if (tGpu != null) _gpuTemp = tGpu.Value ?? 0f;

                    var lGpu = gpu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && (s.Name.Contains("Core") || s.Name.Contains("GPU")));
                    if (lGpu != null) _gpuLoad = lGpu.Value ?? 0f;

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
        public static float GetGpuTemp() => _gpuTemp;
        public static float GetGpuLoad() => _gpuLoad;
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX
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

            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

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
            var mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem)) return mem.dwMemoryLoad;
            return 0;
        }

        public static (bool IsCharging, int BatteryPercent) GetBatteryStatus()
        {
            var power = new SYSTEM_POWER_STATUS();
            if (GetSystemPowerStatus(power)) return (power.ACLineStatus == 1, power.BatteryLifePercent);
            return (true, 100);
        }
    }
}
