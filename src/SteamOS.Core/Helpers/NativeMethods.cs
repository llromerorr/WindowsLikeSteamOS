using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamOSConfigurator.Helpers
{
    public static class NativeMethods
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag); 
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
        
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        
        [StructLayout(LayoutKind.Sequential)] 
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        
        [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] public static extern bool SystemParametersInfoTimeout(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
        
        public const uint SPI_SETWORKAREA = 0x002F; 
        public const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001; 
        public const uint SPIF_SENDCHANGE = 0x0002; 
        public const uint SPIF_UPDATEINIFILE = 0x0001;
        
        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool ExitWindowsEx(uint uFlags, uint dwReason);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); 
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hWnd);
        
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)] public static extern IntPtr LoadLibrary(string lpFileName);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeLibrary(IntPtr hModule);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void UpdateProfilesDelegate();
        
        public const int SW_HIDE = 0; 
        public const uint EVENT_SYSTEM_FOREGROUND = 3; 
        public const uint WINEVENT_OUTOFCONTEXT = 0;

        public const int WM_POWERBROADCAST = 0x0218;
        public const int PBT_APMSUSPEND = 0x0004;
        public const int PBT_APMRESUMESUSPEND = 0x0007;
    }
}
