using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Interop;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SharpDX.DirectInput;
using SteamOSConfigurator.Helpers;

namespace SteamOSConfigurator
{
    // Estructura pública para la configuración del mando
    public class MapeoControl
    {
        public string NombreControl { get; set; } = string.Empty;
        public int VendorID { get; set; }
        public int ProductID { get; set; }
        public Dictionary<string, int> Botones { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, string> Ejes { get; set; } = new Dictionary<string, string>();
    }

    public partial class VentanaMapeo : Window
    {
        private DirectInput _directInput;
        private Joystick? _joystick;
        private MapeoControl _configActual = new MapeoControl();
        private bool _mapeando = true;

        public VentanaMapeo()
        {
            InitializeComponent();
            _directInput = new DirectInput();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await IniciarProcesoMapeoAsync();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _mapeando = false; // Detiene los bucles asíncronos si el usuario cierra la ventana con la "X"
            try { _joystick?.Unacquire(); _joystick?.Dispose(); _directInput.Dispose(); } catch { }
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async Task IniciarProcesoMapeoAsync()
        {
            // 1. Detectar Mando
            _joystick = await DetectarMandoAsync();
            if (_joystick == null)
            {
                lblEmoji.Text = "❌";
                lblInstruccion.Text = "No se detectó ningún mando";
                lblDetalle.Text = "Verifica la conexión USB y vuelve a intentarlo.";
                lblInstruccion.Foreground = System.Windows.Media.Brushes.Crimson;
                btnCancelar.Content = "CERRAR";
                return;
            }

            _configActual.NombreControl = _joystick.Information.ProductName;
            _configActual.VendorID = _joystick.Properties.VendorId;
            _configActual.ProductID = _joystick.Properties.ProductId;
            lblSubtitulo.Text = $"MANDO DETECTADO: {_configActual.NombreControl.ToUpper()}";

            await Task.Delay(1000);

            // 2. Mapear Botones (Con emojis)
            var botones = new (string clave, string descripcion, string emoji)[]
            {
                ("A", "Botón A (Abajo)", "🟢"),
                ("B", "Botón B (Derecha)", "🔴"),
                ("X", "Botón X (Izquierda)", "🔵"),
                ("Y", "Botón Y (Arriba)", "🟡"),
                ("LB", "Gatillo superior Izquierdo (LB/L1)", "🔲"),
                ("RB", "Gatillo superior Derecho (RB/R1)", "🔲"),
                ("Select", "Botón SELECT / BACK", "◀️"),
                ("Start", "Botón START / MENU", "▶️"),
                ("L3", "Clic en palanca Izquierda", "🕹️"),
                ("R3", "Clic en palanca Derecha", "🕹️")
            };

            foreach (var (clave, descripcion, emoji) in botones)
            {
                if (!_mapeando) return;

                lblEmoji.Text = emoji;
                lblInstruccion.Text = $"Presiona el {descripcion}";
                lblDetalle.Text = "Presiona y suelta rápidamente";

                int botonDetectado = await EsperarPulsacionAsync();
                
                if (botonDetectado == -1) // Timeout o cancelado
                {
                    if (!_mapeando) return;
                    lblInstruccion.Text = "Tiempo agotado";
                    lblDetalle.Text = "No se detectó la pulsación. Cancelando mapeo...";
                    await Task.Delay(2000);
                    Close();
                    return;
                }

                _configActual.Botones[clave] = botonDetectado;
                
                lblInstruccion.Text = "¡Guardado!";
                lblDetalle.Text = "";
                await Task.Delay(500);
            }

            // 3. Mapear Ejes (Palancas)
            var ejes = new (string clave, string instruccion)[]
            {
                ("LeftX", "Mueve palanca IZQUIERDA hacia la DERECHA"),
                ("LeftY", "Mueve palanca IZQUIERDA hacia ABAJO"),
                ("RightX", "Mueve palanca DERECHA hacia la DERECHA"),
                ("RightY", "Mueve palanca DERECHA hacia ABAJO")
            };

            foreach (var (clave, instruccion) in ejes)
            {
                if (!_mapeando) return;

                lblEmoji.Text = "🕹️";
                lblInstruccion.Text = instruccion;
                lblDetalle.Text = "Mueve y luego suelta la palanca";

                string? ejeDetectado = await EsperarEjeAsync();
                if (ejeDetectado == null)
                {
                    if (!_mapeando) return;
                    lblInstruccion.Text = "Tiempo agotado";
                    lblDetalle.Text = "Cancelando mapeo...";
                    await Task.Delay(2000);
                    Close();
                    return;
                }

                _configActual.Ejes[clave] = ejeDetectado;
                lblInstruccion.Text = "¡Registrado!";
                lblDetalle.Text = "Suelta la palanca...";
                await EsperarSoltarEjeAsync(ejeDetectado);
                await Task.Delay(400);
            }

            // 3.5. Mapear Gatillos Inferiores (LT/RT - Soporte analógico y digital)
            var gatillos = new (string clave, string instruccion)[]
            {
                ("LT", "Presiona a fondo el Gatillo IZQUIERDO (LT/L2)"),
                ("RT", "Presiona a fondo el Gatillo DERECHO (RT/R2)")
            };

            foreach (var (clave, instruccion) in gatillos)
            {
                if (!_mapeando) return;

                lblEmoji.Text = "🔽";
                lblInstruccion.Text = instruccion;
                lblDetalle.Text = "Presiona a fondo y suelta (analógico o botón)";

                var (esBoton, botonID, ejeDetectado) = await EsperarBotonOEjeAsync();
                if (!esBoton && ejeDetectado == null)
                {
                    if (!_mapeando) return;
                    lblInstruccion.Text = "Tiempo agotado";
                    lblDetalle.Text = "Cancelando mapeo...";
                    await Task.Delay(2000);
                    Close();
                    return;
                }

                if (esBoton) 
                    _configActual.Botones[clave] = botonID;
                else 
                    _configActual.Ejes[clave] = ejeDetectado!;

                lblInstruccion.Text = "¡Registrado!";
                lblDetalle.Text = "Suelta el gatillo...";
                
                if (!esBoton) await EsperarSoltarEjeAsync(ejeDetectado!);
                await Task.Delay(400);
            }

            // 4. Finalización y Guardado
            GuardarConfiguracion();

            lblEmoji.Text = "✅";
            lblInstruccion.Text = "¡Mapeo Completado!";
            lblInstruccion.Foreground = System.Windows.Media.Brushes.SpringGreen;
            lblDetalle.Text = "Tu mando está configurado para la emulación.";
            btnCancelar.Content = "CERRAR";

            await Task.Delay(3000);
            if (_mapeando) Close();
        }

        private async Task<Joystick?> DetectarMandoAsync()
        {
            // OBTENEMOS EL HANDLE EN EL HILO PRINCIPAL (Evita el crasheo de seguridad de WPF)
            IntPtr windowHandle = new WindowInteropHelper(this).Handle;

            return await Task.Run(() =>
            {
                for (int i = 0; i < 30 && _mapeando; i++) // 3 segundos de timeout
                {
                    var dispositivos = _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
                    if (dispositivos.Count > 0)
                    {
                        var js = new Joystick(_directInput, dispositivos[0].InstanceGuid);
                        js.Properties.BufferSize = 128;
                        // USAMOS LA VARIABLE windowHandle EN LUGAR DE 'this'
                        js.SetCooperativeLevel(windowHandle, CooperativeLevel.NonExclusive | CooperativeLevel.Background);
                        js.Acquire();
                        return js;
                    }
                    Thread.Sleep(100);
                }
                return null;
            });
        }

        private async Task<int> EsperarPulsacionAsync()
        {
            return await Task.Run(() =>
            {
                int limiteTimeOut = 10000; // 10 segundos para presionar
                int transcurrido = 0;

                while (transcurrido < limiteTimeOut && _mapeando && _joystick != null)
                {
                    try
                    {
                        _joystick.Poll();
                        var estado = _joystick.GetCurrentState();
                        for (int i = 0; i < estado.Buttons.Length; i++)
                        {
                            if (estado.Buttons[i])
                            {
                                // Esperar a que lo suelte para evitar dobles lecturas
                                while (_joystick.GetCurrentState().Buttons[i] && _mapeando) { _joystick.Poll(); Thread.Sleep(10); }
                                return i;
                            }
                        }
                    }
                    catch { }
                    Thread.Sleep(16);
                    transcurrido += 16;
                }
                return -1;
            });
        }

        private async Task<string?> EsperarEjeAsync()
        {
            return await Task.Run(() =>
            {
                if (_joystick == null) return null;
                _joystick.Poll();
                var inicial = _joystick.GetCurrentState();
                int limite = 12000;
                int transcurrido = 0;

                while (transcurrido < 10000 && _mapeando)
                {
                    _joystick.Poll();
                    var st = _joystick.GetCurrentState();

                    if (Math.Abs(st.X - inicial.X) > limite) return "X";
                    if (Math.Abs(st.Y - inicial.Y) > limite) return "Y";
                    if (Math.Abs(st.Z - inicial.Z) > limite) return "Z";
                    if (Math.Abs(st.RotationX - inicial.RotationX) > limite) return "RotationX";
                    if (Math.Abs(st.RotationY - inicial.RotationY) > limite) return "RotationY";
                    if (Math.Abs(st.RotationZ - inicial.RotationZ) > limite) return "RotationZ";
                    if (st.Sliders.Length > 0 && Math.Abs(st.Sliders[0] - inicial.Sliders[0]) > limite) return "Slider0";
                    if (st.Sliders.Length > 1 && Math.Abs(st.Sliders[1] - inicial.Sliders[1]) > limite) return "Slider1";

                    Thread.Sleep(16);
                    transcurrido += 16;
                }
                return null;
            });
        }

        private async Task<(bool EsBoton, int BotonID, string? Eje)> EsperarBotonOEjeAsync()
        {
            return await Task.Run(() =>
            {
                if (_joystick == null) return (false, -1, null);
                _joystick.Poll();
                var inicial = _joystick.GetCurrentState();
                int limiteEje = 12000;
                int transcurrido = 0;

                while (transcurrido < 10000 && _mapeando)
                {
                    _joystick.Poll();
                    var st = _joystick.GetCurrentState();

                    // Check axes
                    if (Math.Abs(st.X - inicial.X) > limiteEje) return (false, -1, "X");
                    if (Math.Abs(st.Y - inicial.Y) > limiteEje) return (false, -1, "Y");
                    if (Math.Abs(st.Z - inicial.Z) > limiteEje) return (false, -1, "Z");
                    if (Math.Abs(st.RotationX - inicial.RotationX) > limiteEje) return (false, -1, "RotationX");
                    if (Math.Abs(st.RotationY - inicial.RotationY) > limiteEje) return (false, -1, "RotationY");
                    if (Math.Abs(st.RotationZ - inicial.RotationZ) > limiteEje) return (false, -1, "RotationZ");
                    if (st.Sliders.Length > 0 && Math.Abs(st.Sliders[0] - inicial.Sliders[0]) > limiteEje) return (false, -1, "Slider0");
                    if (st.Sliders.Length > 1 && Math.Abs(st.Sliders[1] - inicial.Sliders[1]) > limiteEje) return (false, -1, "Slider1");

                    // Check buttons
                    for (int i = 0; i < st.Buttons.Length; i++)
                    {
                        if (st.Buttons[i])
                        {
                            while (_joystick.GetCurrentState().Buttons[i] && _mapeando) { _joystick.Poll(); Thread.Sleep(10); }
                            return (true, i, null);
                        }
                    }

                    Thread.Sleep(16);
                    transcurrido += 16;
                }
                return (false, -1, null);
            });
        }

        private async Task EsperarSoltarEjeAsync(string eje)
        {
            await Task.Run(() =>
            {
                if (_joystick == null) return;
                _joystick.Poll();
                var inicial = _joystick.GetCurrentState();
                while (_mapeando)
                {
                    _joystick.Poll();
                    var st = _joystick.GetCurrentState();
                    if (Math.Abs(JoystickHelper.ObtenerValorEje(st, eje) - JoystickHelper.ObtenerValorEje(inicial, eje)) < 5000) break;
                    Thread.Sleep(16);
                }
            });
        }

        private void GuardarConfiguracion()
        {
            try
            {
                if (!Directory.Exists(AppPaths.RaizDatos)) Directory.CreateDirectory(AppPaths.RaizDatos);
                File.WriteAllText(AppPaths.MapeoConfig, JsonSerializer.Serialize(_configActual, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Logger.Log($"Error al guardar configuración de mapeo: {ex.Message}"); }
        }
    }
}