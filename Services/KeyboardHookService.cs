using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamOSConfigurator.Services
{
    public interface IKeyboardHookService
    {
        void IniciarHook(Func<bool> aislamientoActivoFunc);
        void DetenerHook();
        bool Suspendido { get; set; }
    }

    public class KeyboardHookService : IKeyboardHookService
    {
        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")] static extern short GetKeyState(int nVirtKey);

        const int WH_KEYBOARD_LL = 13;

        private LowLevelKeyboardProc? _keyboardDelegate;
        private IntPtr _keyboardHook = IntPtr.Zero;
        private Func<bool>? _aislamientoActivoFunc;

        public bool Suspendido { get; set; } = false;

        public void IniciarHook(Func<bool> aislamientoActivoFunc)
        {
            _aislamientoActivoFunc = aislamientoActivoFunc;
            _keyboardDelegate = KeyboardHookCallback;

            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardDelegate, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        public void DetenerHook()
        {
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

            if (nCode >= 0 && _aislamientoActivoFunc != null && _aislamientoActivoFunc())
            {
                int vkCode = Marshal.ReadInt32(lParam);
                bool altPressed = (GetKeyState(0x12) & 0x8000) != 0;
                bool ctrlPressed = (GetKeyState(0x11) & 0x8000) != 0;

                if ((vkCode == 0x09 && altPressed) || (vkCode == 0x1B && altPressed) || (vkCode == 0x1B && ctrlPressed) || vkCode == 0x5B || vkCode == 0x5C)
                    return new IntPtr(1);
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }
    }
}
