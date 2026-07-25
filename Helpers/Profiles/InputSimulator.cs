using System.Runtime.InteropServices;
using System.Threading;
using System;

namespace SteamOSConfigurator.Helpers.Profiles;

/// <summary>
/// Envía pulsaciones de teclado a nivel de hardware (scan code) usando SendInput.
/// A diferencia de PostMessage/SendMessage, esto pasa por la misma cola de
/// input del driver que un teclado físico, por lo que el DirectInput/RawInput
/// del juego lo detecta como una pulsación real. No toca memoria del proceso
/// del juego ni engancha nada: es 100% superficie pública de Win32.
/// </summary>
public static class InputSimulator
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0x00;

    private const ushort VK_MENU = 0x12;   // Alt (genérico izquierdo)
    private const ushort VK_RETURN = 0x0D; // Enter

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Envía la secuencia Alt down -> Enter down -> Enter up -> Alt up como
    /// pulsaciones de hardware reales (scan code), con pausas entre cada
    /// evento para imitar el timing humano y darle tiempo al juego de
    /// procesar el WM_SYSKEYDOWN/WM_KEYDOWN antes del siguiente evento.
    /// </summary>
    /// <param name="targetWindow">
    /// Handle de la ventana del juego. Se trae a primer plano antes de
    /// enviar el input: SendInput entrega a la ventana con foco, no a un
    /// handle específico, así que si el foco está en otro sitio esto no
    /// tendrá ningún efecto sobre el juego.
    /// </param>
    /// <param name="keyDelayMs">Pausa entre cada evento individual de tecla.</param>
    public static void SendAltEnter(IntPtr targetWindow, int keyDelayMs = 60)
    {
        if (targetWindow != IntPtr.Zero)
        {
            SetForegroundWindow(targetWindow);
            Thread.Sleep(150); // dar tiempo a que el foco realmente cambie
        }

        SendKeyEvent(VK_MENU, keyUp: false);
        Thread.Sleep(keyDelayMs);

        SendKeyEvent(VK_RETURN, keyUp: false);
        Thread.Sleep(keyDelayMs);

        SendKeyEvent(VK_RETURN, keyUp: true);
        Thread.Sleep(keyDelayMs);

        SendKeyEvent(VK_MENU, keyUp: true);
    }

    private static void SendKeyEvent(ushort virtualKey, bool keyUp)
    {
        var scanCode = (ushort)MapVirtualKey(virtualKey, MAPVK_VK_TO_VSC);

        var flags = KEYEVENTF_SCANCODE;
        if (keyUp) flags |= KEYEVENTF_KEYUP;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,          // 0 porque usamos KEYEVENTF_SCANCODE, no VK directo
                    wScan = scanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        var inputs = new[] { input };
        var sent = SendInput(1, inputs, Marshal.SizeOf<INPUT>());

        if (sent == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"SendInput falló (Win32 error {error}) al enviar VK 0x{virtualKey:X2}.");
        }
    }
}
