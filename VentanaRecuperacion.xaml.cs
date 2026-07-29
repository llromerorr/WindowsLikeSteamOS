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
using WindowsLikeSteamOS.Services;

namespace SteamOSConfigurator
{
    public enum AccionRecuperacion
    {
        Ninguno,
        ReintentarSteam,
        ModoEscritorio,
        CerrarSesionWindows
    }

    public class GameViewModel
    {
        public SteamOSConfigurator.Helpers.ManagedGame Game { get; set; }
        public string Name => Game?.Name ?? "";
        public string StatusText => (Game?.IsPluginInstalled ?? false) ? "PROXY ACTIVO" : "";
        public string StatusColor => (Game?.IsPluginInstalled ?? false) ? "#00FF00" : "Transparent";
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
        private readonly int[] _opcionesFPS = new int[] { 0, 30, 40, 60, 90, 120, 144 };
        private int _indexFPS = 0;
        private readonly string[] _nombresOSD = new string[] { "OFF", "Nivel 1 (FPS)", "Nivel 2 (Básico)", "Nivel 3 (Completo)", "Nivel 4 (Avanzado)" };
        private int _indexOSD = 0;
        private readonly string[] _nombresMotorOSD = new string[] { "WPF", "RTSS" };
        private int _indexMotorOSD = 0;
        private bool _gpuStretchActivo = true;
        private readonly string[] _opcionesEscalado = new string[] { "OFF", "720p (FSR)", "900p (FSR)" };
        private int _indexEscalado = 0;

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

                int screenWidthPixels = GetSystemMetrics(0);  
                int screenHeightPixels = GetSystemMetrics(1); 

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
        //  NAVEGACIÓN DIRECTA — llamadas desde TraductorMando
        // ══════════════════════════════════════════════════════════════

        public void NavUp() => Dispatcher.Invoke(() => 
        {
            if (Grid_TabJuegos.Visibility == Visibility.Visible)
            {
                if (lstJuegos.Items.Count > 0)
                {
                    lstJuegos.SelectedIndex = Math.Max(0, lstJuegos.SelectedIndex - 1);
                    lstJuegos.ScrollIntoView(lstJuegos.SelectedItem);
                }
                return;
            }
            MoverEnfoque(-1);
        });
        public void NavDown() => Dispatcher.Invoke(() => 
        {
            if (Grid_TabJuegos.Visibility == Visibility.Visible)
            {
                if (lstJuegos.Items.Count > 0)
                {
                    lstJuegos.SelectedIndex = Math.Min(lstJuegos.Items.Count - 1, lstJuegos.SelectedIndex + 1);
                    lstJuegos.ScrollIntoView(lstJuegos.SelectedItem);
                }
                return;
            }
            MoverEnfoque(1);
        });
        public void NavLeft() => Dispatcher.Invoke(() => AjustarOpcionActual(-1));
        public void NavRight() => Dispatcher.Invoke(() => AjustarOpcionActual(1));
        public void NavSelect() => Dispatcher.Invoke(() =>
        {
            if (Grid_TabJuegos.Visibility == Visibility.Visible)
            {
                if (btnGestionarJuego.IsEnabled)
                    btnGestionarJuego.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return;
            }
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

        public void NavPrevTab() => Dispatcher.Invoke(() => CambiarPestañaRelativa(-1));
        public void NavNextTab() => Dispatcher.Invoke(() => CambiarPestañaRelativa(1));

        private void CambiarPestañaRelativa(int delta)
        {
            var grids = new FrameworkElement[] { Grid_TabConfig, Grid_TabRed, Grid_TabRendimiento, Grid_TabJuegos, Grid_TabMando, Grid_TabEnergia };
            var tabs = new[] { btnTabConfig, btnTabRed, btnTabRendimiento, btnTabJuegos, btnTabMando, btnTabEnergia };
            
            int idx = Array.FindIndex(grids, g => g.Visibility == Visibility.Visible);
            if (idx == -1) idx = 0;
            idx = (idx + delta + tabs.Length) % tabs.Length;
            tabs[idx].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        // ══════════════════════════════════════════════════════════════
        //  VISIBILIDAD Y TABS
        // ══════════════════════════════════════════════════════════════

        public void MostrarPanel()
        {
            _isClosing = false;
            AjustarTamanioPantalla();
            OcultarConfirmacion();
            Opacity = 1;
            IsHitTestVisible = true;
            CargarEstadosIniciales();
            
            // Iniciar en pestaña Configuración
            CambiarPestaña(Grid_TabConfig, btnTabConfig);

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

        private void CambiarPestaña(FrameworkElement nuevaPestaña, Button btnTabSeleccionado)
        {
            Grid_TabConfig.Visibility = Visibility.Collapsed;
            Grid_TabRed.Visibility = Visibility.Collapsed;
            Grid_TabRendimiento.Visibility = Visibility.Collapsed;
            Grid_TabJuegos.Visibility = Visibility.Collapsed;
            Grid_TabMando.Visibility = Visibility.Collapsed;
            Grid_TabEnergia.Visibility = Visibility.Collapsed;
            
            btnTabConfig.Background = new SolidColorBrush(Colors.Transparent);
            btnTabRed.Background = new SolidColorBrush(Colors.Transparent);
            btnTabRendimiento.Background = new SolidColorBrush(Colors.Transparent);
            btnTabJuegos.Background = new SolidColorBrush(Colors.Transparent);
            btnTabMando.Background = new SolidColorBrush(Colors.Transparent);
            btnTabEnergia.Background = new SolidColorBrush(Colors.Transparent);

            nuevaPestaña.Visibility = Visibility.Visible;
            btnTabSeleccionado.Background = new SolidColorBrush(Color.FromRgb(42, 63, 94)); // #2A3F5E
            
            // Actualizar botones navegables de la pestaña
            if (nuevaPestaña == Grid_TabConfig) 
                _botonesNavegables = new List<Button> { btnVolumen };
            else if (nuevaPestaña == Grid_TabRed) 
                _botonesNavegables = new List<Button> {  }; 
            else if (nuevaPestaña == Grid_TabRendimiento)
            {
                _botonesNavegables = new List<Button> { btnNivelOSD, btnMotorOSD, btnLimiteFPS, btnFiltroEscalado, btnGpuStretch };
            }
            else if (nuevaPestaña == Grid_TabJuegos)
            {
                _botonesNavegables = new List<Button>();
                CargarListaJuegos();
                if (lstJuegos.Items.Count > 0 && lstJuegos.SelectedIndex == -1)
                {
                    lstJuegos.SelectedIndex = 0;
                }
            }
            else if (nuevaPestaña == Grid_TabMando) 
                _botonesNavegables = new List<Button> {  }; 
            else if (nuevaPestaña == Grid_TabEnergia) 
                _botonesNavegables = new List<Button> { btnSuspenderConsola, btnHibernarConsola, btnReiniciarConsola, btnApagarConsola, btnModoEscritorio, btnCerrarSesion, btnReintentar, btnReinstalarSteam };

            _focusedIndex = 0;
            ActualizarEstilosBotones();
        }

        private void BtnTabConfig_Click(object sender, RoutedEventArgs e) => CambiarPestaña(Grid_TabConfig, btnTabConfig);
        private void BtnTabRed_Click(object sender, RoutedEventArgs e) => CambiarPestaña(Grid_TabRed, btnTabRed);
        private void BtnTabRendimiento_Click(object sender, RoutedEventArgs e) => CambiarPestaña(Grid_TabRendimiento, btnTabRendimiento);
        private void BtnTabJuegos_Click(object sender, RoutedEventArgs e) => CambiarPestaña(Grid_TabJuegos, btnTabJuegos);
        private void BtnTabMando_Click(object sender, RoutedEventArgs e) => CambiarPestaña(Grid_TabMando, btnTabMando);
        private void BtnTabEnergia_Click(object sender, RoutedEventArgs e) => CambiarPestaña(Grid_TabEnergia, btnTabEnergia);

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
            DoubleAnimation anim = new DoubleAnimation(360, 0, TimeSpan.FromMilliseconds(200));
            anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            CajonTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        public event EventHandler? PanelOcultado;

        public void OcultarPanel()
        {
            if (_isClosing) return;
            _isClosing = true;
            
            TraductorMando.NotificarQAMCerrado();

            DoubleAnimation anim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(150));
            anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
            anim.Completed += (s, ev) => 
            {
                Opacity = 0;
                IsHitTestVisible = false;
                _timerDashboard.Stop();
                App.VentanaRecuperacionAbierta = false;
                PanelOcultado?.Invoke(this, EventArgs.Empty);
            };
            CajonTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        private void AreaTransparente_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_modoConfirmacion) OcultarConfirmacion();
            else OcultarPanel();
        }

        private void TimerDashboard_Tick(object? sender, EventArgs e) => ActualizarDashboard();

        private void ActualizarDashboard()
        {
            lblHora.Text = DateTime.Now.ToString("HH:mm");
            var rtss = RTSSSharedMemory.ObtenerRendimientoJuegoActual();
            string pPath = rtss?.ProcessPath ?? "";
            
            if (!string.IsNullOrEmpty(pPath) && File.Exists(pPath))
            {
                try
                {
                    var icon = System.Drawing.Icon.ExtractAssociatedIcon(pPath);
                    if (icon != null)
                    {
                        imgGameIcon.Source = Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        borderGameIcon.Visibility = Visibility.Visible;
                    }
                    lblHeaderTitle.Text = Path.GetFileNameWithoutExtension(pPath);
                }
                catch
                {
                    borderGameIcon.Visibility = Visibility.Collapsed;
                    lblHeaderTitle.Text = "WindowsLikeSteamOS";
                }
            }
            else
            {
                borderGameIcon.Visibility = Visibility.Collapsed;
                lblHeaderTitle.Text = "WindowsLikeSteamOS";
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CargarEstadosIniciales();
        }

        private void CargarEstadosIniciales()
        {
            _volumenActual = _audioService.ObtenerVolumenActual();
            ActualizarUIVolumen();

            var config = ConfigManager.CargarConfiguracion();
            
            int fpsL = config.IndexFPS;
            _indexFPS = Array.IndexOf(_opcionesFPS, fpsL);
            if (_indexFPS < 0) _indexFPS = 0;
            ActualizarUIFPS();

            _indexOSD = config.IndexOSD;
            if (_indexOSD < 0 || _indexOSD >= _nombresOSD.Length) _indexOSD = 0;
            ActualizarUIOSD();

            _indexMotorOSD = config.OsdEngine == "RTSS" ? 1 : 0;
            ActualizarUIMotorOSD();

            _gpuStretchActivo = config.GpuStretchActivo;
            ActualizarUIStretch();
        }

        private void GuardarEstadoActual()
        {
            var config = ConfigManager.CargarConfiguracion();
            config.IndexFPS = _opcionesFPS[_indexFPS];
            config.IndexOSD = _indexOSD;
            config.OsdEngine = _nombresMotorOSD[_indexMotorOSD];
            config.GpuStretchActivo = _gpuStretchActivo;
            ConfigManager.GuardarConfiguracion(config);
        }

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
        private void ActualizarUIMotorOSD() => lblValMotorOSD.Text = _nombresMotorOSD[_indexMotorOSD];
        private void ActualizarUIStretch() => lblValStretch.Text = _gpuStretchActivo ? "ON" : "OFF";

        private void ActualizarEstilosBotones()
        {
            if (_botonesNavegables.Count == 0) return;
            for (int i = 0; i < _botonesNavegables.Count; i++)
            {
                var btn = _botonesNavegables[i];
                if (i == _focusedIndex)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(42, 63, 94)); // #2A3F5E
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
            if (_botonesNavegables.Count == 0 || _focusedIndex < 0 || _focusedIndex >= _botonesNavegables.Count) return;
            var btn = _botonesNavegables[_focusedIndex];

            if (btn == btnVolumen)
            {
                _volumenActual = _audioService.AjustarVolumen(delta * 5);
                ActualizarUIVolumen();
            }
            else if (btn == btnLimiteFPS)
            {
                _indexFPS = (_indexFPS + delta + _opcionesFPS.Length) % _opcionesFPS.Length;
                ActualizarUIFPS();
                RivaTunerCore.AplicarConfiguracion((int)_opcionesFPS[_indexFPS], _indexOSD);
                GuardarEstadoActual();
            }
            else if (btn == btnNivelOSD)
            {
                _indexOSD = (_indexOSD + delta + _nombresOSD.Length) % _nombresOSD.Length;
                ActualizarUIOSD();
                RivaTunerCore.AplicarConfiguracion((int)_opcionesFPS[_indexFPS], _indexOSD);
                GuardarEstadoActual();
            }
            else if (btn == btnMotorOSD)
            {
                _indexMotorOSD = (_indexMotorOSD + delta + _nombresMotorOSD.Length) % _nombresMotorOSD.Length;
                ActualizarUIMotorOSD();
                GuardarEstadoActual();
                RivaTunerCore.AplicarConfiguracion((int)_opcionesFPS[_indexFPS], _indexOSD);
            }
            else if (btn == btnGpuStretch)
            {
                _gpuStretchActivo = !_gpuStretchActivo;
                Task.Run(() =>
                {
                    if (_gpuStretchActivo) _gpuScalingService.ForzarEscaladoCompleto();
                    else _gpuScalingService.RestaurarEscaladoPorMonitor();
                });
                lblValStretch.Text = _gpuStretchActivo ? "ON" : "OFF";
                GuardarEstadoActual();
            }
            else if (btn == btnFiltroEscalado)
            {
                _indexEscalado = (_indexEscalado + delta + _opcionesEscalado.Length) % _opcionesEscalado.Length;
                lblValFiltroEscalado.Text = _opcionesEscalado[_indexEscalado];
                
                bool isFSR = _indexEscalado > 0;
                uint width = 0;
                uint height = 0;
                
                if (_indexEscalado == 1) { width = 1280; height = 720; }
                else if (_indexEscalado == 2) { width = 1600; height = 900; }
                
                SteamOSSharedMemory.Instance.SetResolutionSpoof(isFSR, width, height);
                GuardarEstadoActual();
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
                    if (_botonesNavegables.Count > 0 && _focusedIndex >= 0 && _focusedIndex < _botonesNavegables.Count)
                        _botonesNavegables[_focusedIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    e.Handled = true;
                    break;
            }
        }

        // ── Botones ──
        private void SetFocusToButton(Button btn)
        {
            int index = _botonesNavegables.IndexOf(btn);
            if (index >= 0)
            {
                _focusedIndex = index;
                ActualizarEstilosBotones();
            }
        }

        private void BtnVolumen_Click(object sender, RoutedEventArgs e) 
        { 
            if (sender is Button btn) SetFocusToButton(btn);
            _audioService.AlternarSilencio(); 
            ActualizarUIVolumen(); 
        }

        private void BtnLimiteFPS_Click(object sender, RoutedEventArgs e) 
        { 
            if (sender is Button btn) SetFocusToButton(btn);
            AjustarOpcionActual(1); 
        }

        private void BtnNivelOSD_Click(object sender, RoutedEventArgs e) 
        {
            if (sender is Button btn) SetFocusToButton(btn);
            AjustarOpcionActual(1);
        }

        private void BtnMotorOSD_Click(object sender, RoutedEventArgs e) 
        {
            if (sender is Button btn) SetFocusToButton(btn);
            AjustarOpcionActual(1);
        }

        private void BtnGpuStretch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn) SetFocusToButton(btn);
            AjustarOpcionActual(1);
        }

        private void BtnFiltroEscalado_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn) SetFocusToButton(btn);
            AjustarOpcionActual(1);
        }

        // ── PESTAÑA JUEGOS ──
        private List<GameViewModel> _juegosDisponibles = new();

        private void CargarListaJuegos()
        {
            try
            {
                var managedGames = SteamOSConfigurator.Helpers.ManagedGamesManager.GetGames();
                _juegosDisponibles = managedGames.Select(g => new GameViewModel { Game = g }).ToList();
                lstJuegos.ItemsSource = _juegosDisponibles;
                ActualizarBotonJuego(null);
            }
            catch (Exception ex)
            {
                Logger.Log($"[VentanaRecuperacion] Error cargando juegos: {ex.Message}");
            }
        }

        private void LstJuegos_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = lstJuegos.SelectedItem as GameViewModel;
            ActualizarBotonJuego(selected);
        }

        private void ActualizarBotonJuego(GameViewModel? selected)
        {
            if (selected == null)
            {
                txtAccionJuego.Text = "Selecciona un juego...";
                txtAccionJuego.Foreground = new SolidColorBrush(Color.FromRgb(243, 243, 243));
                btnGestionarJuego.IsEnabled = false;
                return;
            }

            if (SteamOSConfigurator.Services.WindowWatcherService.IsGameRunning)
            {
                txtAccionJuego.Text = "Debes cerrar el juego activo para gestionar los DLLs";
                txtAccionJuego.Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82)); // Red
                btnGestionarJuego.IsEnabled = false;
            }
            else
            {
                btnGestionarJuego.IsEnabled = true;
                if (selected.Game.IsPluginInstalled)
                {
                    txtAccionJuego.Text = "Desinstalar DXGI.dll";
                    txtAccionJuego.Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82)); // Red
                }
                else
                {
                    txtAccionJuego.Text = "Instalar DXGI.dll (Proxy)";
                    txtAccionJuego.Foreground = new SolidColorBrush(Color.FromRgb(102, 192, 244)); // Blue
                }
            }
        }

        private void BtnGestionarJuego_Click(object sender, RoutedEventArgs e)
        {
            var selected = lstJuegos.SelectedItem as GameViewModel;
            if (selected == null) return;

            try
            {
                if (selected.Game.IsPluginInstalled)
                {
                    SteamOSConfigurator.Helpers.ManagedGamesManager.UninstallPlugin(selected.Game);
                }
                else
                {
                    SteamOSConfigurator.Helpers.ManagedGamesManager.InstallPlugin(selected.Game);
                }
            }
            catch (InvalidOperationException ex)
            {
                // This means there is a third-party dll and we block it
                txtAccionJuego.Text = "Bloqueado: Ya existe otro DLL (ReShade, DXVK)";
                txtAccionJuego.Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82));
                btnGestionarJuego.IsEnabled = false;
                return;
            }

            // Refrescar lista visualmente
            lstJuegos.Items.Refresh();
            ActualizarBotonJuego(selected);
        }

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

            await Task.Run(() =>
            {
                foreach (var p in Process.GetProcessesByName("steam")) { try { p.Kill(); p.Dispose(); } catch { } }
                foreach (var p in Process.GetProcessesByName("steamwebhelper")) { try { p.Kill(); p.Dispose(); } catch { } }
            });

            await Task.Delay(1500);

            await Task.Run(() =>
            {
                string rutaSteam = _steamService.ObtenerRutaSteam();
                if (!string.IsNullOrEmpty(rutaSteam))
                {
                    Process.Start(new ProcessStartInfo { FileName = rutaSteam, Arguments = "-gamepadui", UseShellExecute = true });
                }
            });

            await Task.Delay(5000);
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
            
            bool exito = await _dependencyService.InstalarSteamAsync(estado => { });

            Dispatcher.Invoke(() =>
            {
                Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => 
                {
                    btnReinstalarSteam.IsEnabled = true;
                    App.ReinstalandoOReinicioSteam = false;
                }));
            });
        }

        private void BtnSuspenderConsola_Click(object sender, RoutedEventArgs e)
        {
            SolicitarConfirmacion("¿Suspender?", "Se suspenderá el equipo.", () => { _powerService.Suspend(); OcultarPanel(); });
        }

        private void BtnHibernarConsola_Click(object sender, RoutedEventArgs e)
        {
            SolicitarConfirmacion("¿Hibernar?", "Se hibernará el equipo.", () => { _powerService.Hibernate(); OcultarPanel(); });
        }

        private void BtnReiniciarConsola_Click(object sender, RoutedEventArgs e)
        {
            SolicitarConfirmacion("¿Reiniciar?", "Se reiniciará el equipo.", () => { _powerService.Restart(); });
        }

        private void BtnApagarConsola_Click(object sender, RoutedEventArgs e)
        {
            SolicitarConfirmacion("¿Apagar?", "Se apagará el equipo.", () => { _powerService.Shutdown(); });
        }

        private void BtnCerrarSesion_Click(object sender, RoutedEventArgs e)
        {
            SolicitarConfirmacion("¿Cerrar Sesión?", "Se cerrará la sesión actual.", () =>
            {
                AccionResultante = AccionRecuperacion.CerrarSesionWindows;
                OcultarPanel();
            });
        }

        private void BtnModoEscritorio_Click(object sender, RoutedEventArgs e)
        {
            SolicitarConfirmacion("¿Salir al Escritorio?", "Se cerrará Steam y se abrirá el escritorio.", () =>
            {
                AccionResultante = AccionRecuperacion.ModoEscritorio;
                OcultarPanel();
            });
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _timerDashboard.Stop();
        }
    }
}
