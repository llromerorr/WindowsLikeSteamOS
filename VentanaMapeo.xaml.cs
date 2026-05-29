using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Interop;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SharpDX.DirectInput;

namespace SteamOSConfigurator
{
    // Estructura pública para la configuración del mando
    public class MapeoControl
    {
        public string NombreControl { get; set; } = string.Empty;
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
                ("LT", "Gatillo inferior Izquierdo (LT/L2)", "🔽"),
                ("RT", "Gatillo inferior Derecho (RT/R2)", "🔽"),
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
                    if (Math.Abs(ObtenerValorEje(st, eje) - ObtenerValorEje(inicial, eje)) < 5000) break;
                    Thread.Sleep(16);
                }
            });
        }

        private int ObtenerValorEje(JoystickState st, string eje) => eje switch
        {
            "X" => st.X, "Y" => st.Y, "Z" => st.Z,
            "RotationX" => st.RotationX, "RotationY" => st.RotationY, "RotationZ" => st.RotationZ,
            "Slider0" => st.Sliders.Length > 0 ? st.Sliders[0] : 32767,
            "Slider1" => st.Sliders.Length > 1 ? st.Sliders[1] : 32767,
            _ => 32767
        };

        private void GuardarConfiguracion()
        {
            try
            {
                string carpeta = @"C:\ProgramData\SteamOS";
                if (!Directory.Exists(carpeta)) Directory.CreateDirectory(carpeta);
                string ruta = Path.Combine(carpeta, "mapeo_config.json");
                File.WriteAllText(ruta, JsonSerializer.Serialize(_configActual, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}