using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SteamOSConfigurator.Services
{
    public interface IKeyboardHookService
    {
        void IniciarHook(Func<bool> aislamientoActivoFunc);
        void DetenerHook();
        bool Suspendido { get; set; }
        event Action? OnSalirModoEscritorio;
        event Action? OnAbrirRecuperacion;
    }

    public class KeyboardHookService : IKeyboardHookService
    {
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public System.Drawing.Point pt;
        }

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_HOTKEY = 0x0312;
        private const uint WM_QUIT = 0x0012;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;

        private const int HOTKEY_ID_ESCRITORIO = 1;
        private const int HOTKEY_ID_RECUPERACION = 2;

        private const uint VK_S = 0x53;
        private const uint VK_R = 0x52;

        private LowLevelKeyboardProc? _keyboardDelegate;
        private IntPtr _keyboardHook = IntPtr.Zero;
        private Func<bool>? _aislamientoActivoFunc;
        private Thread? _hookThread;
        private uint _hookThreadId;
        private readonly ManualResetEventSlim _initEvent = new(false);

        public bool Suspendido { get; set; } = false;
        public event Action? OnSalirModoEscritorio;
        public event Action? OnAbrirRecuperacion;

        public void IniciarHook(Func<bool> aislamientoActivoFunc)
        {
            DetenerHook();
            _aislamientoActivoFunc = aislamientoActivoFunc;
            _initEvent.Reset();

            _hookThread = new Thread(HookThreadWorker)
            {
                IsBackground = true,
                Name = "SteamOS_KeyboardHookThread"
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();

            // Esperar inicialización en el hilo STA
            _initEvent.Wait(2000);
        }

        private void HookThreadWorker()
        {
            _hookThreadId = GetCurrentThreadId();
            _keyboardDelegate = KeyboardHookCallback;

            // 1. Registrar HotKeys del Sistema Operativo
            uint modComb = MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;
            RegisterHotKey(IntPtr.Zero, HOTKEY_ID_ESCRITORIO, modComb, VK_S);
            RegisterHotKey(IntPtr.Zero, HOTKEY_ID_RECUPERACION, modComb, VK_R);

            // 2. Instalar Low-Level Keyboard Hook
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule? curModule = curProcess.MainModule)
            {
                IntPtr hMod = GetModuleHandle(curModule?.ModuleName);
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardDelegate, hMod, 0);
            }

            if (_keyboardHook != IntPtr.Zero)
            {
                Logger.Log("[KeyboardHookService] Hook de teclado global registrado con éxito.");
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                Logger.Log($"[KeyboardHookService] Advertencia: Hook SetWindowsHookEx falló (Win32Error={err}), confiando en RegisterHotKey.");
            }

            _initEvent.Set();

            // 3. Message Loop obligatorio de Windows
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY)
                {
                    int id = msg.wParam.ToInt32();
                    if (id == HOTKEY_ID_ESCRITORIO)
                    {
                        Logger.Log("[KeyboardHookService] WM_HOTKEY: Ctrl + Shift + Alt + S detectado.");
                        Task.Run(() => OnSalirModoEscritorio?.Invoke());
                    }
                    else if (id == HOTKEY_ID_RECUPERACION)
                    {
                        Logger.Log("[KeyboardHookService] WM_HOTKEY: Ctrl + Shift + Alt + R detectado.");
                        Task.Run(() => OnAbrirRecuperacion?.Invoke());
                    }
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            // 4. Limpieza al salir
            UnregisterHotKey(IntPtr.Zero, HOTKEY_ID_ESCRITORIO);
            UnregisterHotKey(IntPtr.Zero, HOTKEY_ID_RECUPERACION);

            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
        }

        public void DetenerHook()
        {
            if (_hookThread != null && _hookThread.IsAlive)
            {
                if (_hookThreadId != 0)
                {
                    PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                }
                _hookThread.Join(1000);
                _hookThread = null;
            }

            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (Suspendido)
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            if (nCode >= 0)
            {
                int msgType = wParam.ToInt32();
                if (msgType == WM_KEYDOWN || msgType == WM_SYSKEYDOWN)
                {
                    int vkCode = Marshal.ReadInt32(lParam);

                    bool altPressed = (GetAsyncKeyState(0x12) & 0x8000) != 0;   // VK_MENU
                    bool ctrlPressed = (GetAsyncKeyState(0x11) & 0x8000) != 0;  // VK_CONTROL
                    bool shiftPressed = (GetAsyncKeyState(0x10) & 0x8000) != 0; // VK_SHIFT

                    // 1. Detectar Atajo Global: Ctrl + Shift + Alt + S (Modo Escritorio)
                    if (ctrlPressed && shiftPressed && altPressed && (vkCode == 0x53 || vkCode == 0x73)) // 'S' o 's'
                    {
                        Logger.Log("[KeyboardHookService] Hook Callback: ¡Ctrl + Shift + Alt + S detectado!");
                        Task.Run(() => OnSalirModoEscritorio?.Invoke());
                        return new IntPtr(1);
                    }

                    // 2. Detectar Atajo Global: Ctrl + Shift + Alt + R (Recuperación)
                    if (ctrlPressed && shiftPressed && altPressed && (vkCode == 0x52 || vkCode == 0x72)) // 'R' o 'r'
                    {
                        Logger.Log("[KeyboardHookService] Hook Callback: ¡Ctrl + Shift + Alt + R detectado!");
                        Task.Run(() => OnAbrirRecuperacion?.Invoke());
                        return new IntPtr(1);
                    }

                    // 3. Bloqueo de teclas especiales si aislamiento activo
                    if (_aislamientoActivoFunc != null && _aislamientoActivoFunc())
                    {
                        bool isAltTab = (vkCode == 0x09 && altPressed);
                        bool isAltEsc = (vkCode == 0x1B && altPressed);
                        bool isCtrlEsc = (vkCode == 0x1B && ctrlPressed);
                        bool isWinKey = (vkCode == 0x5B || vkCode == 0x5C);
                        bool isAltF4 = (vkCode == 0x73 && altPressed && !ctrlPressed); // Excluir si viene con Ctrl

                        if (isAltTab || isAltEsc || isCtrlEsc || isWinKey || isAltF4)
                        {
                            Logger.Log($"[KeyboardHookService] Tecla/Atajo bloqueado: VK=0x{vkCode:X}");
                            return new IntPtr(1);
                        }
                    }
                }
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }
    }
}
