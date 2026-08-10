using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using SteamOSConfigurator.Helpers;
using WindowsLikeSteamOS.Services;

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
        void SetOverlayVisible(bool visible);
        void WriteOverlayTexture(byte[] pixels, int width, int height, bool visible);
        void ReiniciarEstadoIPC();
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
        [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        public static void ForceDisableFullscreenOptimizations(string exePath)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");

                string current = key.GetValue(exePath) as string ?? "";
                if (!current.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE"))
                {
                    string newValue = string.IsNullOrEmpty(current)
                        ? "~ DISABLEDXMAXIMIZEDWINDOWEDMODE"
                        : current + " DISABLEDXMAXIMIZEDWINDOWEDMODE";
                    key.SetValue(exePath, newValue, RegistryValueKind.String);
                    Logger.Log($"[SteamService] AppCompatFlags activado para {exePath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamService] Error al establecer AppCompatFlags para {exePath}: {ex.Message}");
            }
        }



        private HashSet<IntPtr> _ventanasSteamOcultas = new HashSet<IntPtr>();
        private readonly object _lockVentanas = new object();
        private IntPtr _juegoActivoHwnd = IntPtr.Zero;
        private WlsosIpc? _currentIpc = null;
        private int _currentIpcPid = 0;
        private readonly object _ipcLock = new object();

        public IntPtr JuegoActivoHwnd => _juegoActivoHwnd;

        private void EnsureIpcForProcess(int pid)
        {
            lock (_ipcLock)
            {
                if (_currentIpc != null && _currentIpcPid == pid) return;

                DisposeIpcInternal();

                _currentIpcPid = pid;
                Logger.Log($"[SteamService] Creating IPC for PID {pid}: H2A=Local\\WLSOS_IPC_H2A_{pid} A2H=Local\\WLSOS_IPC_A2H_{pid}");
                try
                {
                    _currentIpc = new WlsosIpc(pid);
                    _currentIpc.OnFSRChanged += (enabled) => Logger.Log($"[IPC Event] FSR Changed: {enabled}");
                    _currentIpc.OnFSRSharpnessChanged += (sharp) => Logger.Log($"[IPC Event] FSR Sharpness Changed: {sharp:F2}");
                    _currentIpc.OnCRTChanged += (enabled) => Logger.Log($"[IPC Event] CRT Changed: {enabled}");
                    _currentIpc.OnCRTIntensityChanged += (intensity) => Logger.Log($"[IPC Event] CRT Intensity Changed: {intensity:F2}");

                    _currentIpc.OnVolumeRequested += (vol) => {
                        try { new AudioService().EstablecerVolumen(vol); } catch { }
                    };

                    _currentIpc.OnPowerActionRequested += (action) => {
                        try {
                            var power = new PowerService();
                            switch (action) {
                                case PowerAction.Suspend: power.Suspend(); break;
                                case PowerAction.Hibernate: power.Hibernate(); break;
                                case PowerAction.Restart: power.Restart(); break;
                                case PowerAction.Shutdown: power.Shutdown(); break;
                                case PowerAction.Desktop:
                                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                                        if (App.VentanaRecuperacionInstancia != null) {
                                            App.VentanaRecuperacionInstancia.AccionResultante = AccionRecuperacion.ModoEscritorio;
                                            App.VentanaRecuperacionInstancia.OcultarPanel();
                                        }
                                    });
                                    break;
                            }
                        } catch { }
                    };

                    _currentIpc.SetOverlayVisible(false);
                    _currentIpc.SetFSR(false, 0.5f);
                    _currentIpc.SetCRT(false, 0.15f);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[SteamService] Error al crear IPC para PID {pid}: {ex.Message}");
                    _currentIpc = null;
                    _currentIpcPid = 0;
                }
            }
        }

        public void SetOverlayVisible(bool visible)
        {
            lock (_ipcLock)
            {
                _currentIpc?.SetOverlayVisible(visible);
            }
        }

        public void WriteOverlayTexture(byte[] pixels, int width, int height, bool visible)
        {
            lock (_ipcLock)
            {
                _currentIpc?.WriteOverlayTexture(pixels, width, height, visible);
            }
        }

        public void ReiniciarEstadoIPC()
        {
            Logger.Log("[SteamService] Reiniciando y limpiando estado de memoria IPC...");
            DisposeIpcInternal();
        }

        private void DisposeIpcInternal()
        {
            lock (_ipcLock)
            {
                if (_currentIpc != null)
                {
                    Logger.Log($"[SteamService] Disposing IPC for PID {_currentIpcPid}");
                    try { _currentIpc.Dispose(); } catch { }
                    _currentIpc = null;
                    _currentIpcPid = 0;
                }
            }
        }

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
                            DisposeIpcInternal();
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
                        DisposeIpcInternal();
                        CambiarVisibilidadSteam(false);
                        keyboardHookService.Suspendido = false;
                        juegoActivo?.Dispose();
                        juegoActivo = null;
                    }
                }
                else
                {
                    // 1. Intentar detección perfecta mediante RivaTuner (RTSS)
                    var rtssInfo = RTSSSharedMemory.ObtenerRendimientoJuegoActual();
                    IntPtr fgHwnd = GetForegroundWindow();
                    GetWindowThreadProcessId(fgHwnd, out uint fgPid);

                    if (rtssInfo != null && rtssInfo.DatosValidos && rtssInfo.ProcessId > 0 && rtssInfo.ProcessId == fgPid)
                    {
                        try
                        {
                            var proc = Process.GetProcessById((int)rtssInfo.ProcessId);
                            string pName = proc.ProcessName.ToLower();

                            juegoActivo = proc;
                            _juegoActivoHwnd = fgHwnd;

                            Logger.Log($"[MonitorDeJuegosAsync] Juego detectado vía RTSS: '{pName}' (PID={rtssInfo.ProcessId}). Ocultando ventanas secundarias de Steam.");
                            EnsureIpcForProcess((int)rtssInfo.ProcessId);
                            CambiarVisibilidadSteam(true);
                            continue;
                        }
                        catch { }
                    }

                    // 2. Fallback heurístico si RTSS no detecta o no está en foreground
                    if (fgHwnd != IntPtr.Zero && fgPid > 0)
                    {
                        try
                        {
                            var proc = Process.GetProcessById((int)fgPid);
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
                                // Aquí quitamos la estricta comprobación de GetWindowTextLength > 0
                                // porque juegos como Dark Souls 3 a veces no tienen título en ciertas API.
                                // Ya que hemos filtrado los procesos de sistema, asumiremos que es un juego.
                                juegoActivo = proc;
                                _juegoActivoHwnd = fgHwnd;

                                Logger.Log($"[MonitorDeJuegosAsync] Juego detectado por heurística (Fallback): '{pName}' (PID={fgPid}).");
                                EnsureIpcForProcess((int)fgPid);
                                CambiarVisibilidadSteam(true);
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
            DisposeIpcInternal();
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
