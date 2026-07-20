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
        public static Action? OnRecoveryRequested;

        private static CancellationTokenSource? _cts;
        private static ViGEmClient? _vigemClient;
        private static IXbox360Controller? _xboxVirtual;
        private static DirectInput? _directInput;
        private static Joystick? _joystick;
        private static HidStream? _hidRumbleStream; 
        private static readonly List<string> _rutasOcultadas = new();
        private static int _tiempoChordMs = 80; // Respetando la GUI

        public static async Task IniciarAsync()
        {
            if (_cts != null) return;

            // ── LEER EL DELAY CONFIGURADO EN LA GUI ──
            string rutaConfigPrincipal = AppPaths.Config;
            if (File.Exists(rutaConfigPrincipal))
            {
                try {
                    var jsonNode = JsonNode.Parse(File.ReadAllText(rutaConfigPrincipal));
                    if (jsonNode?["DelayBotonHome"] != null)
                        _tiempoChordMs = Math.Max(10, jsonNode["DelayBotonHome"]!.GetValue<int>());
                } catch (Exception ex) { Logger.Log($"Error al leer DelayBotonHome: {ex.Message}"); }
            }

            string rutaMapeo = AppPaths.MapeoConfig;
            if (!File.Exists(rutaMapeo)) return; 

            MapeoControl? config;
            try { config = JsonSerializer.Deserialize<MapeoControl>(File.ReadAllText(rutaMapeo)); }
            catch { return; }

            if (config == null || string.IsNullOrEmpty(config.NombreControl)) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            await Task.Run(() =>
            {
                _directInput = new DirectInput();
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
            long tickStartPresionado = 0;
            long tickRecoveryChord = 0;
            long lastGuideTick = 0;

            bool esChordActivo = false;
            bool recoveryDisparado = false;

            while (!token.IsCancellationRequested && _joystick != null && _xboxVirtual != null)
            {
                try
                {
                    _joystick.Poll();
                    var st = _joystick.GetCurrentState();

                    // ── GAMEPLAY / TRADUCCIÓN NORMAL PARA STEAM Y JUEGOS ──
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

                    bool btnSelect = st.Buttons[config.Botones["Select"]];
                    bool btnStart = st.Buttons[config.Botones["Start"]];
                    bool btnLB = st.Buttons[config.Botones["LB"]];
                    bool btnRB = st.Buttons[config.Botones["RB"]];

                    // Detección de Combinación de Modo Recuperación (LB + RB + Select + Start sostenidos por 1s)
                    if (btnLB && btnRB && btnSelect && btnStart)
                    {
                        if (tickRecoveryChord == 0) tickRecoveryChord = Environment.TickCount64;
                        else if (Environment.TickCount64 - tickRecoveryChord > 1000 && !recoveryDisparado)
                        {
                            recoveryDisparado = true;
                            Logger.Log("[TraductorMando] Combinación de Recuperación detectada (LB+RB+Select+Start). Disparando evento.");
                            OnRecoveryRequested?.Invoke();
                        }
                    }
                    else
                    {
                        tickRecoveryChord = 0;
                        recoveryDisparado = false;
                    }

                    // Detección del Botón Home/Guide (Select + Start) con Latch para Atajos de Steam (Guide + Y / Guide + A)
                    if (btnSelect && btnStart)
                    {
                        lastGuideTick = Environment.TickCount64;
                        _xboxVirtual.SetButtonState(Xbox360Button.Guide, true);
                        _xboxVirtual.SetButtonState(Xbox360Button.Back, false);
                        _xboxVirtual.SetButtonState(Xbox360Button.Start, false);
                        esChordActivo = true;
                    }
                    else if (esChordActivo)
                    {
                        long now = Environment.TickCount64;
                        bool cualquierOtroBoton = st.Buttons[config.Botones["Y"]] || st.Buttons[config.Botones["X"]] ||
                                                  st.Buttons[config.Botones["A"]] || st.Buttons[config.Botones["B"]] ||
                                                  st.Buttons[config.Botones["LB"]] || st.Buttons[config.Botones["RB"]];

                        // Mantener Guide activado si se presiona Y/A/X/B o durante la ventana de gracia de 350ms
                        if (cualquierOtroBoton || (now - lastGuideTick < 350))
                        {
                            _xboxVirtual.SetButtonState(Xbox360Button.Guide, true);
                        }
                        else
                        {
                            _xboxVirtual.SetButtonState(Xbox360Button.Guide, false);
                        }

                        _xboxVirtual.SetButtonState(Xbox360Button.Back, false);
                        _xboxVirtual.SetButtonState(Xbox360Button.Start, false);

                        if (!btnSelect && !btnStart && !cualquierOtroBoton && (now - lastGuideTick >= 350))
                        {
                            esChordActivo = false;
                            tickSelectPresionado = 0;
                            tickStartPresionado = 0;
                        }
                    }
                    else
                    {
                        _xboxVirtual.SetButtonState(Xbox360Button.Guide, false);

                        long now = Environment.TickCount64;

                        if (btnSelect)
                        {
                            if (tickSelectPresionado == 0) tickSelectPresionado = now;
                            bool enviarBack = (now - tickSelectPresionado > _tiempoChordMs);
                            _xboxVirtual.SetButtonState(Xbox360Button.Back, enviarBack);
                        }
                        else
                        {
                            _xboxVirtual.SetButtonState(Xbox360Button.Back, false);
                            tickSelectPresionado = 0;
                        }

                        if (btnStart)
                        {
                            if (tickStartPresionado == 0) tickStartPresionado = now;
                            bool enviarStart = (now - tickStartPresionado > _tiempoChordMs);
                            _xboxVirtual.SetButtonState(Xbox360Button.Start, enviarStart);
                        }
                        else
                        {
                            _xboxVirtual.SetButtonState(Xbox360Button.Start, false);
                            tickStartPresionado = 0;
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

                    if (config.Ejes.ContainsKey("LeftX")) _xboxVirtual.SetAxisValue(Xbox360Axis.LeftThumbX, (short)(JoystickHelper.ObtenerValorEje(st, config.Ejes["LeftX"]) - 32768));
                    if (config.Ejes.ContainsKey("LeftY")) _xboxVirtual.SetAxisValue(Xbox360Axis.LeftThumbY, (short)(32767 - JoystickHelper.ObtenerValorEje(st, config.Ejes["LeftY"])));
                    if (config.Ejes.ContainsKey("RightX")) _xboxVirtual.SetAxisValue(Xbox360Axis.RightThumbX, (short)(JoystickHelper.ObtenerValorEje(st, config.Ejes["RightX"]) - 32768));
                    if (config.Ejes.ContainsKey("RightY")) _xboxVirtual.SetAxisValue(Xbox360Axis.RightThumbY, (short)(32767 - JoystickHelper.ObtenerValorEje(st, config.Ejes["RightY"])));
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Logger.Log($"Error en BucleTraduccion: {ex.Message}");
                        Thread.Sleep(2000);
                        ConectarJoystick(token);
                    }
                }

                Thread.Sleep(16);
            }
        }

        private static void EnviarRumble(byte largeMotor, byte smallMotor)
        {
            if (_hidRumbleStream == null) return;
            try
            {
                byte[] report = new byte[8];
                report[0] = 0x00; 
                report[1] = 0x08; 
                report[2] = 0x00;
                report[3] = smallMotor; 
                report[4] = largeMotor; 
                _hidRumbleStream.Write(report, 0, report.Length);
            }
            catch { }
        }

        private static void PrepararMandoFisico(MapeoControl config)
        {
            try
            {
                var hidHide = new HidHideControlService();
                string exePath = Environment.ProcessPath ?? string.Empty;
                if (!string.IsNullOrEmpty(exePath) && !hidHide.ApplicationPaths.Contains(exePath, StringComparer.OrdinalIgnoreCase))
                {
                    hidHide.AddApplicationPath(exePath);
                }
                hidHide.IsActive = true;

                int vendorId = config.VendorID != 0 ? config.VendorID : 0x0583;
                int productId = config.ProductID != 0 ? config.ProductID : 0xA009;

                var devs = DeviceList.Local.GetHidDevices().Where(d => d.VendorID == vendorId && d.ProductID == productId).ToList();
                foreach (var dev in devs)
                {
                    string devicePath = dev.DevicePath.Replace('#', '\\').ToUpperInvariant();
                    try
                    {
                        hidHide.AddBlockedInstanceId(devicePath);
                        _rutasOcultadas.Add(devicePath);
                    }
                    catch (Exception ex) { Logger.Log($"Error ocultando dispositivo {devicePath}: {ex.Message}"); }

                    if (_hidRumbleStream == null && dev.TryOpen(out var stream))
                    {
                        _hidRumbleStream = stream;
                    }
                }
            }
            catch (Exception ex) { Logger.Log($"Error configurando HidHide: {ex.Message}"); }
        }

        private static void RevertirOcultamiento()
        {
            try
            {
                if (_rutasOcultadas.Count == 0) return;
                var hidHide = new HidHideControlService();
                foreach (var path in _rutasOcultadas)
                {
                    try { hidHide.RemoveBlockedInstanceId(path); } catch { }
                }
                _rutasOcultadas.Clear();
            }
            catch (Exception ex) { Logger.Log($"Error revirtiendo HidHide: {ex.Message}"); }
        }
    }
}