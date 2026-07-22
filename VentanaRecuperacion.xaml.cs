using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Diagnostics;
using SteamOSConfigurator.Services;
using SteamOSConfigurator.Helpers;

namespace SteamOSConfigurator
{
    public enum AccionRecuperacion
    {
        Ninguno,
        ReintentarSteam,
        ModoEscritorio,
        CerrarSesionWindows
    }

    public partial class VentanaRecuperacion : Window
    {
        // ── Win32: forzar foco incluso sobre juegos fullscreen ──
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        public AccionRecuperacion AccionResultante { get; set; } = AccionRecuperacion.Ninguno;

        private readonly IPowerService _powerService = new PowerService();
        private readonly IDependencyService _dependencyService = new DependencyService();
        private readonly IDisplayService _displayService = new DisplayService();
        private readonly IAudioService _audioService;
        private readonly IGpuScalingService _gpuScalingService = new NvidiaGpuScalingService();
        private readonly SteamService _steamService = new();

        private List<Button> _botonesNavegables = new();
        private int _focusedIndex = 0;

        // ── ESTADOS ──
        private int _volumenActual = 85;
        private readonly int[] _opcionesFPS = new int[] { 0, 30, 40, 60, 90, 120 };
        private int _indexFPS = 0;
        private readonly string[] _nombresOSD = new string[] { "OFF", "FPS", "GPU/CPU", "Frametime", "Full" };
        private int _indexOSD = 0;
        private bool _gpuStretchActivo = true;

        private DispatcherTimer _timerDashboard;
        private bool _isClosing = false;
        private Action? _accionConfirmada;
        private bool _modoConfirmacion = false;
        private List<Button> _botonesOriginales = new();
        private int _indiceOriginal = 0;

        public static VentanaRecuperacion? Instancia { get; private set; }

        public VentanaRecuperacion()
        {
            InitializeComponent();
            Instancia = this;
            _audioService = new AudioService();
            _botonesNavegables = new List<Button> { btnVolumen, btnLimiteFPS, btnNivelOSD, btnGpuStretch, btnLiberarRAM, btnReintentar, btnReinstalarSteam, btnRestaurarPantalla, btnSuspenderConsola, btnHibernarConsola, btnCerrarSesion, btnReiniciarConsola, btnApagarConsola, btnModoEscritorio };
            
            _timerDashboard = new DispatcherTimer();
            _timerDashboard.Interval = TimeSpan.FromSeconds(1);
            _timerDashboard.Tick += TimerDashboard_Tick;

            try
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, ev) =>
                {
                    try { Dispatcher.Invoke(AjustarTamanioPantalla); } catch { }
                };
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        public void AjustarTamanioPantalla()
        {
            try
            {
                PresentationSource source = PresentationSource.FromVisual(this);
                double dpiX = 1.0, dpiY = 1.0;
                if (source?.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformToDevice.M11;
                    dpiY = source.CompositionTarget.TransformToDevice.M22;
                }

                int screenWidthPixels = GetSystemMetrics(0);  // SM_CXSCREEN
                int screenHeightPixels = GetSystemMetrics(1); // SM_CYSCREEN

                Left = 0;
                Top = 0;
                Width = screenWidthPixels > 0 ? screenWidthPixels / dpiX : SystemParameters.PrimaryScreenWidth;
                Height = screenHeightPixels > 0 ? screenHeightPixels / dpiY : SystemParameters.PrimaryScreenHeight;
            }
            catch
            {
                Left = 0;
                Top = 0;
                Width = SystemParameters.PrimaryScreenWidth;
                Height = SystemParameters.PrimaryScreenHeight;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  NAVEGACIÓN DIRECTA — llamadas desde TraductorMando sin
        //  depender de foco de teclado ni PresentationSource
        // ══════════════════════════════════════════════════════════════

        public void NavUp() => Dispatcher.Invoke(() => MoverEnfoque(-1));
        public void NavDown() => Dispatcher.Invoke(() => MoverEnfoque(1));
        public void NavLeft() => Dispatcher.Invoke(() => AjustarOpcionActual(-1));
        public void NavRight() => Dispatcher.Invoke(() => AjustarOpcionActual(1));
        public void NavSelect() => Dispatcher.Invoke(() =>
        {
            if (_focusedIndex >= 0 && _focusedIndex < _botonesNavegables.Count)
                _botonesNavegables[_focusedIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        public void NavBack() => Dispatcher.Invoke(() => 
        {
            if (_modoConfirmacion)
            {
                OcultarConfirmacion();
            }
            else
            {
                OcultarPanel();
            }
        });

        // ══════════════════════════════════════════════════════════════
        //  FORZAR FOCO (Win32 AttachThreadInput)
        // ══════════════════════════════════════════════════════════════


        public void MostrarPanel()
        {
            _isClosing = false;
            AjustarTamanioPantalla();
            OcultarConfirmacion();
            Opacity = 1;
            IsHitTestVisible = true;
            CargarEstadosIniciales();
            
            if (_botonesNavegables.Count > 0)
            {
                _focusedIndex = 0;
                ActualizarEstilosBotones();
            }

            ActualizarDashboard();
            _timerDashboard.Start();

            if (!IsLoaded)
            {
                RoutedEventHandler onLoaded = null!;
                onLoaded = (s, e) => 
                {
                    Loaded -= onLoaded;
                    ReproducirAnimacionEntrada();
                };
                Loaded += onLoaded;
            }
            else
            {
                ReproducirAnimacionEntrada();
            }
        }

        // ── SISTEMA DE CONFIRMACIÓN ──
        private void SolicitarConfirmacion(string titulo, string subtitulo, Action accion)
        {
            _accionConfirmada = accion;
            _modoConfirmacion = true;
            _indiceOriginal = _focusedIndex;
            txtTituloConfirmacion.Text = titulo;
            txtSubtituloConfirmacion.Text = subtitulo;
            OverlayConfirmacion.Visibility = Visibility.Visible;

            _botonesOriginales = new List<Button>(_botonesNavegables);
            _botonesNavegables = new List<Button> { btnConfirmarAccion, btnCancelarAccion };
            _focusedIndex = 0;
            ActualizarEstilosBotones();
        }

        private void BtnConfirmarAccion_Click(object sender, RoutedEventArgs e)
        {
            Action? a = _accionConfirmada;
            OcultarConfirmacion();
            a?.Invoke();
        }

        private void BtnCancelarAccion_Click(object sender, RoutedEventArgs e)
        {
            OcultarConfirmacion();
        }

        private void OcultarConfirmacion()
        {
            _modoConfirmacion = false;
            OverlayConfirmacion.Visibility = Visibility.Collapsed;
            if (_botonesOriginales.Count > 0)
            {
                _botonesNavegables = new List<Button>(_botonesOriginales);
                _focusedIndex = _indiceOriginal;
            }
            ActualizarEstilosBotones();
        }

        private void ReproducirAnimacionEntrada()
        {
            DoubleAnimation anim = new DoubleAnimation(310, 0, TimeSpan.FromMilliseconds(200));
            anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            CajonTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        public event EventHandler? PanelOcultado;

        public void OcultarPanel()
        {
            if (_isClosing) return;
            _isClosing = true;
            
            TraductorMando.NotificarQAMCerrado();

            DoubleAnimation anim = new DoubleAnimation(0, 310, TimeSpan.FromMilliseconds(150));
            anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
            anim.Completed += (s, ev) => 
            {
                Opacity = 0;
                IsHitTestVisible = false;
                _timerDashboard.Stop();
                App.VentanaRecuperacionAbierta = false;
                Logger.Log("[VentanaRecuperacion] Panel oculto.");
                PanelOcultado?.Invoke(this, EventArgs.Empty);
            };
            CajonTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Ya no hace MostrarPanel aquí, lo hará App.xaml.cs
        }

        private void TimerDashboard_Tick(object sender, EventArgs e)
        {
            ActualizarDashboard();
        }

        private void ActualizarDashboard()
        {
            lblHora.Text = DateTime.Now.ToString("HH:mm");

            double cpu = SysInfo.GetCpuUsage();
            lblCPU.Text = $"{cpu:0}%";
            barCPU.Value = cpu;

            float cpuTemp = SysInfo.GetCpuTemp();
            lblCPUTemp.Text = cpuTemp > 0 ? $"{cpuTemp:0}°C" : "--°C";

            double ram = SysInfo.GetRamUsage();
            lblRAM.Text = $"{ram:0}%";
            barRAM.Value = ram;

            float gpuLoad = SysInfo.GetGpuLoad();
            lblGPU.Text = $"{gpuLoad:0}%";
            barGPU.Value = gpuLoad;

            float gpuTemp = SysInfo.GetGpuTemp();
            lblGPUTemp.Text = gpuTemp > 0 ? $"{gpuTemp:0}°C" : "--°C";

            var (isCharging, batteryPercent) = SysInfo.GetBatteryStatus();
            if (batteryPercent >= 0)
            {
                panelBateria.Visibility = Visibility.Visible;
                lblBateria.Text = $"{batteryPercent}%";
                barBateria.Value = batteryPercent;
            }
            else
            {
                panelBateria.Visibility = Visibility.Collapsed;
            }
        }

        public bool IsAppShuttingDown { get; set; } = false;

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!IsAppShuttingDown)
            {
                e.Cancel = true;
                OcultarPanel();
                return;
            }

            _timerDashboard?.Stop();
            Instancia = null;
            _audioService.Dispose();
            Logger.Log("[VentanaRecuperacion] Ventana destruyéndose.");
        }

        // Click en el área transparente fuera del cajón → cerrar
        private void AreaTransparente_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Logger.Log("[VentanaRecuperacion] Click fuera del cajón. Ocultando.");
            OcultarPanel();
        }

        private void CargarEstadosIniciales()
        {
            try
            {
                _volumenActual = _audioService.ObtenerVolumenActual();
                ActualizarUIVolumen();
                _indexOSD = Math.Clamp(RivaTunerCore.NivelOSDActual, 0, _nombresOSD.Length - 1);
                ActualizarUIOSD();
                int currentFPS = RivaTunerCore.LimiteFPSActual;
                for (int i = 0; i < _opcionesFPS.Length; i++)
                    if (_opcionesFPS[i] == currentFPS) { _indexFPS = i; break; }
                ActualizarUIFPS();
                ActualizarUIStretch();
            }
            catch (Exception ex)
            {
                Logger.Log($"[VentanaRecuperacion] Error cargando estados: {ex.Message}");
            }
        }

        // ── UI Updates ──
        private void ActualizarUIVolumen()
        {
            bool muted = _audioService.EstaSilenciado();
            lblValVolumen.Text = muted ? "MUTE" : $"{_volumenActual}%";
        }

        private void ActualizarUIFPS()
        {
            int val = _opcionesFPS[_indexFPS];
            lblValFPS.Text = val == 0 ? "OFF" : $"{val}";
        }

        private void ActualizarUIOSD() => lblValOSD.Text = _nombresOSD[_indexOSD];
        private void ActualizarUIStretch() => lblValStretch.Text = _gpuStretchActivo ? "ON" : "OFF";

        // ── Navegación ──
        private void ActualizarEstilosBotones()
        {
            for (int i = 0; i < _botonesNavegables.Count; i++)
            {
                var btn = _botonesNavegables[i];
                if (i == _focusedIndex)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(42, 63, 94)); // Steam Pill Cyan Background (#2A3F5E)
                    btn.ClearValue(Button.BorderBrushProperty);
                }
                else
                {
                    btn.ClearValue(Button.BackgroundProperty);
                    btn.ClearValue(Button.BorderBrushProperty);
                }
            }
        }

        private void MoverEnfoque(int delta)
        {
            if (_botonesNavegables.Count == 0) return;
            _focusedIndex = (_focusedIndex + delta + _botonesNavegables.Count) % _botonesNavegables.Count;
            ActualizarEstilosBotones();
            try { _botonesNavegables[_focusedIndex].BringIntoView(); } catch { }
        }

        private void AjustarOpcionActual(int delta)
        {
            if (_focusedIndex < 0 || _focusedIndex >= _botonesNavegables.Count) return;
            var btn = _botonesNavegables[_focusedIndex];

            if (btn == btnVolumen)
            {
                _volumenActual = _audioService.AjustarVolumen(delta * 5);
                ActualizarUIVolumen();
            }
            else if (btn == btnLimiteFPS)
            {
                _indexFPS = (_indexFPS + delta + _opcionesFPS.Length) % _opcionesFPS.Length;
                RivaTunerCore.AplicarConfiguracion(_opcionesFPS[_indexFPS], _indexOSD);
                ActualizarUIFPS();
            }
            else if (btn == btnNivelOSD)
            {
                _indexOSD = (_indexOSD + delta + _nombresOSD.Length) % _nombresOSD.Length;
                RivaTunerCore.AplicarConfiguracion(_opcionesFPS[_indexFPS], _indexOSD);
                ActualizarUIOSD();
            }
            else if (btn == btnGpuStretch)
            {
                _gpuStretchActivo = !_gpuStretchActivo;
                Task.Run(() =>
                {
                    if (_gpuStretchActivo) _gpuScalingService.ForzarEscaladoCompleto();
                    else _gpuScalingService.RestaurarEscaladoPorMonitor();
                });
                ActualizarUIStretch();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up:    MoverEnfoque(-1); e.Handled = true; break;
                case Key.Down:  MoverEnfoque(1);  e.Handled = true; break;
                case Key.Left:  AjustarOpcionActual(-1); e.Handled = true; break;
                case Key.Right: AjustarOpcionActual(1);  e.Handled = true; break;
                case Key.Escape: 
                    if(_modoConfirmacion) OcultarConfirmacion();
                    else OcultarPanel(); 
                    e.Handled = true; break;
                case Key.Enter:
                case Key.Space:
                    if (_focusedIndex >= 0 && _focusedIndex < _botonesNavegables.Count)
                        _botonesNavegables[_focusedIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    e.Handled = true;
                    break;
            }
        }

        // ── Botones ──
        private void BtnCerrarMenu_Click(object sender, RoutedEventArgs e) => OcultarPanel();
        private void BtnVolumen_Click(object sender, RoutedEventArgs e) { _audioService.AlternarSilencio(); ActualizarUIVolumen(); }
        private void BtnLimiteFPS_Click(object sender, RoutedEventArgs e) => AjustarOpcionActual(1);
        private void BtnNivelOSD_Click(object sender, RoutedEventArgs e) => AjustarOpcionActual(1);
        private void BtnGpuStretch_Click(object sender, RoutedEventArgs e) => AjustarOpcionActual(1);

        private async void BtnReintentar_Click(object sender, RoutedEventArgs e)
        {
            SolicitarConfirmacion(
                "¿Reiniciar Steam?", 
                "Se cerrará Steam y se volverá a iniciar en modo consola.", 
                EjecutarReiniciarSteam
            );
        }

        private async void EjecutarReiniciarSteam()
        {
            App.ReinstalandoOReinicioSteam = true;
            btnReintentar.IsEnabled = false;
            lblEstadoReiniciarSteam.Text = "Cerrando...";

            await Task.Run(() =>
            {
                foreach (var p in Process.GetProcessesByName("steam")) { try { p.Kill(); p.Dispose(); } catch { } }
                foreach (var p in Process.GetProcessesByName("steamwebhelper")) { try { p.Kill(); p.Dispose(); } catch { } }
            });

            await Task.Delay(1500);
            lblEstadoReiniciarSteam.Text = "Iniciando...";

            await Task.Run(() =>
            {
                string rutaSteam = _steamService.ObtenerRutaSteam();
                if (!string.IsNullOrEmpty(rutaSteam))
                {
                    Process.Start(new ProcessStartInfo { FileName = rutaSteam, Arguments = "-gamepadui", UseShellExecute = true });
                }
            });

            await Task.Delay(3000);
            lblEstadoReiniciarSteam.Text = "¡Listo!";
            await Task.Delay(2000);
            lblEstadoReiniciarSteam.Text = "";
            btnReintentar.IsEnabled = true;
            App.ReinstalandoOReinicioSteam = false;
        }

        private void BtnReinstalarSteam_Click(object sender, RoutedEventArgs e)
        {
            SolicitarConfirmacion(
                "¿Reinstalar Steam?", 
                "Se descargará e instalará la versión oficial de Steam. Los juegos no se borrarán.", 
                EjecutarReinstalarSteam
            );
        }

        private async void EjecutarReinstalarSteam()
        {
            App.ReinstalandoOReinicioSteam = true;
            btnReinstalarSteam.IsEnabled = false;
            lblEstadoReinstalar.Text = "Instalando...";
            
            bool exito = await _dependencyService.InstalarSteamAsync(estado => 
            {
                Dispatcher.Invoke(() => lblEstadoReinstalar.Text = estado);
            });

            Dispatcher.Invoke(() =>
            {
                lblEstadoReinstalar.Text = exito ? "¡Listo!" : "Error";
                Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => 
                {
                    lblEstadoReinstalar.Text = "";
                    btnReinstalarSteam.IsEnabled = true;
                    App.ReinstalandoOReinicioSteam = false;
                }));
            });
        }

        private void BtnRestaurarPantalla_Click(object sender, RoutedEventArgs e)
        {
            _displayService.RestaurarEntornoOriginal(_gpuScalingService);
        }

        private async void BtnLiberarRAM_Click(object sender, RoutedEventArgs e)
        {
            lblValRAM.Text = "Liberando...";
            btnLiberarRAM.IsEnabled = false;

            await Task.Run(() => 
            {
                try 
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    foreach (Process proc in Process.GetProcesses())
                    {
                        try { SetProcessWorkingSetSize(proc.Handle, -1, -1); } catch { }
                    }
                } 
                catch { }
            });

            lblValRAM.Text = "¡RAM Liberada!";
            await Task.Delay(2000);
            lblValRAM.Text = "Limpiar";
            btnLiberarRAM.IsEnabled = true;
        }

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

        [DllImport("PowrProf.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hiberate, bool forceCritical, bool disableWakeEvent);

        private void BtnSuspenderConsola_Click(object sender, RoutedEventArgs e)
        {
            SolicitarConfirmacion(
                "¿Suspender Consola?", 
                "La consola entrará en modo de suspensión.", 
                () => 
                {
                    Logger.Log("[QAM] Usuario seleccionó Suspender Consola.");
                    try { SetSuspendState(false, true, false); } catch { }
                }
            );
        }

        private void BtnHibernarConsola_Click(object sender, RoutedEventArgs e)
        {
            SolicitarConfirmacion(
                "¿Hibernar Consola?", 
                "La consola guardará el estado actual en el disco y se apagará.", 
                () => 
                {
                    Logger.Log("[QAM] Usuario seleccionó Hibernar Consola.");
                    try { SetSuspendState(true, true, false); } catch { }
                }
            );
        }

        private void BtnReiniciarConsola_Click(object sender, RoutedEventArgs e)
        {
            SolicitarConfirmacion(
                "¿Reiniciar Consola?", 
                "Se cerrarán los juegos abiertos y el sistema se reiniciará.", 
                () => 
                {
                    Logger.Log("[QAM] Usuario seleccionó Reiniciar Consola.");
                    try { Process.Start("shutdown.exe", "/r /t 0"); } catch { }
                }
            );
        }

        private void BtnApagarConsola_Click(object sender, RoutedEventArgs e)
        {
            SolicitarConfirmacion(
                "¿Apagar Consola?", 
                "Se cerrarán todos los programas y el equipo se apagará.", 
                () => 
                {
                    Logger.Log("[QAM] Usuario seleccionó Apagar Consola.");
                    try { Process.Start("shutdown.exe", "/s /t 0"); } catch { }
                }
            );
        }

        private void BtnCerrarSesion_Click(object? sender, RoutedEventArgs? e)
        {
            SolicitarConfirmacion(
                "¿Cerrar Sesión de Windows?", 
                "Se cerrará la sesión del usuario actual de Windows.", 
                () => 
                {
                    AccionResultante = AccionRecuperacion.CerrarSesionWindows;
                    OcultarPanel();
                }
            );
        }

        private void BtnModoEscritorio_Click(object? sender, RoutedEventArgs? e)
        {
            SolicitarConfirmacion(
                "¿Salir al Escritorio?", 
                "Se cerrará el entorno de consola e iniciará el escritorio de Windows Explorer.", 
                () => 
                {
                    AccionResultante = AccionRecuperacion.ModoEscritorio;
                    OcultarPanel();
                }
            );
        }
    }
}
