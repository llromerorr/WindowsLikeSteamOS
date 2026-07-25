using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SteamOSConfigurator.Helpers;

namespace SteamOSConfigurator
{
    public partial class VentanaHUD : Window
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        public static VentanaHUD? Instancia { get; private set; }
        private readonly DispatcherTimer _timer;
        private readonly Queue<float> _fpsHistory = new Queue<float>();

        public VentanaHUD()
        {
            InitializeComponent();
            Instancia = this;

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += Timer_Tick;

            SourceInitialized += VentanaHUD_SourceInitialized;
        }

        private void VentanaHUD_SourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer?.Stop();
            if (Instancia == this) Instancia = null;
            base.OnClosed(e);
        }

        public void ActualizarNivelOSD(int nivel)
        {
            Dispatcher.Invoke(() =>
            {
                if (nivel <= 0)
                {
                    _timer.Stop();
                    Opacity = 0;
                    return;
                }

                Opacity = 1;
                
                // Configuración dinámica estricta de 4 niveles OSD
                FilaFPS.Visibility = nivel >= 1 ? Visibility.Visible : Visibility.Collapsed;
                FilaGrafica.Visibility = nivel >= 2 ? Visibility.Visible : Visibility.Collapsed;
                
                FilaGPU.Visibility = nivel >= 3 ? Visibility.Visible : Visibility.Collapsed;
                FilaCPU.Visibility = nivel >= 3 ? Visibility.Visible : Visibility.Collapsed;

                ActualizarDatos();
                if (!_timer.IsEnabled) _timer.Start();
            });
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            ActualizarDatos();
        }

        private void ActualizarDatos()
        {
            try
            {
                var rendimiento = RTSSSharedMemory.ObtenerRendimientoJuegoActual();
                
                // NIVEL 1+: FPS, Game Name y Hora
                lblFPS.Text = rendimiento.Fps > 0 ? $"{rendimiento.Fps:0}" : "--";
                lblGameName.Text = !string.IsNullOrEmpty(rendimiento.GameName) ? rendimiento.GameName : "";
                lblHoraHUD.Text = DateTime.Now.ToString("HH:mm");

                if (!string.IsNullOrEmpty(rendimiento.ProcessPath) && System.IO.File.Exists(rendimiento.ProcessPath))
                {
                    using (var sysicon = System.Drawing.Icon.ExtractAssociatedIcon(rendimiento.ProcessPath))
                    {
                        if (sysicon != null)
                        {
                            imgGameIcon.Source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                                sysicon.Handle,
                                System.Windows.Int32Rect.Empty,
                                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                            borderGameIcon.Visibility = Visibility.Visible;
                        }
                    }
                }
                else
                {
                    borderGameIcon.Visibility = Visibility.Collapsed;
                }

                // NIVEL 2+: Gráfica Frametime ms & Resolución
                if (FilaGrafica.Visibility == Visibility.Visible)
                {
                    lblFrametimeMs.Text = rendimiento.FrametimeMs > 0 ? $"{rendimiento.FrametimeMs:0.0} ms" : "-- ms";
                    lblResolution.Text = !string.IsNullOrEmpty(rendimiento.Resolution) ? $"{rendimiento.Resolution}" : "";

                    if (rendimiento.Fps > 0)
                    {
                        _fpsHistory.Enqueue(rendimiento.Fps);
                        if (_fpsHistory.Count > 40) _fpsHistory.Dequeue();
                        
                        var points = new System.Windows.Media.PointCollection();
                        int x = 0;
                        float maxFps = 60f;
                        foreach(var f in _fpsHistory) if (f > maxFps) maxFps = f;
                        if (maxFps < 60) maxFps = 60;

                        foreach(var f in _fpsHistory)
                        {
                            double y = 22 - ((f / maxFps) * 22);
                            points.Add(new System.Windows.Point(x * 3, y));
                            x++;
                        }
                        graphFPS.Points = points;
                    }
                }

                // NIVEL 3+: GPU (Carga, Temp, Clock, Power, VRAM)
                if (FilaGPU.Visibility == Visibility.Visible)
                {
                    float gpuLoad = SysInfo.GetGpuLoad();
                    float gpuTemp = SysInfo.GetGpuTemp();
                    float gpuClock = SysInfo.GetGpuClock();
                    float gpuPower = SysInfo.GetGpuPower();
                    float vramUsedMb = SysInfo.GetGpuVramUsedMb();

                    lblGPU.Text = $"{gpuLoad:0}%";
                    lblGPUTemp.Text = gpuTemp > 0 ? $"{gpuTemp:0}°C" : "--°C";
                    lblGPUClock.Text = gpuClock > 0 ? $"{gpuClock:0} MHz" : "-- MHz";
                    lblGPUPower.Text = gpuPower > 0 ? $"{gpuPower:0}W" : "--W";
                    
                    if (vramUsedMb > 0)
                    {
                        float vramGb = vramUsedMb > 100 ? (vramUsedMb / 1024f) : vramUsedMb;
                        lblVRAMUsed.Text = $"{vramGb:0.0} GB";
                    }
                    else
                    {
                        lblVRAMUsed.Text = "-- GB";
                    }

                    string gpuName = SysInfo.GetGpuName();
                    string driverVer = SysInfo.GetGpuDriverVersion();
                    if (!string.IsNullOrEmpty(gpuName))
                    {
                        lblGPUName.Text = string.IsNullOrEmpty(driverVer) ? gpuName : $"{gpuName}  (Driver: {driverVer})";
                        lblGPUName.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        lblGPUName.Visibility = Visibility.Collapsed;
                    }
                }

                // NIVEL 3+: CPU (Carga, Temp, Clock, Power, RAM)
                if (FilaCPU.Visibility == Visibility.Visible)
                {
                    float cpuLoad = SysInfo.GetCpuLoad();
                    float cpuTemp = SysInfo.GetCpuTemp();
                    float cpuClock = SysInfo.GetCpuClock();
                    float cpuPower = SysInfo.GetCpuPower();

                    if (cpuLoad == 0) cpuLoad = (float)SysInfo.GetCpuUsage();

                    lblCPU.Text = $"{cpuLoad:0}%";
                    lblCPUTemp.Text = cpuTemp > 0 ? $"{cpuTemp:0}°C" : "--°C";
                    lblCPUClock.Text = cpuClock > 0 ? $"{cpuClock:0} MHz" : "-- MHz";
                    lblCPUPower.Text = cpuPower > 0 ? $"{cpuPower:0}W" : "--W";
                    
                    float ramUsedGb = SysInfo.GetRamUsedGb();
                    lblRAMUsed.Text = ramUsedGb > 0 ? $"{ramUsedGb:0.0} GB" : "-- GB";
                }

                // NIVEL 4+: VENTILADORES RPM
                if (RivaTunerCore.NivelOSDActual >= 4)
                {
                    float gpuFan = SysInfo.GetGpuFanRPM();
                    float cpuFan = SysInfo.GetCpuFanRPM();

                    bool isGpuPct = SysInfo.IsGpuFanPercentage();

                    if (gpuFan > 0 || isGpuPct)
                    {
                        panelGPUFan.Visibility = Visibility.Visible;
                        if (isGpuPct) lblGPUFan.Text = $"{gpuFan:0} %";
                        else lblGPUFan.Text = $"{gpuFan:0} RPM";
                    }
                    else
                    {
                        panelGPUFan.Visibility = Visibility.Collapsed;
                    }

                    if (cpuFan > 0)
                    {
                        panelCPUFan.Visibility = Visibility.Visible;
                        lblCPUFan.Text = $"{cpuFan:0} RPM";
                    }
                    else
                    {
                        panelCPUFan.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    panelGPUFan.Visibility = Visibility.Collapsed;
                    panelCPUFan.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }
    }
}
