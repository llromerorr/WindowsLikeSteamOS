using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SteamOSConfigurator;
using SteamOSConfigurator.Helpers;
using SteamOSConfigurator.Models;
using SteamOSConfigurator.Services;

namespace SteamOS.Shell
{
    public static class Program
    {
        private static readonly IAudioService _audioService = new AudioService();
        private static readonly ISteamService _steamService = new SteamService();
        private static readonly IDisplayService _displayService = new DisplayService();
        private static readonly IKeyboardHookService _keyboardHookService = new KeyboardHookService();
        private static readonly IGpuScalingService _gpuScalingService = new NvidiaGpuScalingService();
        private static readonly IPowerService _powerService = new PowerService();
        private static readonly IDependencyService _dependencyService = new DependencyService();
        private static WindowWatcherService? _windowWatcherService;

        private static bool _modoEscritorio = false;
        private static bool _cerrandoSesion = false;
        public static bool ReinstalandoOReinicioSteam = false;

        private static IntPtr _hWinEventHook = IntPtr.Zero;
        private static NativeMethods.WinEventDelegate? _winEventDelegate;

        [STAThread]
        public static async Task Main(string[] args)
        {
            try
            {
                NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch { }

            NativeMethods.SystemParametersInfoTimeout(NativeMethods.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE | NativeMethods.SPIF_UPDATEINIFILE);

            if (args.Length > 0 && args[0].Equals("--test-rumble", StringComparison.OrdinalIgnoreCase))
            {
                [DllImport("kernel32.dll", SetLastError = true)]
                static extern bool AttachConsole(int dwProcessId);

                if (AttachConsole(-1))
                {
                    try
                    {
                        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                    }
                    catch { }
                }

                void Imprimir(string mensaje)
                {
                    Console.WriteLine(mensaje);
                    Logger.Log(mensaje);
                }

                Imprimir("\n==================================================");
                Imprimir("=== SteamOS - Prueba de Vibración de Mando ===");
                Imprimir("==================================================");
                Imprimir("Iniciando servicio de traducción de mando...");

                _ = TraductorMando.IniciarAsync();

                Imprimir("Buscando y adquiriendo mando físico...");
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(250);
                    if (TraductorMando.EstaConectado) break;
                }

                if (!TraductorMando.EstaConectado)
                {
                    Imprimir("[AVISO] No se pudo adquirir el mando físico. Asegúrate de que esté conectado por USB y mapeado.");
                    TraductorMando.Detener();
                    Environment.ExitCode = 1;
                    return;
                }

                Imprimir("[ÉXITO] ¡Mando adquirido! Toma el mando con las manos.\n");

                Imprimir("-> [Paso 1] Disparo único: pulso de 80ms...");
                TraductorMando.EnviarRumble(255, 255);
                await Task.Delay(80);
                TraductorMando.EnviarRumble(0, 0);
                Imprimir("   -> Pulso finalizado (detención inmediata comprobada).");

                await Task.Delay(1200);

                Imprimir("-> [Paso 2] Ráfaga de 3 disparos rápidos...");
                for (int r = 1; r <= 3; r++)
                {
                    Imprimir($"   Disparo {r}...");
                    TraductorMando.EnviarRumble(255, 255);
                    await Task.Delay(60);
                    TraductorMando.EnviarRumble(0, 0);
                    await Task.Delay(120);
                }
                Imprimir("   -> Ráfaga finalizada.");

                await Task.Delay(1200);

                Imprimir("-> [Paso 3] Motor pesado izquierdo (baja frecuencia / rumble grave)...");
                TraductorMando.EnviarRumble(255, 0);
                await Task.Delay(400);
                TraductorMando.EnviarRumble(0, 0);
                Imprimir("   -> Detenido.");

                await Task.Delay(800);

                Imprimir("-> [Paso 4] Motor ligero derecho (alta frecuencia / rumble agudo)...");
                TraductorMando.EnviarRumble(0, 255);
                await Task.Delay(400);
                TraductorMando.EnviarRumble(0, 0);
                Imprimir("   -> Detenido.");

                await Task.Delay(800);

                Imprimir("-> [Paso 5] Vibración continua (1.5 segundos a 50Hz)...");
                long fin = Environment.TickCount64 + 1500;
                while (Environment.TickCount64 < fin)
                {
                    TraductorMando.EnviarRumble(200, 200);
                    await Task.Delay(20);
                }
                TraductorMando.EnviarRumble(0, 0);
                Imprimir("   -> Corte inmediato aplicado.");

                Imprimir("\n=== PRUEBA DE VIBRACIÓN COMPLETADA CON ÉXITO ===");
                TraductorMando.Detener();
                return;
            }

            Logger.Log("==================================================");
            Logger.Log("=== SteamOS Shell Engine (Background Service) ===");
            Logger.Log("==================================================");

            _keyboardHookService.OnSalirModoEscritorio += SalirAModoEscritorio;

            await EjecutarModoConsolaAsync();
        }

        private static void ForzarRegistroEscaladoNVIDIA()
        {
            try
            {
                Logger.Log("[ForzarRegistroEscaladoNVIDIA] Aplicando forzado de escalado en registro de Windows...");
                using (var config = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration", true))
                {
                    if (config == null) return;
                    string[] subKeys = config.GetSubKeyNames();
                    foreach (string sub in subKeys)
                    {
                        using (var key0 = config.OpenSubKey(sub + @"\00\00", true))
                        {
                            if (key0 == null) continue;
                            try
                            {
                                key0.SetValue("Scaling", 3, RegistryValueKind.DWord);
                                key0.SetValue("ScalingMode", 3, RegistryValueKind.DWord);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ForzarRegistroEscaladoNVIDIA] Error: {ex.Message}");
            }
        }

        private static void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero || _modoEscritorio || _steamService.JuegoActivoHwnd == IntPtr.Zero) return;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;

            try
            {
                using (var proc = Process.GetProcessById((int)pid))
                {
                    string pName = proc.ProcessName.ToLower();
                    if (pName == "steam" || pName == "steamwebhelper")
                    {
                        int length = NativeMethods.GetWindowTextLength(hwnd);
                        string titulo = "";
                        if (length > 0)
                        {
                            StringBuilder sb = new StringBuilder(length + 1);
                            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                            titulo = sb.ToString();
                        }

                        // Proteger la interfaz de Steam, Big Picture, GameOverlayUI y superficies CEF
                        if (string.IsNullOrWhiteSpace(titulo) ||
                            titulo.Contains("Big Picture", StringComparison.OrdinalIgnoreCase) ||
                            titulo.Contains("Overlay", StringComparison.OrdinalIgnoreCase) ||
                            titulo.Equals("Steam", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                        _steamService.AddVentanaSteamOculta(hwnd);
                        NativeMethods.SetForegroundWindow(_steamService.JuegoActivoHwnd);
                    }
                }
            }
            catch { }
        }

        private static async Task EjecutarModoConsolaAsync()
        {
            try
            {
                Logger.Log("[Shell] Iniciando modo consola...");
                FontSmoothingHelper.ActivarClearType();
                GameBarHelper.DesactivarGameBarEnUsuarioActual();

                var config = CargarConfig();
                if (config == null)
                {
                    Logger.Log("[Shell] ERROR: La configuración es nula. Cerrando sesión.");
                    CerrarSesionRapido();
                    return;
                }

                Logger.Log($"[Shell] Configuración cargada: Monitor={config.MonitorDeviceName}, Res={config.ResolucionWidth}x{config.ResolucionHeight}@{config.RefreshRate}Hz, FPS={config.LimiteFPS}");

                _powerService.ActivarPlanMaximoRendimiento();
                _powerService.PrevenirSuspensionAutomatica();

                // Limpiar procesos de Steam previos
                foreach (var p in Process.GetProcessesByName("steam")) { try { p.Kill(); p.Dispose(); } catch { } }
                foreach (var p in Process.GetProcessesByName("steamwebhelper")) { try { p.Kill(); p.Dispose(); } catch { } }
                await Task.Delay(1000);

                var workArea = new NativeMethods.RECT { Left = 0, Top = 0, Right = config.ResolucionWidth, Bottom = config.ResolucionHeight };
                NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETWORKAREA, 0, ref workArea, NativeMethods.SPIF_SENDCHANGE);

                if (config.ForzarFastSync)
                    NvidiaFastSync.Activar();
                else
                    NvidiaFastSync.Restaurar();

                foreach (var p in Process.GetProcessesByName("RTSS")) { try { p.Kill(); p.Dispose(); } catch { } }
                foreach (var p in Process.GetProcessesByName("rtss")) { try { p.Kill(); p.Dispose(); } catch { } }
                foreach (var p in Process.GetProcessesByName("MSIAfterburner")) { try { p.Kill(); p.Dispose(); } catch { } }
                Thread.Sleep(800);

                RivaTunerCore.AsegurarInstalacionSilenciosa();
                RivaTunerCore.ForzarModoConsola(config.LimiteFPS);
                RivaTunerCore.DespertarFantasma();

                _windowWatcherService = new WindowWatcherService();
                _windowWatcherService.Start();

                MSIAfterburnerCore.AsegurarEjecucion();
                RivaTunerCore.AplicarConfiguracion(config.LimiteFPS, config.IndexOSD);
                MPOService.AsegurarMPODesactivado();
                ForzarRegistroEscaladoNVIDIA();

                if (config.AudioDispositivo != null)
                {
                    _audioService.EstablecerDispositivoPorDefecto(config.AudioDispositivo);
                }

                _displayService.AislarPantalla(config, _gpuScalingService);

                if (config.EmuladorActivado)
                {
                    TraductorMando.EsJuegoEnPrimerPlano = () => _steamService.JuegoActivoHwnd != IntPtr.Zero;
                    TraductorMando.AlReanudarSistema = () =>
                    {
                        if (_steamService.JuegoActivoHwnd != IntPtr.Zero)
                        {
                            NativeMethods.SetForegroundWindow(_steamService.JuegoActivoHwnd);
                        }
                        else
                        {
                            _steamService.EnfocarBigPicture();
                        }
                    };
                    _ = TraductorMando.IniciarAsync();
                }

                await Task.Delay(4000);

                _keyboardHookService.IniciarHook(() => !_modoEscritorio);

                string rutaSteam = _steamService.ObtenerRutaSteam();
                if (string.IsNullOrEmpty(rutaSteam))
                {
                    Logger.Log("[Shell] ERROR: No se encontró la ruta de Steam. Cerrando sesión.");
                    CerrarSesionRapido();
                    return;
                }

                _steamService.LimpiarPosicionVentanaSteam();
                _steamService.SanitizarMapeosSdlSteam();

                Logger.Log("[Shell] Iniciando Steam con '-gamepadui'...");
                using (Process? steam = Process.Start(new ProcessStartInfo { FileName = rutaSteam, Arguments = "-gamepadui", UseShellExecute = true }))
                {
                    if (steam != null)
                    {
                        _steamService.MoverVentanaSteamAlMonitorPrincipal(steam.Id, 25);
                    }
                }

                bool steamListo = await _steamService.EsperarSteamListoAsync(() => _modoEscritorio);
                if (steamListo)
                {
                    _steamService.EnfocarBigPicture();
                }
                else if (!_modoEscritorio)
                {
                    Logger.Log("[Shell] Steam no se inició correctamente (Timeout).");
                }

                if (!_modoEscritorio)
                {
                    _winEventDelegate = new NativeMethods.WinEventDelegate(WinEventCallback);
                    _hWinEventHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventDelegate, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
                    _ = Task.Run(() => _steamService.MonitorDeJuegosAsync(() => _modoEscritorio, _keyboardHookService));
                }

                while (!_modoEscritorio)
                {
                    var procesosSteam = Process.GetProcessesByName("steam");
                    if (procesosSteam.Length == 0)
                    {
                        if (ReinstalandoOReinicioSteam)
                        {
                            await Task.Delay(3000);
                            continue;
                        }

                        bool seReinicio = await EsperarPosibleReinicio(4000);
                        if (seReinicio || ReinstalandoOReinicioSteam)
                        {
                            await _steamService.EsperarSteamListoAsync(() => _modoEscritorio);
                            ReinstalandoOReinicioSteam = false;
                            continue;
                        }
                        else
                        {
                            Logger.Log("[Shell] Steam cerrado definitivamente. Saliendo.");
                            break;
                        }
                    }

                    Process steamPrincipal = procesosSteam[0];
                    steamPrincipal.EnableRaisingEvents = true;
                    for (int j = 1; j < procesosSteam.Length; j++) procesosSteam[j].Dispose();
                    try { await steamPrincipal.WaitForExitAsync(); } catch { }
                    steamPrincipal.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Shell] Error fatal: {ex.Message}");
            }
            finally
            {
                Logger.Log("[Shell] Restaurando entorno original...");
                if (!_modoEscritorio && !_cerrandoSesion)
                {
                    try { _displayService.RestaurarEntornoOriginal(_gpuScalingService); } catch { }
                    _cerrandoSesion = true;
                    CerrarSesionRapido();
                }

                try { _powerService.RestaurarPlanEnergia(); } catch { }
                try { _powerService.PermitirSuspension(); } catch { }
                try { RivaTunerCore.ApagarFantasma(); } catch { }
                try { NvidiaFastSync.Restaurar(); } catch { }

                try { if (_hWinEventHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(_hWinEventHook); _hWinEventHook = IntPtr.Zero; } } catch { }
                try { _keyboardHookService.DetenerHook(); } catch { }
                try { NativeMethods.SystemParametersInfoTimeout(NativeMethods.SPI_SETFOREGROUNDLOCKTIMEOUT, 200000, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE | NativeMethods.SPIF_UPDATEINIFILE); } catch { }
                Logger.Log("[Shell] Restauración finalizada.");
            }
        }

        private static async Task<bool> EsperarPosibleReinicio(int timeoutMs)
        {
            int transcurrido = 0;
            while (transcurrido < timeoutMs && !_modoEscritorio && !_cerrandoSesion)
            {
                var procesos = Process.GetProcessesByName("steam");
                bool tieneProcesos = procesos.Length > 0;
                foreach (var p in procesos) p.Dispose();
                if (tieneProcesos)
                {
                    await Task.Delay(1500);
                    transcurrido += 1500;

                    var procesosNuevos = Process.GetProcessesByName("steam");
                    bool tieneProcesosNuevos = procesosNuevos.Length > 0;
                    foreach (var p in procesosNuevos) p.Dispose();
                    if (tieneProcesosNuevos)
                    {
                        await Task.Delay(1500);
                        transcurrido += 1500;

                        var procesosConfirmacion = Process.GetProcessesByName("steam");
                        bool tieneConfirmacion = procesosConfirmacion.Length > 0;
                        foreach (var p in procesosConfirmacion) p.Dispose();
                        if (tieneConfirmacion) return true;
                    }
                }
                await Task.Delay(500);
                transcurrido += 500;
            }
            return false;
        }

        private static void CerrarSesionRapido()
        {
            Logger.Log("[Shell] Cerrando sesión...");
            foreach (var p in Process.GetProcessesByName("steam")) { try { p.Kill(); p.Dispose(); } catch { } }
            foreach (var p in Process.GetProcessesByName("steamwebhelper")) { try { p.Kill(); p.Dispose(); } catch { } }

            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/l",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { }
        }

        private static void SalirAModoEscritorio()
        {
            if (_modoEscritorio) return;
            _modoEscritorio = true;
            Logger.Log("[Shell] Atajo Ctrl+Shift+Alt+S detectado: Saliendo a Modo Escritorio (explorer.exe)...");

            try { _displayService.RestaurarEntornoOriginal(_gpuScalingService); } catch (Exception ex) { Logger.Log($"[ModoEscritorio] Error restaurando pantalla: {ex.Message}"); }
            try { _powerService.RestaurarPlanEnergia(); } catch { }
            try { _powerService.PermitirSuspension(); } catch { }
            try { RivaTunerCore.ApagarFantasma(); } catch { }
            try { NvidiaFastSync.Restaurar(); } catch { }
            try { TraductorMando.Detener(); } catch { }
            try { _keyboardHookService.DetenerHook(); } catch { }
            try { if (_hWinEventHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(_hWinEventHook); _hWinEventHook = IntPtr.Zero; } } catch { }

            // Matar procesos de Steam para liberar la pantalla
            foreach (var p in Process.GetProcessesByName("steam")) { try { p.Kill(); p.Dispose(); } catch { } }
            foreach (var p in Process.GetProcessesByName("steamwebhelper")) { try { p.Kill(); p.Dispose(); } catch { } }

            // Iniciar explorer.exe (el escritorio de Windows)
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = true
                });
                Logger.Log("[Shell] explorer.exe lanzado exitosamente.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[Shell] Error lanzando explorer.exe: {ex.Message}");
            }

            Environment.Exit(0);
        }

        private static ConfiguracionSteamOS? CargarConfig()
        {
            string ruta = AppPaths.Config;
            if (!File.Exists(ruta)) return null;
            return JsonSerializer.Deserialize<ConfiguracionSteamOS>(File.ReadAllText(ruta));
        }
    }
}
