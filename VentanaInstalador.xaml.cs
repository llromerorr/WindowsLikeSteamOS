using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using SteamOSConfigurator.Helpers;
using SteamOSConfigurator.Services;

namespace SteamOSConfigurator
{
    public partial class VentanaInstalador : Wpf.Ui.Controls.FluentWindow
    {
        private InfoEstadoInstalacion _estado = new InfoEstadoInstalacion();
        private readonly DependencyService _depService = new DependencyService();

        public VentanaInstalador()
        {
            InitializeComponent();
            IconHelper.AsignarIcono(this, imgLogo);
            CargarEstadoInicial();
        }

        private void CargarEstadoInicial()
        {
            _estado = InstallationService.EvaluarEstado();

            lblFechaInstalador.Text = BuildInfo.FormatearFecha(_estado.FechaInstalador);
            lblFechaInstalada.Text = _estado.BinarioDestinoExiste 
                ? BuildInfo.FormatearFecha(_estado.FechaInstalada) 
                : "No instalado";

            switch (_estado.Estado)
            {
                case EstadoInstalacion.NoInstalado:
                    Title = "Instalador de SteamOS";
                    titleBar.Title = "Instalador de SteamOS";
                    lblSubtitulo.Text = "Transforma tu PC en una consola con inicio directo a Steam Big Picture";
                    lblMensajeEstado.Text = "Listo para instalar SteamOS y preparar tu equipo.";
                    lblMensajeEstado.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorSuccessBrush");
                    btnAccionPrincipal.Content = "INSTALAR STEAMOS";
                    break;

                case EstadoInstalacion.ActualizacionDisponible:
                    Title = "Actualizador de SteamOS";
                    titleBar.Title = "Actualizador de SteamOS";
                    lblSubtitulo.Text = "Hay una nueva versión disponible para tu equipo";
                    lblMensajeEstado.Text = "Se actualizarán los archivos del sistema. Tus juegos, resolución y mandos se mantendrán intactos.";
                    lblMensajeEstado.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorSuccessBrush");
                    btnAccionPrincipal.Content = "ACTUALIZAR STEAMOS";
                    break;

                case EstadoInstalacion.Downgrade:
                    Title = "Instalador de SteamOS";
                    titleBar.Title = "Instalador de SteamOS";
                    lblSubtitulo.Text = "La versión instalada es más reciente que este instalador";
                    lblMensajeEstado.Text = "Aviso: Tienes instalada una versión más nueva. Puedes revertir a esta versión anterior si lo necesitas.";
                    lblMensajeEstado.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorCautionBrush");
                    btnAccionPrincipal.Content = "REVERTIR A ESTA VERSIÓN";
                    break;

                case EstadoInstalacion.MismaVersion:
                case EstadoInstalacion.InstaladoYEnEjecucion:
                    Title = "Mantenimiento de SteamOS";
                    titleBar.Title = "Mantenimiento de SteamOS";
                    lblSubtitulo.Text = "Esta versión ya se encuentra instalada en tu sistema";
                    lblMensajeEstado.Text = "Puedes reinstalar para reparar accesos directos o archivos del sistema.";
                    lblMensajeEstado.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
                    btnAccionPrincipal.Content = "REPARAR / REINSTALAR";
                    break;
            }
        }

        private async void BtnAccionPrincipal_Click(object sender, RoutedEventArgs e)
        {
            viewInicio.Visibility = Visibility.Collapsed;
            viewProgreso.Visibility = Visibility.Visible;

            try
            {
                // 1. Comprobar e instalar dependencias silenciosas
                if (!_depService.SteamInstalado)
                {
                    await _depService.InstalarSteamAsync(msg =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            lblProgresoPaso.Text = "Descargando Steam...";
                            lblProgresoDetalle.Text = msg;
                        });
                    });
                }

                if (!_depService.RtssInstalado)
                {
                    await _depService.InstalarRtssAsync(msg =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            lblProgresoPaso.Text = "Configurando RivaTuner...";
                            lblProgresoDetalle.Text = msg;
                        });
                    });
                }

                // 2. Ejecutar instalación de SteamOS
                await InstallationService.InstalarOActualizarAsync(msg =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        lblProgresoPaso.Text = "Configurando SteamOS...";
                        lblProgresoDetalle.Text = msg;
                    });
                });

                // 3. Mostrar pantalla de éxito
                viewProgreso.Visibility = Visibility.Collapsed;
                viewCompletado.Visibility = Visibility.Visible;

                if (_estado.Estado == EstadoInstalacion.ActualizacionDisponible)
                {
                    lblCompletadoTitulo.Text = "¡Actualización Completada!";
                    lblCompletadoDesc.Text = "SteamOS se ha actualizado a la última versión. Toda tu configuración ha sido preservada.";
                }
                else
                {
                    lblCompletadoTitulo.Text = "¡Instalación Completada!";
                    lblCompletadoDesc.Text = "SteamOS se ha configurado correctamente. Ya tienes el acceso directo en tu Escritorio para personalizar la pantalla, audio y mando.";
                }
            }
            catch (Exception ex)
            {
                viewProgreso.Visibility = Visibility.Collapsed;
                viewInicio.Visibility = Visibility.Visible;
                MessageBox.Show($"Ocurrió un error durante el proceso:\n{ex.Message}", "Error de Instalación", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAbrirConfiguracion_Click(object sender, RoutedEventArgs e)
        {
            var mainWin = new MainWindow();
            mainWin.Show();
            Close();
        }

        private void BtnFinalizar_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnSalir_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
