using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using SharpDX.DirectInput;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nefarius.Drivers.HidHide;
using HidSharp;

namespace SteamOSConfigurator
{
    public static class TraductorMando
    {
        private static bool _ejecutando = false;
        private static ViGEmClient? _vigemClient;
        private static IXbox360Controller? _xboxVirtual;
        private static DirectInput? _directInput;
        private static Joystick? _joystick;
        private static HidStream? _hidRumbleStream; 
        private static readonly List<string> _rutasOcultadas = new();
        private static int _tiempoChordMs = 65; // Valor por defecto

        public static async Task IniciarAsync()
        {
            if (_ejecutando) return;

            // ── NUEVO: LEER EL DELAY DINÁMICO DESDE EL JSON PRINCIPAL ──
            string rutaConfigPrincipal = @"C:\ProgramData\SteamOS\config.json";
            if (File.Exists(rutaConfigPrincipal))
            {
                try {
                    var jsonNode = JsonNode.Parse(File.ReadAllText(rutaConfigPrincipal));
                    if (jsonNode?["DelayBotonHome"] != null)
                        _tiempoChordMs = jsonNode["DelayBotonHome"]!.GetValue<int>();
                } catch { }
            }

            string rutaMapeo = @"C:\ProgramData\SteamOS\mapeo_config.json";
            if (!File.Exists(rutaMapeo)) return; 

            MapeoControl? config;
            try { config = JsonSerializer.Deserialize<MapeoControl>(File.ReadAllText(rutaMapeo)); }
            catch { return; }

            if (config == null || config.Botones.Count == 0) return;

            _ejecutando = true;
            _directInput = new DirectInput();

            await Task.Run(() =>
            {
                PrepararMandoFisico();
                ConectarJoystick();
                if (_joystick == null) { Detener(); return; }

                try
                {
                    _vigemClient = new ViGEmClient();
                    _xboxVirtual = _vigemClient.CreateXbox360Controller();
                    _xboxVirtual.FeedbackReceived += (_, e) => EnviarRumble(e.LargeMotor, e.SmallMotor); 
                    _xboxVirtual.Connect();
                }
                catch { Detener(); return; }

                BucleTraduccion(config);
            });
        }

        public static void Detener()
        {
            _ejecutando = false;
            try { _xboxVirtual?.Disconnect(); } catch { }
            try { _vigemClient?.Dispose(); } catch { }
            try { _joystick?.Unacquire(); _joystick?.Dispose(); } catch { }
            try { _directInput?.Dispose(); } catch { }
            try { _hidRumbleStream?.Dispose(); _hidRumbleStream = null; } catch { } 
            RevertirOcultamiento();
        }

        private static void ConectarJoystick()
        {
            for (int i = 0; i < 5; i++)
            {
                var dispositivos = _directInput!.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
                if (dispositivos.Count > 0)
                {
                    _joystick = new Joystick(_directInput, dispositivos[0].InstanceGuid);
                    _joystick.Properties.BufferSize = 128;
                    _joystick.Acquire();
                    return;
                }
                Thread.Sleep(1000);
            }
        }

        private static void BucleTraduccion(MapeoControl config)
        {
            long tickSelectPresionado = 0;
            bool selectBloqueadoPorChord = false;

            while (_ejecutando && _joystick != null && _xboxVirtual != null)
            {
                try
                {
                    _joystick.Poll();
                    var st = _joystick.GetCurrentState();

                    _xboxVirtual.SetButtonState(Xbox360Button.A, st.Buttons[config.Botones["A"]]);
                    _xboxVirtual.SetButtonState(Xbox360Button.B, st.Buttons[config.Botones["B"]]);
                    _xboxVirtual.SetButtonState(Xbox360Button.X, st.Buttons[config.Botones["X"]]);
                    _xboxVirtual.SetButtonState(Xbox360Button.Y, st.Buttons[config.Botones["Y"]]);
                    _xboxVirtual.SetButtonState(Xbox360Button.LeftShoulder, st.Buttons[config.Botones["LB"]]);
                    _xboxVirtual.SetButtonState(Xbox360Button.RightShoulder, st.Buttons[config.Botones["RB"]]);
                    _xboxVirtual.SetButtonState(Xbox360Button.LeftThumb, st.Buttons[config.Botones["L3"]]);
                    _xboxVirtual.SetButtonState(Xbox360Button.RightThumb, st.Buttons[config.Botones["R3"]]);

                    _xboxVirtual.SetSliderValue(Xbox360Slider.LeftTrigger, (byte)(st.Buttons[config.Botones["LT"]] ? 255 : 0));
                    _xboxVirtual.SetSliderValue(Xbox360Slider.RightTrigger, (byte)(st.Buttons[config.Botones["RT"]] ? 255 : 0));

                    // ── APLICANDO EL DELAY DINÁMICO DE LA UI ──
                    bool btnSelect = st.Buttons[config.Botones["Select"]];
                    bool btnStart = st.Buttons[config.Botones["Start"]];

                    if (btnSelect && btnStart)
                    {
                        _xboxVirtual.SetButtonState(Xbox360Button.Guide, true);
                        _xboxVirtual.SetButtonState(Xbox360Button.Back, false);
                        _xboxVirtual.SetButtonState(Xbox360Button.Start, false);
                        selectBloqueadoPorChord = true;
                    }
                    else
                    {
                        _xboxVirtual.SetButtonState(Xbox360Button.Guide, false);
                        _xboxVirtual.SetButtonState(Xbox360Button.Start, btnStart);

                        if (btnSelect)
                        {
                            if (!selectBloqueadoPorChord)
                            {
                                if (tickSelectPresionado == 0) tickSelectPresionado = Environment.TickCount64;

                                if (Environment.TickCount64 - tickSelectPresionado > _tiempoChordMs) // Usando la variable
                                {
                                    _xboxVirtual.SetButtonState(Xbox360Button.Back, true);
                                }
                            }
                        }
                        else
                        {
                            _xboxVirtual.SetButtonState(Xbox360Button.Back, false);
                            tickSelectPresionado = 0;
                            selectBloqueadoPorChord = false;
                        }
                    }

                    if (st.PointOfViewControllers.Length > 0)
                    {
                        int pov = st.PointOfViewControllers[0];
                        _xboxVirtual.SetButtonState(Xbox360Button.Up, pov == 0 || pov == 4500 || pov == 31500);
                        _xboxVirtual.SetButtonState(Xbox360Button.Right, pov == 4500 || pov == 9000 || pov == 13500);
                        _xboxVirtual.SetButtonState(Xbox360Button.Down, pov == 13500 || pov == 18000 || pov == 22500);
                        _xboxVirtual.SetButtonState(Xbox360Button.Left, pov == 22500 || pov == 27000 || pov == 31500);
                    }

                    int lx = Deadzone(ObtenerValorEje(st, config.Ejes["LeftX"]) - 32768);
                    int ly = Deadzone(65535 - ObtenerValorEje(st, config.Ejes["LeftY"]) - 32768);
                    int rx = Deadzone(ObtenerValorEje(st, config.Ejes["RightX"]) - 32768);
                    int ry = Deadzone(65535 - ObtenerValorEje(st, config.Ejes["RightY"]) - 32768);

                    _xboxVirtual.SetAxisValue(Xbox360Axis.LeftThumbX, (short)Math.Clamp(lx, -32768, 32767));
                    _xboxVirtual.SetAxisValue(Xbox360Axis.LeftThumbY, (short)Math.Clamp(ly, -32768, 32767));
                    _xboxVirtual.SetAxisValue(Xbox360Axis.RightThumbX, (short)Math.Clamp(rx, -32768, 32767));
                    _xboxVirtual.SetAxisValue(Xbox360Axis.RightThumbY, (short)Math.Clamp(ry, -32768, 32767));

                    Thread.Sleep(10);
                }
                catch
                {
                    Thread.Sleep(2000);
                    ConectarJoystick();
                }
            }
        }

        private static int Deadzone(int v, int zona = 4000) => Math.Abs(v) < zona ? 0 : v;

        private static int ObtenerValorEje(JoystickState st, string eje) => eje switch
        {
            "X" => st.X, "Y" => st.Y, "Z" => st.Z,
            "RotationX" => st.RotationX, "RotationY" => st.RotationY, "RotationZ" => st.RotationZ,
            "Slider0" => st.Sliders.Length > 0 ? st.Sliders[0] : 32767,
            "Slider1" => st.Sliders.Length > 1 ? st.Sliders[1] : 32767,
            _ => 32767
        };

        private static void PrepararMandoFisico()
        {
            try
            {
                var hidHide = new HidHideControlService();
                string exePath = Environment.ProcessPath ?? "";
                if (!hidHide.ApplicationPaths.Contains(exePath, StringComparer.OrdinalIgnoreCase)) hidHide.AddApplicationPath(exePath);
                hidHide.IsActive = true;

                var devs = DeviceList.Local.GetHidDevices().Where(d => d.VendorID == 0x0583 && d.ProductID == 0xA009).ToList();
                foreach (var dev in devs)
                {
                    string devicePath = dev.DevicePath.Replace('#', '\\').ToUpperInvariant();
                    try { hidHide.AddBlockedInstanceId(devicePath); _rutasOcultadas.Add(devicePath); } catch { }

                    if (_hidRumbleStream == null && dev.TryOpen(out var stream))
                    {
                        if (dev.GetMaxOutputReportLength() > 0) _hidRumbleStream = stream;
                        else stream.Dispose();
                    }
                }
            }
            catch { }
        }

        private static void EnviarRumble(byte grande, byte pequeno)
        {
            if (_hidRumbleStream == null) return;
            try { _hidRumbleStream.Write(new byte[] { 0x00, grande, pequeno, 0x00 }); }
            catch { }
        }

        private static void RevertirOcultamiento()
        {
            if (_rutasOcultadas.Count == 0) return;
            try
            {
                var hidHide = new HidHideControlService();
                foreach (var ruta in _rutasOcultadas) try { hidHide.RemoveBlockedInstanceId(ruta); } catch { }
                _rutasOcultadas.Clear();
            }
            catch { }
        }
    }
}