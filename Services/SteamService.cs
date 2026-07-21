using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using SteamOSConfigurator.Helpers;

namespace SteamOSConfigurator.Services
{
    public interface ISteamService
    {
        Task<bool> EsperarSteamListoAsync(Func<bool> modoEscritorioFunc);
        Task MonitorDeJuegosAsync(Func<bool> modoEscritorioFunc, IKeyboardHookService keyboardHookService);
        void CambiarVisibilidadSteam(bool ocultar);
        string ObtenerRutaSteam();
        void LimpiarPosicionVentanaSteam();
        void MoverVentanaSteamAlMonitorPrincipal(int steamPid, int intentos);
        IntPtr JuegoActivoHwnd { get; }
        void AddVentanaSteamOculta(IntPtr hwnd);
    }

    public class SteamService : ISteamService
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOZORDER = 0x0004;
        const int SW_HIDE = 0;
        const int SW_SHOW = 5;
        const int SW_RESTORE = 9;

        private HashSet<IntPtr> _ventanasSteamOcultas = new HashSet<IntPtr>();
        private readonly object _lockVentanas = new object();
        private IntPtr _juegoActivoHwnd = IntPtr.Zero;

        public IntPtr JuegoActivoHwnd => _juegoActivoHwnd;

        public void AddVentanaSteamOculta(IntPtr hwnd)
        {
            lock (_lockVentanas) { _ventanasSteamOcultas.Add(hwnd); }
        }

        private string? DetectarVentanaSteam()
        {
            string? resultado = null;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                int length = GetWindowTextLength(hWnd);
                if (length > 0)
                {
                    StringBuilder sb = new StringBuilder(length + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    string titulo = sb.ToString().ToLower().Trim();

                    GetWindowThreadProcessId(hWnd, out uint pid);
                    try
                    {
                        using (var proc = Process.GetProcessById((int)pid))
                        {
                            string pName = proc.ProcessName.ToLower();
                            if (pName == "steam" || pName == "steamwebhelper")
                            {
                                if (titulo.Contains("updating steam") ||
                                    titulo.Contains("actualizando steam") ||
                                    titulo.Contains("steam update") ||
                                    titulo.Contains("bootstrapper") ||
                                    titulo.Contains("steam updater") ||
                                    titulo.Contains("steam - actualización") ||
                                    titulo.Contains("steam - update"))
                                {
                                    resultado = "updating";
                                    return false; // Detener enumeración
                                }

                                if (titulo.Contains("steam login") ||
                                    titulo.Contains("iniciar sesión en steam") ||
                                    titulo.Contains("iniciar sesión") ||
                                    titulo.Contains("iniciar sesion") ||
                                    titulo.Contains("connecting steam") ||
                                    titulo.Contains("conectando a steam") ||
                                    titulo.Contains("connecting to steam") ||
                                    titulo.Contains("conectando a la red de steam"))
                                {
                                    resultado = "login";
                                    return false; // Detener enumeración
                                }

                                if (titulo.Contains("big picture") || titulo == "steam")
                                {
                                    resultado = "bigpicture";
                                    return false; // Detener enumeración
                                }

                                // Loguear ventana ignorada para diagnóstico
                                Logger.Log($"[DetectarVentanaSteam] Ignorada visible: PID={pid}, Proc={pName}, Title=\"{titulo}\"");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Loguear errores de GetProcessById
                        Logger.Log($"[DetectarVentanaSteam] Error al obtener proceso para HWND={hWnd.ToInt64():X}, PID={pid}: {ex.Message}");
                    }
                }
                return true;
            }, IntPtr.Zero);

            return resultado;
        }

        private async Task EsperarReinicioDeSteamAsync(Func<bool> modoEscritorioFunc)
        {
            Logger.Log("Esperando que el proceso anterior de Steam termine...");
            int intentos = 0;
            while (!modoEscritorioFunc() && intentos < 30)
            {
                var proc = Process.GetProcessesByName("steam");
                bool tieneProcesos = proc.Length > 0;
                foreach (var p in proc) p.Dispose();
                if (!tieneProcesos) break;
                await Task.Delay(500);
                intentos++;
            }

            Logger.Log("Esperando que un nuevo proceso de Steam se inicie...");
            intentos = 0;
            Process? nuevoSteam = null;
            while (!modoEscritorioFunc() && intentos < 60)
            {
                var proc = Process.GetProcessesByName("steam");
                if (proc.Length > 0)
                {
                    nuevoSteam = proc[0];
                    for (int j = 1; j < proc.Length; j++) proc[j].Dispose();
                    break;
                }
                await Task.Delay(500);
                intentos++;
            }

            if (nuevoSteam != null)
            {
                Logger.Log($"Nuevo proceso de Steam detectado (PID: {nuevoSteam.Id}). Esperando steamwebhelper...");
                intentos = 0;
                while (!modoEscritorioFunc() && intentos < 60)
                {
                    var procHelper = Process.GetProcessesByName("steamwebhelper");
                    bool helperActivo = procHelper.Length > 0;
                    foreach (var p in procHelper) p.Dispose();
                    if (helperActivo)
                    {
                        Logger.Log("steamwebhelper detectado, reinicio completado.");
                        break;
                    }
                    await Task.Delay(500);
                    intentos++;
                }

                // Re-lanzar gamepadui para asegurar que abra correctamente
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = ObtenerRutaSteam(),
                        Arguments = "-gamepadui",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error al relanzar Steam con -gamepadui: {ex.Message}");
                }

                MoverVentanaSteamAlMonitorPrincipal(nuevoSteam.Id, 15);
                nuevoSteam.Dispose();
            }
            else
            {
                Logger.Log("[EsperarReinicioDeSteamAsync] Advertencia: No se detectó un nuevo proceso de Steam tras el reinicio.");
            }
        }

        public async Task<bool> EsperarSteamListoAsync(Func<bool> modoEscritorioFunc)
        {
            bool seEstaActualizando = false;
            int ticksSinProceso = 0;

            Logger.Log("[EsperarSteamListoAsync] Iniciando bucle de espera de Steam...");

            while (!modoEscritorioFunc())
            {
                var procesosSteam = Process.GetProcessesByName("steam");
                var procesosHelper = Process.GetProcessesByName("steamwebhelper");

                int steamCount = procesosSteam.Length;
                int helperCount = procesosHelper.Length;

                foreach (var p in procesosSteam) p.Dispose();
                foreach (var p in procesosHelper) p.Dispose();

                if (steamCount == 0 && helperCount == 0)
                {
                    ticksSinProceso++;
                    int limiteTicks = seEstaActualizando ? 120 : 20;
                    if (ticksSinProceso >= limiteTicks)
                    {
                        Logger.Log($"[EsperarSteamListoAsync] No se detectaron procesos de Steam por {limiteTicks * 0.5} segundos. Cancelando espera.");
                        return false;
                    }
                }
                else
                {
                    if (ticksSinProceso > 0)
                        Logger.Log($"[EsperarSteamListoAsync] Procesos detectados. Reseteando ticks sin proceso. (Procesos steam: {steamCount}, helper: {helperCount})");
                    ticksSinProceso = 0;
                }

                var ventanaDetectada = DetectarVentanaSteam();

                if (ventanaDetectada == "bigpicture")
                {
                    Logger.Log("[EsperarSteamListoAsync] Big Picture detectado.");
                    return true;
                }

                if (ventanaDetectada == "updating")
                {
                    if (!seEstaActualizando)
                    {
                        seEstaActualizando = true;
                        Logger.Log("[EsperarSteamListoAsync] Steam se está actualizando...");
                    }
                }
                else if (ventanaDetectada == "login")
                {
                    Logger.Log("[EsperarSteamListoAsync] Ventana de login o conexión detectada. Esperando...");
                }
                else if (seEstaActualizando)
                {
                    Logger.Log("[EsperarSteamListoAsync] La ventana de actualización desapareció. Esperando reinicio de Steam...");
                    seEstaActualizando = false;
                    await EsperarReinicioDeSteamAsync(modoEscritorioFunc);
                    ticksSinProceso = 0; // Resetear tras esperar reinicio
                }

                await Task.Delay(500);
            }
            Logger.Log("[EsperarSteamListoAsync] Saliendo del bucle por activación de modo Escritorio.");
            return false;
        }

        public async Task MonitorDeJuegosAsync(Func<bool> modoEscritorioFunc, IKeyboardHookService keyboardHookService)
        {
            Process? juegoActivo = null;
            Logger.Log("[MonitorDeJuegosAsync] Monitor de juegos iniciado.");

            while (!modoEscritorioFunc())
            {
                await Task.Delay(1000);
                if (juegoActivo != null)
                {
                    try
                    {
                        if (juegoActivo.HasExited)
                        {
                            _juegoActivoHwnd = IntPtr.Zero;
                            Logger.Log($"[MonitorDeJuegosAsync] Juego finalizado: PID={juegoActivo.Id}. Reactivando hook de teclado y restaurando visibilidad de Steam.");
                            CambiarVisibilidadSteam(false);
                            keyboardHookService.Suspendido = false;
                            juegoActivo.Dispose();
                            juegoActivo = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[MonitorDeJuegosAsync] Error al consultar salida del juego: {ex.Message}");
                        _juegoActivoHwnd = IntPtr.Zero;
                        CambiarVisibilidadSteam(false);
                        keyboardHookService.Suspendido = false;
                        juegoActivo?.Dispose();
                        juegoActivo = null;
                    }
                }
                else
                {
                    IntPtr fgHwnd = GetForegroundWindow();
                    if (fgHwnd != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(fgHwnd, out uint pid);
                        try
                        {
                            var proc = Process.GetProcessById((int)pid);
                            string pName = proc.ProcessName.ToLower();

                            var ignoreList = new HashSet<string>
                            {
                                "steam", "steamwebhelper", "gameoverlayui", "windowslikesteamos", "explorer",
                                "idle", "svchost", "ctfmon", "conhost", "taskmgr", "logonui", "dwm", 
                                "rundll32", "spoolsv", "shellexperiencehost", "searchhost", 
                                "startmenuexperiencehost", "lockapp", "sihost", "smartscreen", 
                                "applicationframehost", "windowsterminal"
                            };

                            if (!ignoreList.Contains(pName))
                            {
                                int length = GetWindowTextLength(fgHwnd);
                                if (length > 0)
                                {
                                    StringBuilder sb = new StringBuilder(length + 1);
                                    GetWindowText(fgHwnd, sb, sb.Capacity);
                                    string titulo = sb.ToString();

                                    juegoActivo = proc;
                                    _juegoActivoHwnd = fgHwnd;
                                    // Mantener el hook de teclado activo durante el juego para bloquear Alt+Tab, Alt+F4 y Tecla Windows
                                    Logger.Log($"[MonitorDeJuegosAsync] Juego detectado en primer plano: '{pName}' (PID={pid}, Title=\"{titulo}\"). Ocultando ventanas secundarias de Steam.");
                                    CambiarVisibilidadSteam(true);
                                }
                                else
                                {
                                    proc.Dispose();
                                }
                            }
                            else
                            {
                                proc.Dispose();
                            }
                        }
                        catch { }
                    }
                }
            }
            juegoActivo?.Dispose();
            Logger.Log("[MonitorDeJuegosAsync] Monitor de juegos finalizado.");
        }

        public void CambiarVisibilidadSteam(bool ocultar)
        {
            if (ocultar)
            {
                lock (_lockVentanas) { _ventanasSteamOcultas.Clear(); }
                Logger.Log("[CambiarVisibilidadSteam] Ocultando ventanas de Steam...");
                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    try
                    {
                        using (var proc = Process.GetProcessById((int)pid))
                        {
                            string pName = proc.ProcessName.ToLower();
                            if (pName == "steam" || pName == "steamwebhelper")
                            {
                                if (IsWindowVisible(hWnd))
                                {
                                    int length = GetWindowTextLength(hWnd);
                                    string titulo = "";
                                    if (length > 0)
                                    {
                                        StringBuilder sb = new StringBuilder(length + 1);
                                        GetWindowText(hWnd, sb, sb.Capacity);
                                        titulo = sb.ToString();
                                    }
                                    Logger.Log($"[CambiarVisibilidadSteam] Ocultando HWND={hWnd.ToInt64():X}, Title=\"{titulo}\" (Proceso={pName})");
                                    lock (_lockVentanas) { _ventanasSteamOcultas.Add(hWnd); }
                                    ShowWindow(hWnd, SW_HIDE);
                                }
                            }
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            else
            {
                lock (_lockVentanas)
                {
                    Logger.Log($"[CambiarVisibilidadSteam] Restaurando {_ventanasSteamOcultas.Count} ventanas de Steam...");
                    foreach (IntPtr hWnd in _ventanasSteamOcultas)
                    {
                        int length = GetWindowTextLength(hWnd);
                        string titulo = "";
                        if (length > 0)
                        {
                            StringBuilder sb = new StringBuilder(length + 1);
                            GetWindowText(hWnd, sb, sb.Capacity);
                            titulo = sb.ToString();
                        }
                        Logger.Log($"[CambiarVisibilidadSteam] Mostrando y enfocando HWND={hWnd.ToInt64():X}, Title=\"{titulo}\"");
                        ShowWindow(hWnd, SW_SHOW);
                        SetForegroundWindow(hWnd);
                    }
                    _ventanasSteamOcultas.Clear();
                }
            }
        }

        public string ObtenerRutaSteam()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(AppPaths.SteamRegistryKey);
                if (key != null) return Path.Combine(key.GetValue("InstallPath") as string ?? "", "steam.exe");
            }
            catch { }
            return AppPaths.SteamFallback;
        }

        public void LimpiarPosicionVentanaSteam()
        {
            try
            {
                Logger.Log("[LimpiarPosicionVentanaSteam] Limpiando SteamWindowX y SteamWindowY en el registro.");
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true);
                if (key == null) return;
                key.SetValue("SteamWindowX", 0, RegistryValueKind.DWord);
                key.SetValue("SteamWindowY", 0, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Logger.Log($"[LimpiarPosicionVentanaSteam] Error al limpiar registro de Steam: {ex.Message}");
            }
        }

        public void MoverVentanaSteamAlMonitorPrincipal(int steamPid, int intentos)
        {
            Task.Run(async () =>
            {
                Logger.Log($"[MoverVentanaSteamAlMonitorPrincipal] Iniciando tarea para mover ventanas a (0,0)... PID principal={steamPid}, Intentos={intentos}");
                for (int i = 0; i < intentos; i++)
                {
                    await Task.Delay(1000);
                    List<IntPtr> ventanas = new();

                    EnumWindows((hWnd, _) =>
                    {
                        if (IsWindowVisible(hWnd))
                        {
                            GetWindowThreadProcessId(hWnd, out uint pid);
                            try
                            {
                                using (var proc = Process.GetProcessById((int)pid))
                                {
                                    string pName = proc.ProcessName.ToLower();
                                    if (pName == "steam" || pName == "steamwebhelper")
                                    {
                                        ventanas.Add(hWnd);
                                    }
                                }
                            }
                            catch { }
                        }
                        return true;
                    }, IntPtr.Zero);

                    if (ventanas.Count > 0)
                    {
                        Logger.Log($"[MoverVentanaSteamAlMonitorPrincipal] Detectadas {ventanas.Count} ventanas visibles en el intento {i + 1}. Moviendo...");
                        foreach (IntPtr hWnd in ventanas)
                        {
                            int length = GetWindowTextLength(hWnd);
                            string titulo = "";
                            if (length > 0)
                            {
                                StringBuilder sb = new StringBuilder(length + 1);
                                GetWindowText(hWnd, sb, sb.Capacity);
                                titulo = sb.ToString();
                            }
                            Logger.Log($"[MoverVentanaSteamAlMonitorPrincipal] HWND={hWnd.ToInt64():X}, Title=\"{titulo}\" -> Restaurando y moviendo a (0,0)");
                            ShowWindow(hWnd, SW_RESTORE);
                            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
                        }
                        break;
                    }
                    else
                    {
                        if ((i + 1) % 5 == 0)
                            Logger.Log($"[MoverVentanaSteamAlMonitorPrincipal] Intento {i + 1}/{intentos}: No se encontraron ventanas de Steam visibles.");
                    }
                }
                Logger.Log("[MoverVentanaSteamAlMonitorPrincipal] Tarea finalizada.");
            });
        }
    }
}
