using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using SharpDX.DirectInput;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nefarius.Drivers.HidHide;
using HidSharp;
using SteamOSConfigurator.Helpers;
using SteamOSConfigurator.Services;
using WindowsInput;
using WindowsInput.Native;

namespace SteamOSConfigurator
{
    public static class TraductorMando
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        private static IntPtr _dummyHwnd = IntPtr.Zero;


        private static CancellationTokenSource? _cts;
        private static ViGEmClient? _vigemClient;
        private static IXbox360Controller? _xboxVirtual;
        private static DirectInput? _directInput;
        private static Joystick? _joystick;
        private static Effect? _rumbleEffect;
        private static EffectParameters? _rumbleParams;
        private static ConstantForce? _constantForce;
        private static readonly List<string> _rutasOcultadas = new();
        private static readonly InputSimulator _inputSimulator = new();
        public static Func<bool>? EsJuegoEnPrimerPlano;
        private static int _tiempoChordMs = 100; // Ventana de gracia para acordes humanos

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
                        _tiempoChordMs = Math.Max(80, jsonNode["DelayBotonHome"]!.GetValue<int>());
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
            try { _rumbleEffect?.Stop(); _rumbleEffect?.Dispose(); _rumbleEffect = null; } catch (Exception ex) { Logger.Log($"Error disposing RumbleEffect: {ex.Message}"); }
            try { _joystick?.Unacquire(); _joystick?.Dispose(); } catch (Exception ex) { Logger.Log($"Error disposing Joystick: {ex.Message}"); }
            try { _directInput?.Dispose(); } catch (Exception ex) { Logger.Log($"Error disposing DirectInput: {ex.Message}"); }
            if (_dummyHwnd != IntPtr.Zero)
            {
                try { DestroyWindow(_dummyHwnd); } catch { }
                _dummyHwnd = IntPtr.Zero;
            }
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
                    try
                    {
                        if (_dummyHwnd == IntPtr.Zero)
                        {
                            _dummyHwnd = CreateWindowEx(0, "STATIC", "SteamOS_DI_Host", 0x08000000 /* WS_DISABLED */, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        }
                        _joystick.SetCooperativeLevel(_dummyHwnd, CooperativeLevel.Background | CooperativeLevel.Exclusive);
                        Logger.Log($"[ConectarJoystick] SetCooperativeLevel Background + Exclusive aplicado con HWND=0x{_dummyHwnd.ToInt64():X}.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ConectarJoystick] Error al configurar CooperativeLevel: {ex.Message}");
                    }
                    _joystick.Acquire();
                    Logger.Log("[ConectarJoystick] Joystick conectado y adquirido en modo Background + Exclusive.");

                    // Inicializar el efecto de vibración dual nativo (Controlpanel Force)
                    try
                    {
                        var controlPanelGuid = new Guid("f71ec2ed-e1e4-4ac6-bda6-89f892d3800d");
                        _rumbleParams = new EffectParameters
                        {
                            Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                            Duration = 10000000,
                            SamplePeriod = 0,
                            Gain = 10000,
                            TriggerButton = -1,
                            TriggerRepeatInterval = 0,
                            Axes = new int[] { 0, 4 },
                            Directions = new int[] { 0, 100 }
                        };
                        _constantForce = new ConstantForce { Magnitude = 0 };
                        _rumbleParams.Parameters = _constantForce;
                        _rumbleEffect = new Effect(_joystick, controlPanelGuid, _rumbleParams);
                        Logger.Log("[ConectarJoystick] Efecto de vibración dual inicializado con éxito.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ConectarJoystick] No se pudo inicializar efecto de vibración dual: {ex.Message}");
                    }

                    return;
                }
                Thread.Sleep(1000);
            }
        }

        private static void BucleTraduccion(MapeoControl config, CancellationToken token)
        {
            long tickSelectPresionado = 0;
            long tickStartPresionado = 0;
            long tickInicioPulsoGuide = 0;
            bool chordGuideActivo = false;
            bool chordBloqueadoHastaSoltar = false;

            while (!token.IsCancellationRequested && _joystick != null && _xboxVirtual != null)
            {
                try
                {
                    _joystick.Poll();
                    var st = _joystick.GetCurrentState();

                    // ── GAMEPLAY / TRADUCCIÓN NORMAL PARA STEAM Y JUEGOS ──
                    bool btnA = st.Buttons[config.Botones["A"]];
                    bool btnB = st.Buttons[config.Botones["B"]];
                    bool btnX = st.Buttons[config.Botones["X"]];
                    bool btnY = st.Buttons[config.Botones["Y"]];
                    bool btnLB = st.Buttons[config.Botones["LB"]];
                    bool btnRB = st.Buttons[config.Botones["RB"]];
                    bool btnL3 = st.Buttons[config.Botones["L3"]];
                    bool btnR3 = st.Buttons[config.Botones["R3"]];
                    bool btnSelect = st.Buttons[config.Botones["Select"]];
                    bool btnStart = st.Buttons[config.Botones["Start"]];

                    _xboxVirtual.SetButtonState(Xbox360Button.A, btnA);
                    _xboxVirtual.SetButtonState(Xbox360Button.B, btnB);
                    _xboxVirtual.SetButtonState(Xbox360Button.X, btnX);
                    _xboxVirtual.SetButtonState(Xbox360Button.Y, btnY);
                    _xboxVirtual.SetButtonState(Xbox360Button.LeftShoulder, btnLB);
                    _xboxVirtual.SetButtonState(Xbox360Button.RightShoulder, btnRB);
                    _xboxVirtual.SetButtonState(Xbox360Button.LeftThumb, btnL3);
                    _xboxVirtual.SetButtonState(Xbox360Button.RightThumb, btnR3);

                    byte ltValue = 0;
                    if (config.Botones.ContainsKey("LT")) ltValue = (byte)(st.Buttons[config.Botones["LT"]] ? 255 : 0);
                    else if (config.Ejes.ContainsKey("LT")) ltValue = (byte)Math.Clamp((JoystickHelper.ObtenerValorEje(st, config.Ejes["LT"]) * 255) / 65535, 0, 255);

                    byte rtValue = 0;
                    if (config.Botones.ContainsKey("RT")) rtValue = (byte)(st.Buttons[config.Botones["RT"]] ? 255 : 0);
                    else if (config.Ejes.ContainsKey("RT")) rtValue = (byte)Math.Clamp((JoystickHelper.ObtenerValorEje(st, config.Ejes["RT"]) * 255) / 65535, 0, 255);

                    _xboxVirtual.SetSliderValue(Xbox360Slider.LeftTrigger, ltValue);
                    _xboxVirtual.SetSliderValue(Xbox360Slider.RightTrigger, rtValue);

                    long now = Environment.TickCount64;

                    // ── DETECCIÓN ROBUSTA DEL BOTÓN HOME/GUIDE (CHORD SELECT + START) ──
                    if (btnSelect && btnStart)
                    {
                        if (!chordBloqueadoHastaSoltar && !chordGuideActivo)
                        {
                            chordGuideActivo = true;
                            chordBloqueadoHastaSoltar = true;
                            tickInicioPulsoGuide = now;

                            // Suprimir de inmediato cualquier Back o Start pendiente
                            _xboxVirtual.SetButtonState(Xbox360Button.Back, false);
                            _xboxVirtual.SetButtonState(Xbox360Button.Start, false);
                            tickSelectPresionado = 0;
                            tickStartPresionado = 0;

                            // Activar botón Guide virtual en Xbox 360
                            _xboxVirtual.SetButtonState(Xbox360Button.Guide, true);
                            Logger.Log("[TraductorMando] ¡Chord Select+Start detectado! Iniciando pulso de Guide (120ms)...");

                            // Si estamos en la interfaz de Steam (sin juego activo), enviar atajo nativo GamepadUI (Ctrl+1)
                            if (EsJuegoEnPrimerPlano == null || !EsJuegoEnPrimerPlano())
                            {
                                try
                                {
                                    _inputSimulator.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_1);
                                    Logger.Log("[TraductorMando] Atajo Steam GamepadUI Ctrl+1 inyectado exitosamente.");
                                }
                                catch (Exception ex)
                                {
                                    Logger.Log($"[TraductorMando] Advertencia al inyectar Ctrl+1: {ex.Message}");
                                }
                            }
                        }
                    }

                    // Gestión del pulso discreto de Guide (duración fija de 120ms sin importar cuánto tiempo mantenga el usuario)
                    if (chordGuideActivo)
                    {
                        if (now - tickInicioPulsoGuide >= 120)
                        {
                            _xboxVirtual.SetButtonState(Xbox360Button.Guide, false);
                            chordGuideActivo = false;
                            Logger.Log("[TraductorMando] Pulso de Guide finalizado (Guide=false).");
                        }
                    }
                    else
                    {
                        _xboxVirtual.SetButtonState(Xbox360Button.Guide, false);
                    }

                    // Desbloqueo del Chord únicamente cuando ambos botones son soltados por completo
                    if (!btnSelect && !btnStart)
                    {
                        chordBloqueadoHastaSoltar = false;
                    }

                    // Manejo individual de Select (Back)
                    if (btnSelect)
                    {
                        if (!chordBloqueadoHastaSoltar && !chordGuideActivo)
                        {
                            if (tickSelectPresionado == 0) tickSelectPresionado = now;
                            bool enviarBack = (now - tickSelectPresionado > _tiempoChordMs);
                            _xboxVirtual.SetButtonState(Xbox360Button.Back, enviarBack);
                        }
                        else
                        {
                            _xboxVirtual.SetButtonState(Xbox360Button.Back, false);
                        }
                    }
                    else
                    {
                        _xboxVirtual.SetButtonState(Xbox360Button.Back, false);
                        tickSelectPresionado = 0;
                    }

                    // Manejo individual de Start
                    if (btnStart)
                    {
                        if (!chordBloqueadoHastaSoltar && !chordGuideActivo)
                        {
                            if (tickStartPresionado == 0) tickStartPresionado = now;
                            bool enviarStart = (now - tickStartPresionado > _tiempoChordMs);
                            _xboxVirtual.SetButtonState(Xbox360Button.Start, enviarStart);
                        }
                        else
                        {
                            _xboxVirtual.SetButtonState(Xbox360Button.Start, false);
                        }
                    }
                    else
                    {
                        _xboxVirtual.SetButtonState(Xbox360Button.Start, false);
                        tickStartPresionado = 0;
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
            if (_rumbleEffect == null || _rumbleParams == null || _constantForce == null) return;
            try
            {
                if (largeMotor == 0 && smallMotor == 0)
                {
                    _rumbleEffect.Stop();
                    return;
                }

                // Dirección empírica comprobada para DirectInput Controlpanel Force:
                // -1000 = Motor izquierdo (pesado/frecuencia baja)
                // +1000 = Motor derecho (ligero/frecuencia alta)
                // 0 = Ambos motores simultáneos
                int dirX = (largeMotor > 0 && smallMotor > 0) ? 0 : (largeMotor > 0 ? -1000 : 1000);
                int magnitude = (largeMotor > 0 && smallMotor > 0)
                    ? (int)(Math.Max(largeMotor, smallMotor) / 255.0 * 10000)
                    : (largeMotor > 0 ? (int)(largeMotor / 255.0 * 10000) : (int)(smallMotor / 255.0 * 10000));

                _rumbleParams.Directions = new int[] { dirX, 100 };
                _constantForce.Magnitude = Math.Clamp(magnitude, 0, 10000);
                _rumbleParams.Parameters = _constantForce;
                _rumbleEffect.SetParameters(_rumbleParams, EffectParameterFlags.Direction | EffectParameterFlags.TypeSpecificParameters);
                _rumbleEffect.Start(1, EffectPlayFlags.NoDownload);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error en EnviarRumble: {ex.Message}");
            }
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
                    string rawPath = dev.DevicePath;
                    if (rawPath.StartsWith(@"\\?\")) rawPath = rawPath.Substring(4);
                    int guidIndex = rawPath.IndexOf("#{");
                    if (guidIndex > 0) rawPath = rawPath.Substring(0, guidIndex);
                    
                    string instanceId = rawPath.Replace('#', '\\').ToUpperInvariant();
                    
                    try
                    {
                        hidHide.AddBlockedInstanceId(instanceId);
                        _rutasOcultadas.Add(instanceId);
                    }
                    catch (Exception ex) { Logger.Log($"Error ocultando dispositivo {instanceId}: {ex.Message}"); }

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