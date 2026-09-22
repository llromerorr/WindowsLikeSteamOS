using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SharpDX.DirectInput;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
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
        public static bool EstaConectado => _joystick != null;
        private static readonly List<string> _rutasOcultadas = new();
        private static readonly InputSimulator _inputSimulator = new();
        public static Func<bool>? EsJuegoEnPrimerPlano;
        public static Action? AlReanudarSistema;

        private static int _tiempoChordMs = 100; // Ventana de gracia para acordes humanos
        private static volatile bool _suspendido = false;
        private static MapeoControl? _configActual;

        public static async Task IniciarAsync()
        {
            if (_cts != null) return;

            // ── LEER EL DELAY CONFIGURADO EN LA GUI ──
            string rutaConfigPrincipal = AppPaths.Config;
            if (File.Exists(rutaConfigPrincipal))
            {
                try
                {
                    var jsonNode = JsonNode.Parse(File.ReadAllText(rutaConfigPrincipal));
                    if (jsonNode?["DelayBotonHome"] != null)
                        _tiempoChordMs = Math.Max(50, jsonNode["DelayBotonHome"]!.GetValue<int>());
                }
                catch (Exception ex)
                {
                    Logger.Log($"[TraductorMando] Error al leer DelayBotonHome: {ex.Message}");
                }
            }

            string rutaMapeo = AppPaths.MapeoConfig;
            if (!File.Exists(rutaMapeo)) return;

            try
            {
                _configActual = JsonSerializer.Deserialize<MapeoControl>(File.ReadAllText(rutaMapeo));
            }
            catch (Exception ex)
            {
                Logger.Log($"[TraductorMando] Error al leer mapeo_config.json: {ex.Message}");
                return;
            }

            if (_configActual == null || string.IsNullOrEmpty(_configActual.NombreControl)) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // Suscripción a eventos de suspensión y reanudación de Windows
            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch (Exception ex)
            {
                Logger.Log($"[TraductorMando] Advertencia al registrar PowerModeChanged: {ex.Message}");
            }

            await Task.Run(() =>
            {
                BucleTraduccion(_configActual, token);
            });
        }

        public static void Detener()
        {
            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch { }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            LiberarXboxVirtual();
            LiberarJoystick();

            if (_dummyHwnd != IntPtr.Zero)
            {
                try { DestroyWindow(_dummyHwnd); } catch { }
                _dummyHwnd = IntPtr.Zero;
            }
            RevertirOcultamiento();
        }

        private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                Logger.Log("[TraductorMando] Suspensión del sistema detectada (Sleep). Pausando traducción y liberando dispositivos...");
                _suspendido = true;
                ResetearBotonesXbox();
                LiberarJoystick();
                LiberarXboxVirtual();
            }
            else if (e.Mode == PowerModes.Resume)
            {
                Logger.Log("[TraductorMando] Reanudación del sistema detectada (Wake). Reanudando bucle de control (200ms)...");
                Task.Run(async () =>
                {
                    await Task.Delay(200);
                    _suspendido = false;
                    Logger.Log("[TraductorMando] Bucle de traducción reactivado tras suspensión.");
                    try
                    {
                        AlReanudarSistema?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[TraductorMando] Error en callback AlReanudarSistema: {ex.Message}");
                    }
                });
            }
        }

        private static void LiberarJoystick()
        {
            try { _rumbleEffect?.Stop(); _rumbleEffect?.Dispose(); } catch { }
            _rumbleEffect = null;
            _rumbleParams = null;
            _constantForce = null;

            try { _joystick?.Unacquire(); _joystick?.Dispose(); } catch { }
            _joystick = null;

            try { _directInput?.Dispose(); } catch { }
            _directInput = null;
        }

        private static void LiberarXboxVirtual()
        {
            try { _xboxVirtual?.Disconnect(); } catch { }
            _xboxVirtual = null;

            try { _vigemClient?.Dispose(); } catch { }
            _vigemClient = null;
        }

        private static bool AsegurarXboxVirtual()
        {
            if (_xboxVirtual != null && _vigemClient != null) return true;
            try
            {
                LiberarXboxVirtual();
                _vigemClient = new ViGEmClient();
                _xboxVirtual = _vigemClient.CreateXbox360Controller();
                _xboxVirtual.FeedbackReceived += (_, e) => EnviarRumble(e.LargeMotor, e.SmallMotor);
                _xboxVirtual.Connect();
                Logger.Log("[TraductorMando] Mando virtual Xbox 360 conectado y listo en ViGEm.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[TraductorMando] Error al iniciar ViGEm Xbox virtual: {ex.Message}");
                LiberarXboxVirtual();
                return false;
            }
        }

        private static bool EsXbox360Virtual(DeviceInstance d)
        {
            string guidStr = d.ProductGuid.ToString("N");
            // El mando virtual Xbox 360 de ViGEm tiene PID=0x028E, VID=0x045E -> GUID a0090583 vs 028e045e
            if (guidStr.StartsWith("028e045e", StringComparison.OrdinalIgnoreCase)) return true;
            if (d.ProductName.Contains("Xbox", StringComparison.OrdinalIgnoreCase)) return true;
            if (d.InstanceName.Contains("Xbox", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IntentarConectarMandoFisico(MapeoControl config)
        {
            try
            {
                // 1. Asegurar que el proceso actual esté en la whitelist de HidHide
                // ANTES de que DirectInput enumere los dispositivos
                PrepararMandoFisico(config);

                _directInput ??= new DirectInput();

                var dispositivos = _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
                if (dispositivos.Count == 0)
                {
                    Logger.Log("[TraductorMando] No se detectaron dispositivos DirectInput conectados.");
                    return false;
                }

                DeviceInstance? devElegido = null;

                foreach (var dev in dispositivos)
                {
                    // Descartar mandos virtuales Xbox (ViGEm)
                    if (EsXbox360Virtual(dev)) continue;

                    string guidStr = dev.ProductGuid.ToString("N");

                    // Coincidencia por VID y PID exacto si están configurados
                    if (config.VendorID != 0 && config.ProductID != 0)
                    {
                        string pidVidEsperado = $"{config.ProductID:x4}{config.VendorID:x4}";
                        if (guidStr.StartsWith(pidVidEsperado, StringComparison.OrdinalIgnoreCase))
                        {
                            devElegido = dev;
                            break;
                        }
                    }

                    // Coincidencia por nombre de producto o instancia
                    if (!string.IsNullOrEmpty(config.NombreControl) &&
                        (dev.ProductName.Equals(config.NombreControl, StringComparison.OrdinalIgnoreCase) ||
                         dev.InstanceName.Equals(config.NombreControl, StringComparison.OrdinalIgnoreCase)))
                    {
                        devElegido = dev;
                        break;
                    }
                }

                // Fallback: si no hubo coincidencia exacta pero hay mandos que NO son Xbox virtual, usar el primero
                if (devElegido == null)
                {
                    devElegido = dispositivos.FirstOrDefault(d => !EsXbox360Virtual(d));
                }

                if (devElegido == null)
                {
                    Logger.Log($"[TraductorMando] Hay {dispositivos.Count} dispositivo(s) pero ninguno pasó el filtro de mando físico.");
                    return false;
                }

                try { _joystick?.Unacquire(); _joystick?.Dispose(); } catch { }
                _joystick = new Joystick(_directInput, devElegido.InstanceGuid);
                _joystick.Properties.BufferSize = 128;

                if (_dummyHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_dummyHwnd))
                {
                    if (_dummyHwnd != IntPtr.Zero)
                    {
                        try { DestroyWindow(_dummyHwnd); } catch { }
                        _dummyHwnd = IntPtr.Zero;
                    }
                    _dummyHwnd = CreateWindowEx(0, "STATIC", "SteamOS_DI_Host", 0x08000000, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                }

                _joystick.SetCooperativeLevel(_dummyHwnd, CooperativeLevel.Background | CooperativeLevel.Exclusive);
                _joystick.Acquire();
                Logger.Log($"[TraductorMando] Mando físico conectado y adquirido: '{devElegido.ProductName}' (PID=0x{config.ProductID:X4}, VID=0x{config.VendorID:X4}).");

                // Inicializar efecto de vibración dual nativo (Controlpanel Force)
                try
                {
                    var controlPanelGuid = new Guid("f71ec2ed-e1e4-4ac6-bda6-89f892d3800d");
                    _rumbleParams = new EffectParameters
                    {
                        Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                        Duration = 300_000, // 300 ms: se renueva con cada paquete del juego vía Steam/ViGEm
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
                    try { _rumbleEffect.Download(); } catch { }
                    Logger.Log("[TraductorMando] Efecto de vibración dual inicializado y descargado con éxito.");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[TraductorMando] Nota: No se pudo inicializar efecto de vibración dual: {ex.Message}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[TraductorMando] Error al conectar mando físico: {ex.Message}");
                LiberarJoystick();
                return false;
            }
        }

        private static void ResetearBotonesXbox()
        {
            if (_xboxVirtual == null) return;
            try
            {
                _xboxVirtual.SetButtonState(Xbox360Button.A, false);
                _xboxVirtual.SetButtonState(Xbox360Button.B, false);
                _xboxVirtual.SetButtonState(Xbox360Button.X, false);
                _xboxVirtual.SetButtonState(Xbox360Button.Y, false);
                _xboxVirtual.SetButtonState(Xbox360Button.LeftShoulder, false);
                _xboxVirtual.SetButtonState(Xbox360Button.RightShoulder, false);
                _xboxVirtual.SetButtonState(Xbox360Button.LeftThumb, false);
                _xboxVirtual.SetButtonState(Xbox360Button.RightThumb, false);
                _xboxVirtual.SetButtonState(Xbox360Button.Start, false);
                _xboxVirtual.SetButtonState(Xbox360Button.Back, false);
                _xboxVirtual.SetButtonState(Xbox360Button.Guide, false);
                _xboxVirtual.SetButtonState(Xbox360Button.Up, false);
                _xboxVirtual.SetButtonState(Xbox360Button.Down, false);
                _xboxVirtual.SetButtonState(Xbox360Button.Left, false);
                _xboxVirtual.SetButtonState(Xbox360Button.Right, false);
                _xboxVirtual.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
                _xboxVirtual.SetSliderValue(Xbox360Slider.RightTrigger, 0);
                _xboxVirtual.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                _xboxVirtual.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                _xboxVirtual.SetAxisValue(Xbox360Axis.RightThumbX, 0);
                _xboxVirtual.SetAxisValue(Xbox360Axis.RightThumbY, 0);
            }
            catch { }
        }

        private static void BucleTraduccion(MapeoControl config, CancellationToken token)
        {
            long tickSelectPresionado = 0;
            long tickStartPresionado = 0;
            long tickInicioGuide = 0;
            bool selectBloqueadoPorChord = false;
            bool startBloqueadoPorChord = false;
            bool guideReportado = false;

            while (!token.IsCancellationRequested)
            {
                if (_suspendido)
                {
                    Thread.Sleep(200);
                    continue;
                }

                // 1. Asegurar que el mando virtual de Xbox esté activo
                if (_xboxVirtual == null || _vigemClient == null)
                {
                    if (!AsegurarXboxVirtual())
                    {
                        Thread.Sleep(200);
                        continue;
                    }
                }

                // 2. Asegurar que el mando físico esté conectado y adquirido
                if (_joystick == null)
                {
                    if (!IntentarConectarMandoFisico(config))
                    {
                        Thread.Sleep(200);
                        continue;
                    }
                }

                // 3. Lectura de estado y traducción a Xbox 360
                try
                {
                    _joystick!.Poll();
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

                    _xboxVirtual!.SetButtonState(Xbox360Button.A, btnA);
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

                    // ── DETECCIÓN NATIVA DEL BOTÓN HOME/GUIDE (CHORD SELECT + START) ──
                    if (btnSelect && btnStart)
                    {
                        if (tickInicioGuide == 0)
                        {
                            tickInicioGuide = now;
                            Logger.Log("[TraductorMando] ¡Chord Select+Start detectado! Botón Guide de Xbox 360 activado.");
                        }

                        // Mantener Guide activado durante toda la pulsación (comportamiento idéntico al botón físico Guide de Xbox)
                        _xboxVirtual.SetButtonState(Xbox360Button.Guide, true);
                        _xboxVirtual.SetButtonState(Xbox360Button.Back, false);
                        _xboxVirtual.SetButtonState(Xbox360Button.Start, false);
                        selectBloqueadoPorChord = true;
                        startBloqueadoPorChord = true;
                        tickSelectPresionado = 0;
                        tickStartPresionado = 0;
                        guideReportado = true;
                    }
                    else
                    {
                        // Si se acaba de activar Guide pero la pulsación fue ultrarrápida (< 100ms),
                        // asegurar un pulso mínimo de 100ms para que Steam y el Overlay lo registren siempre
                        if (tickInicioGuide > 0 && (now - tickInicioGuide < 100))
                        {
                            _xboxVirtual.SetButtonState(Xbox360Button.Guide, true);
                        }
                        else
                        {
                            if (guideReportado)
                            {
                                Logger.Log("[TraductorMando] Chord Select+Start liberado. Guide desactivado.");
                                guideReportado = false;
                            }
                            _xboxVirtual.SetButtonState(Xbox360Button.Guide, false);
                            tickInicioGuide = 0;
                        }

                        // Desbloqueo únicamente cuando ambos botones son soltados por completo
                        if (!btnSelect && !btnStart)
                        {
                            selectBloqueadoPorChord = false;
                            startBloqueadoPorChord = false;
                        }

                        // Manejo individual de Select (Back)
                        if (btnSelect)
                        {
                            if (!selectBloqueadoPorChord)
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
                            if (!startBloqueadoPorChord)
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
                catch (SharpDX.SharpDXException ex)
                {
                    Logger.Log($"[TraductorMando] Mando físico desconectado o comunicación perdida ({ex.Descriptor.ApiCode}): {ex.Message}. Esperando reconexión...");
                    ResetearBotonesXbox();
                    LiberarJoystick();
                    Thread.Sleep(200);
                }
                catch (VigemInvalidTargetException ex)
                {
                    Logger.Log($"[TraductorMando] Destino virtual ViGEm invalidado: {ex.Message}. Recreando mando virtual...");
                    LiberarXboxVirtual();
                    Thread.Sleep(200);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[TraductorMando] Error en ciclo de traducción: {ex.Message}");
                    ResetearBotonesXbox();
                    LiberarJoystick();
                    LiberarXboxVirtual();
                    Thread.Sleep(300);
                }

                Thread.Sleep(16);
            }

            ResetearBotonesXbox();
            LiberarJoystick();
            LiberarXboxVirtual();
        }

        public static void EnviarRumble(byte largeMotor, byte smallMotor)
        {
            if (_rumbleEffect == null || _rumbleParams == null || _constantForce == null || _joystick == null) return;
            try
            {
                // ── PARAR ─────────────────────────────────────────────────────────────────
                if (largeMotor == 0 && smallMotor == 0)
                {
                    try { _rumbleEffect.Stop(); } catch { }
                    return;
                }

                // ── ACTIVAR / RENOVAR ─────────────────────────────────────────────────────
                // Sin debounce: cada paquete que llega del juego (vía Steam/ViGEm) renueva
                // los 300ms de duración, logrando vibración continua mientras el juego la pida.
                // Si el stop (0,0) se pierde, el motor se auto-detiene en 300ms. (seguro)
                //
                // Eje X: balance entre motor pesado izquierdo y ligero derecho
                //   -1000 = izquierdo puro (impacto grave/baja frecuencia)
                //   +1000 = derecho puro   (vibración aguda/alta frecuencia)
                //       0 = ambos al 50%
                // Rango ±1000 con segundo componente 100: valores calibrados para el G-12U (KYE/Genius)
                int dirX = (largeMotor > 0 && smallMotor > 0)
                    ? (int)(((double)smallMotor - largeMotor) / (smallMotor + largeMotor) * 1000)
                    : (largeMotor > 0 ? -1000 : 1000);

                // Magnitud: escala lineal del motor dominante a rango DirectInput [0, 10000]
                int magnitude = (largeMotor > 0 && smallMotor > 0)
                    ? (int)(Math.Max(largeMotor, smallMotor) / 255.0 * 10000)
                    : (largeMotor > 0 ? (int)(largeMotor / 255.0 * 10000) : (int)(smallMotor / 255.0 * 10000));

                _constantForce.Magnitude = Math.Clamp(magnitude, 0, 10000);
                // 300 000 µs = 300 ms: ventana de seguridad que se renueva con cada paquete.
                // Steam/ViGEm envía paquetes cada ~16 ms mientras el juego pide vibración,
                // por lo que el motor vibra de forma continua y se auto-detiene si el stop se pierde.
                _rumbleParams.Duration = 300_000;
                // Segundo componente 100: necesario para que el driver del G-12U enrute la señal
                // correctamente a los dos motores físicos de forma independiente.
                _rumbleParams.Directions = new int[] { Math.Clamp(dirX, -1000, 1000), 100 };
                _rumbleParams.Parameters = _constantForce;

                try
                {
                    _rumbleEffect.SetParameters(_rumbleParams,
                        EffectParameterFlags.Duration |
                        EffectParameterFlags.Direction |
                        EffectParameterFlags.TypeSpecificParameters);
                    _rumbleEffect.Start(1, EffectPlayFlags.NoDownload);
                }
                catch (SharpDX.SharpDXException)
                {
                    // Si se perdió la adquisición exclusiva temporalmente, re-adquirir y reintentar
                    try
                    {
                        _joystick.Acquire();
                        _rumbleEffect.Download();
                        _rumbleEffect.SetParameters(_rumbleParams,
                            EffectParameterFlags.Duration |
                            EffectParameterFlags.Direction |
                            EffectParameterFlags.TypeSpecificParameters);
                        _rumbleEffect.Start(1, EffectPlayFlags.NoDownload);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TraductorMando] Advertencia en EnviarRumble: {ex.Message}");
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
                        if (!hidHide.BlockedInstanceIds.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
                        {
                            hidHide.AddBlockedInstanceId(instanceId);
                        }
                        if (!_rutasOcultadas.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
                        {
                            _rutasOcultadas.Add(instanceId);
                        }
                    }
                    catch (Exception ex) { Logger.Log($"[TraductorMando] Error ocultando dispositivo {instanceId}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Logger.Log($"[TraductorMando] Error configurando HidHide: {ex.Message}"); }
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
            catch (Exception ex) { Logger.Log($"[TraductorMando] Error revirtiendo HidHide: {ex.Message}"); }
        }
    }
}