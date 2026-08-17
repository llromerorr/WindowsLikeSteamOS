using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SteamOSConfigurator.Helpers;

namespace SteamOSConfigurator.Services
{
    public class WindowWatcherService : IDisposable
    {
        public static bool IsGameRunning
        {
            get
            {
                var rtss = Helpers.RTSSSharedMemory.ObtenerRendimientoJuegoActual();
                return rtss.DatosValidos && !string.IsNullOrEmpty(rtss.GameName);
            }
        }

        // --- Win32 APIs ---
        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        static extern bool PostThreadMessage(uint idThread, uint Msg, UIntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        // --- Constants ---
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        const uint EVENT_OBJECT_CREATE = 0x8000;
        const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        const uint WM_QUIT = 0x0012;

        const int OBJID_WINDOW = 0;
        const int CHILDID_SELF = 0;
        const uint GA_ROOT = 2;

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const int DWMWA_CLOAKED = 14;

        // --- Service State ---
        private Thread? _hookThread;
        private uint _hookThreadId;
        private IntPtr _hookCreate;
        private IntPtr _hookForeground;
        private IntPtr _hookLocation;
        private WinEventDelegate? _delegate; // Keep reference to prevent GC
        private bool _isRunning;
        private readonly int _currentProcessId;
        
        // Debounce dictionary: HWND -> Last execution time
        private ConcurrentDictionary<IntPtr, DateTime> _debounceMap = new();
        private const int DEBOUNCE_MS = 500;

        public WindowWatcherService()
        {
            _currentProcessId = Process.GetCurrentProcess().Id;
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            _hookThread = new Thread(HookThreadProc);
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.IsBackground = true;
            _hookThread.Start();
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;

            if (_hookThreadId != 0)
            {
                PostThreadMessage(_hookThreadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
            }
        }

        private void HookThreadProc()
        {
            _hookThreadId = GetCurrentThreadId();
            _delegate = new WinEventDelegate(WinEventCallback);

            _hookCreate = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_CREATE, IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            _hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_hookCreate != IntPtr.Zero) UnhookWinEvent(_hookCreate);
            if (_hookForeground != IntPtr.Zero) UnhookWinEvent(_hookForeground);
            if (_hookLocation != IntPtr.Zero) UnhookWinEvent(_hookLocation);

            _hookThreadId = 0;
        }

        private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                if (!_isRunning || hwnd == IntPtr.Zero) return;
                
                // 1. Strict Filters
                if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return; 
                if (!IsWindowVisible(hwnd)) return;
                if (GetAncestor(hwnd, GA_ROOT) != hwnd) return; 
                if (GetWindowTextLength(hwnd) == 0) return; 

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == _currentProcessId || pid == 0) return; // Ignore ourselves

                // Check UWP cloaking
                DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
                if (cloaked != 0) return;

                // 2. Debounce
                if (_debounceMap.TryGetValue(hwnd, out DateTime lastTime))
                {
                    if ((DateTime.UtcNow - lastTime).TotalMilliseconds < DEBOUNCE_MS)
                    {
                        return; // Too soon, ignore
                    }
                }
                _debounceMap[hwnd] = DateTime.UtcNow;

                // 3. Process validation (Is it a game?)
                string exePath = GetProcessExePath(pid);
                if (string.IsNullOrEmpty(exePath)) return;
                
                string exeName = System.IO.Path.GetFileName(exePath).ToLower();
                
                // Exclude common system processes
                if (IsSystemProcess(exeName)) return;

            }
            catch (Exception ex)
            {
                Logger.Log($"[WindowWatcherService] Error in callback: {ex.Message}");
            }
        }

        private string GetProcessExePath(uint pid)
        {
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) return ""; // Likely access denied due to Anti-Cheat, which is fine, we skip gracefully

            try
            {
                uint size = 1024;
                StringBuilder sb = new StringBuilder((int)size);
                if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                {
                    return sb.ToString();
                }
                return "";
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        private bool IsSystemProcess(string exeName)
        {
            string[] systemExes = {
                "explorer.exe", "searchapp.exe", "taskmgr.exe", "devenv.exe", 
                "chrome.exe", "msedge.exe", "firefox.exe", "discord.exe", 
                "spotify.exe", "steam.exe", "steamwebhelper.exe", 
                "applicationframehost.exe", "systemsettings.exe", "cmd.exe", "conhost.exe", "powershell.exe",
                "rtss.exe", "rtsshooksloader64.exe", "dwm.exe"
            };
            
            foreach (var sysExe in systemExes)
            {
                if (exeName == sysExe) return true;
            }
            return false;
        }

        public void Dispose()
        {
            Stop();
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }
    }
}
