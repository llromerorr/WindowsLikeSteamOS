using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using SharpDX.DirectInput;
using SteamOSConfigurator.Services;
using SteamOSConfigurator.Helpers;

namespace SteamOSConfigurator
{
    public enum AccionRecuperacion
    {
        Ninguna,
        ReintentarSteam,
        ModoEscritorio,
        CerrarSesionWindows
    }

    public partial class VentanaRecuperacion : Window
    {
        public AccionRecuperacion AccionResultante { get; private set; } = AccionRecuperacion.Ninguna;

        private readonly IDependencyService _dependencyService = new DependencyService();
        private readonly IDisplayService _displayService = new DisplayService();
        private List<Button> _botonesNavegables = new();
        private int _focusedIndex = 0;
        private CancellationTokenSource? _cancellationTokenSource;

        public VentanaRecuperacion()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _botonesNavegables = new List<Button>
            {
                btnReintentar,
                btnRepararCDN,
                btnRestaurarPantalla,
                btnVerLogs,
                btnCerrarSesion,
                btnModoEscritorio
            };

            CargarLogs();

            if (_botonesNavegables.Count > 0)
            {
                _botonesNavegables[0].Focus();
            }

            // Iniciar bucle de lectura de mando directo e independiente
            _cancellationTokenSource = new CancellationTokenSource();
            _ = Task.Run(() => BucleNavegacionMandoDirecto(_cancellationTokenSource.Token));
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
        }

        public void CargarLogs()
        {
            try
            {
                string logPath = AppPaths.LogFile;
                if (File.Exists(logPath))
                {
                    var lineas = File.ReadAllLines(logPath);
                    var ultimasLineas = lineas.Skip(Math.Max(0, lineas.Length - 100)).ToArray();
                    txtLogs.Text = string.Join(Environment.NewLine, ultimasLineas);
                    scrollLogs.ScrollToEnd();
                    lblUltimaActualizacion.Text = $"Actualizado: {DateTime.Now:HH:mm:ss}";
                }
                else
                {
                    txtLogs.Text = "No se encontró el archivo de registro en C:\\ProgramData\\SteamOS\\debug_log.txt";
                }
            }
            catch (Exception ex)
            {
                txtLogs.Text = $"Error al leer el registro: {ex.Message}";
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Down)
            {
                MoverEnfoque(1);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Up)
            {
                MoverEnfoque(-1);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                BtnModoEscritorio_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Space)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_focusedIndex >= 0 && _focusedIndex < _botonesNavegables.Count)
                    {
                        _botonesNavegables[_focusedIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                });
                e.Handled = true;
            }
        }

        private void MoverEnfoque(int delta)
        {
            if (_botonesNavegables.Count == 0) return;
            Dispatcher.Invoke(() =>
            {
                _focusedIndex = (_focusedIndex + delta + _botonesNavegables.Count) % _botonesNavegables.Count;
                _botonesNavegables[_focusedIndex].Focus();
            });
        }

        private async Task BucleNavegacionMandoDirecto(CancellationToken token)
        {
            bool arribaPresionado = false;
            bool abajoPresionado = false;
            bool enterPresionado = false;
            bool cancelPresionado = false;

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(200, token);
                try
                {
                    using (var directInput = new DirectInput())
                    {
                        var dispositivos = directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
                        if (dispositivos.Count > 0)
                        {
                            using (var joystick = new Joystick(directInput, dispositivos[0].InstanceGuid))
                            {
                                try { joystick.Acquire(); } catch { }

                                while (!token.IsCancellationRequested)
                                {
                                    await Task.Delay(30, token);
                                    joystick.Poll();
                                    var st = joystick.GetCurrentState();

                                    bool arriba = false;
                                    bool abajo = false;
                                    bool botonA = st.Buttons.Length > 0 && st.Buttons[0]; // Botón A (0)
                                    bool botonB = st.Buttons.Length > 1 && st.Buttons[1]; // Botón B (1)

                                    if (st.PointOfViewControllers.Length > 0)
                                    {
                                        int pov = st.PointOfViewControllers[0];
                                        arriba = (pov == 0 || pov == 4500 || pov == 31500);
                                        abajo = (pov == 13500 || pov == 18000 || pov == 22500);
                                    }

                                    int ly = JoystickHelper.ObtenerValorEje(st, "Y");
                                    if (ly < 15000) arriba = true;
                                    if (ly > 50000) abajo = true;

                                    if (arriba && !arribaPresionado)
                                    {
                                        MoverEnfoque(-1);
                                        arribaPresionado = true;
                                    }
                                    else if (!arriba) arribaPresionado = false;

                                    if (abajo && !abajoPresionado)
                                    {
                                        MoverEnfoque(1);
                                        abajoPresionado = true;
                                    }
                                    else if (!abajo) abajoPresionado = false;

                                    if (botonA && !enterPresionado)
                                    {
                                        enterPresionado = true;
                                        Dispatcher.Invoke(() =>
                                        {
                                            if (_focusedIndex >= 0 && _focusedIndex < _botonesNavegables.Count)
                                            {
                                                _botonesNavegables[_focusedIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                            }
                                        });
                                    }
                                    else if (!botonA) enterPresionado = false;

                                    if (!botonB) cancelPresionado = false;
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private void BtnReintentar_Click(object sender, RoutedEventArgs e)
        {
            Logger.Log("[VentanaRecuperacion] Usuario seleccionó Reintentar Steam.");
            AccionResultante = AccionRecuperacion.ReintentarSteam;
            Close();
        }

        private async void BtnRepararCDN_Click(object sender, RoutedEventArgs e)
        {
            btnRepararCDN.IsEnabled = false;
            lblEstadoSubtitulo.Text = "Descargando e instalando Steam desde CDN oficial de Valve...";
            Logger.Log("[VentanaRecuperacion] Iniciando reinstalación de Steam desde CDN...");

            bool exito = await _dependencyService.InstalarSteamAsync(progreso =>
            {
                Dispatcher.Invoke(() => lblEstadoSubtitulo.Text = progreso);
            });

            if (exito)
            {
                lblEstadoSubtitulo.Text = "¡Steam instalado/reparado con éxito! Puedes reintentar el inicio.";
                Logger.Log("[VentanaRecuperacion] Steam reinstalado correctamente.");
            }
            else
            {
                lblEstadoSubtitulo.Text = "Error al reinstalar Steam. Revisa los registros.";
                Logger.Log("[VentanaRecuperacion] Error durante la reinstalación de Steam.");
            }
            btnRepararCDN.IsEnabled = true;
            CargarLogs();
        }

        private void BtnRestaurarPantalla_Click(object sender, RoutedEventArgs e)
        {
            Logger.Log("[VentanaRecuperacion] Restaurando entorno original de pantallas.");
            _displayService.RestaurarEntornoOriginal(new NvidiaGpuScalingService());
            lblEstadoSubtitulo.Text = "Entorno de pantallas restaurado a valores por defecto.";
            CargarLogs();
        }

        private void BtnVerLogs_Click(object sender, RoutedEventArgs e)
        {
            CargarLogs();
        }

        private void BtnCerrarSesion_Click(object? sender, RoutedEventArgs? e)
        {
            Logger.Log("[VentanaRecuperacion] Usuario seleccionó Cerrar Sesión de Windows.");
            AccionResultante = AccionRecuperacion.CerrarSesionWindows;
            Close();
        }

        private void BtnModoEscritorio_Click(object? sender, RoutedEventArgs? e)
        {
            Logger.Log("[VentanaRecuperacion] Usuario seleccionó Salir a Modo Escritorio.");
            AccionResultante = AccionRecuperacion.ModoEscritorio;
            Close();
        }
    }
}
