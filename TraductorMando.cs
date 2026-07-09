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
using SteamOSConfigurator.Helpers;

namespace SteamOSConfigurator
{
    public static class TraductorMando
    {
        private static CancellationTokenSource? _cts;
        private static ViGEmClient? _vigemClient;
        private static IXbox360Controller? _xboxVirtual;
        private static DirectInput? _directInput;
        private static Joystick? _joystick;
        private static HidStream? _hidRumbleStream; 
        private static readonly List<string> _rutasOcultadas = new();
        private static int _tiempoChordMs = 65; // Valor por defecto

        public static async Task IniciarAsync()
        {
            if (_cts != null) return;

            // ── NUEVO: LEER EL DELAY DINÁMICO DESDE EL JSON PRINCIPAL ──
            string rutaConfigPrincipal = AppPaths.Config;
            if (File.Exists(rutaConfigPrincipal))
            {
                try {
                    var jsonNode = JsonNode.Parse(File.ReadAllText(rutaConfigPrincipal));
                    if (jsonNode?["DelayBotonHome"] != null)
                        _tiempoChordMs = jsonNode["DelayBotonHome"]!.GetValue<int>();
                } catch (Exception ex) { Logger.Log($"Error al leer DelayBotonHome: {ex.Message}"); }
            }

            string rutaMapeo = AppPaths.MapeoConfig;
            if (!File.Exists(rutaMapeo)) return; 

            MapeoControl? config;
            try { config = JsonSerializer.Deserialize<MapeoControl>(File.ReadAllText(rutaMapeo)); }
            catch { return; }

            if (config == null || config.Botones.Count == 0) return;

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _directInput = new DirectInput();

            await Task.Run(() =>
            {
                PrepararMandoFisico(config);
                ConectarJoystick(token);
                if (_joystick == null) { Detener(); return; }

                try
                {
                    _vigemClient = new ViGEmClient();
                    _xboxVirtual = _vigemClient.CreateXbox360Controller();
                    _xboxVirtual.FeedbackReceived += (_, e) => EnviarRumble(e.LargeMotor, e.SmallMotor); 
                    _xboxVirtual.Connect();
                }
                catch { Detener(); return; }

                BucleTraduccion(config, token);
            });
        }

        public static void Detener()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            try { _xboxVirtual?.Disconnect(); } catch (Exception ex) { Logger.Log($"Error disconnecting Xbox controller: {ex.Message}"); }
            try { _vigemClient?.Dispose(); } catch (Exception ex) { Logger.Log($"Error disposing ViGEmClient: {ex.Message}"); }
            try { _joystick?.Unacquire(); _joystick?.Dispose(); } catch (Exception ex) { Logger.Log($"Error disposing Joystick: {ex.Message}"); }
            try { _directInput?.Dispose(); } catch (Exception ex) { Logger.Log($"Error disposing DirectInput: {ex.Message}"); }
            try { _hidRumbleStream?.Dispose(); _hidRumbleStream = null; } catch (Exception ex) { Logger.Log($"Error disposing RumbleStream: {ex.Message}"); } 
            RevertirOcultamiento();
        }

        private static void ConectarJoystick(CancellationToken token)
        {
            for (int i = 0; i < 5 && !token.IsCancellationRequested; i++)
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

        private static void BucleTraduccion(MapeoControl config, CancellationToken token)
        {
            long tickSelectPresionado = 0;
            bool selectBloqueadoPorChord = false;

            while (!token.IsCancellationRequested && _joystick != null && _xboxVirtual != null)
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

                    byte ltValue = 0;
                    if (config.Botones.ContainsKey("LT")) ltValue = (byte)(st.Buttons[config.Botones["LT"]] ? 255 : 0);
                    else if (config.Ejes.ContainsKey("LT")) ltValue = (byte)Math.Clamp((JoystickHelper.ObtenerValorEje(st, config.Ejes["LT"]) * 255) / 65535, 0, 255);

                    byte rtValue = 0;
                    if (config.Botones.ContainsKey("RT")) rtValue = (byte)(st.Buttons[config.Botones["RT"]] ? 255 : 0);
                    else if (config.Ejes.ContainsKey("RT")) rtValue = (byte)Math.Clamp((JoystickHelper.ObtenerValorEje(st, config.Ejes["RT"]) * 255) / 65535, 0, 255);

                    _xboxVirtual.SetSliderValue(Xbox360Slider.LeftTrigger, ltValue);
                    _xboxVirtual.SetSliderValue(Xbox360Slider.RightTrigger, rtValue);

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

                    int lx = Deadzone(JoystickHelper.ObtenerValorEje(st, config.Ejes["LeftX"]) - 32768);
                    int ly = Deadzone(65535 - JoystickHelper.ObtenerValorEje(st, config.Ejes["LeftY"]) - 32768);
                    int rx = Deadzone(JoystickHelper.ObtenerValorEje(st, config.Ejes["RightX"]) - 32768);
                    int ry = Deadzone(65535 - JoystickHelper.ObtenerValorEje(st, config.Ejes["RightY"]) - 32768);

                    _xboxVirtual.SetAxisValue(Xbox360Axis.LeftThumbX, (short)Math.Clamp(lx, -32768, 32767));
                    _xboxVirtual.SetAxisValue(Xbox360Axis.LeftThumbY, (short)Math.Clamp(ly, -32768, 32767));
                    _xboxVirtual.SetAxisValue(Xbox360Axis.RightThumbX, (short)Math.Clamp(rx, -32768, 32767));
                    _xboxVirtual.SetAxisValue(Xbox360Axis.RightThumbY, (short)Math.Clamp(ry, -32768, 32767));

                    Thread.Sleep(10);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error en BucleTraduccion: {ex.Message}");
                    Thread.Sleep(2000);
                    ConectarJoystick(token);
                }
            }
        }

        private static int Deadzone(int v, int zona = 4000) => Math.Abs(v) < zona ? 0 : v;

        private static void PrepararMandoFisico(MapeoControl config)
        {
            try
            {
                var hidHide = new HidHideControlService();
                string exePath = Environment.ProcessPath ?? "";
                if (!hidHide.ApplicationPaths.Contains(exePath, StringComparer.OrdinalIgnoreCase)) hidHide.AddApplicationPath(exePath);
                hidHide.IsActive = true;

                int vendorId = config.VendorID != 0 ? config.VendorID : 0x0583;
                int productId = config.ProductID != 0 ? config.ProductID : 0xA009;

                var devs = DeviceList.Local.GetHidDevices().Where(d => d.VendorID == vendorId && d.ProductID == productId).ToList();
                foreach (var dev in devs)
                {
                    string devicePath = dev.DevicePath.Replace('#', '\\').ToUpperInvariant();
                    try { hidHide.AddBlockedInstanceId(devicePath); _rutasOcultadas.Add(devicePath); } catch (Exception ex) { Logger.Log($"Error ocultando dispositivo {devicePath}: {ex.Message}"); }

                    if (_hidRumbleStream == null && dev.TryOpen(out var stream))
                    {
                        if (dev.GetMaxOutputReportLength() > 0) _hidRumbleStream = stream;
                        else stream.Dispose();
                    }
                }
            }
            catch (Exception ex) { Logger.Log($"Error al preparar mando físico: {ex.Message}"); }
        }

        private static void EnviarRumble(byte grande, byte pequeno)
        {
            if (_hidRumbleStream == null) return;
            try { _hidRumbleStream.Write(new byte[] { 0x00, grande, pequeno, 0x00 }); }
            catch (Exception ex) { Logger.Log($"Error al enviar Rumble: {ex.Message}"); }
        }

        private static void RevertirOcultamiento()
        {
            if (_rutasOcultadas.Count == 0) return;
            try
            {
                var hidHide = new HidHideControlService();
                foreach (var ruta in _rutasOcultadas) try { hidHide.RemoveBlockedInstanceId(ruta); } catch (Exception ex) { Logger.Log($"Error revirtiendo ocultamiento para {ruta}: {ex.Message}"); }
                _rutasOcultadas.Clear();
            }
            catch (Exception ex) { Logger.Log($"Error revirtiendo ocultamiento general: {ex.Message}"); }
        }
    }
}