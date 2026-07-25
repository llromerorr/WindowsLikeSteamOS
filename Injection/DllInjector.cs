using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using static WindowsLikeSteamOS.Injection.NativeMethods;

namespace WindowsLikeSteamOS.Injection;

public sealed class DllInjector
{
    private readonly IAntiCheatGuard _antiCheatGuard;
    private readonly Action<string>? _logger;

    public uint InjectionTimeoutMs { get; init; } = 10_000;

    public event Action<string>? OnGameLaunchedProtected;
    public event Action<string>? OnGameLaunchedHooked;
    public event Action<string>? OnInjectionFailed;

    public DllInjector(IAntiCheatGuard antiCheatGuard, Action<string>? logger = null)
    {
        _antiCheatGuard = antiCheatGuard;
        _logger = logger;
    }

    private void Log(string message) => _logger?.Invoke($"[DllInjector] {message}");

    public InjectionResult LaunchAndInject(string exePath, string? arguments, string dllPath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return InjectionResult.Failure($"Ejecutable no encontrado: {exePath}");

        if (_antiCheatGuard.IsGameProtected(exePath))
        {
            Log($"'{Path.GetFileName(exePath)}' esta en la lista de Anti-Cheat. Lanzando en modo NATIVO.");
            var protectedProcess = LaunchNormally(exePath, arguments);
            OnGameLaunchedProtected?.Invoke(exePath);
            return InjectionResult.ProtectedLaunch(protectedProcess?.Id ?? -1);
        }

        if (!File.Exists(dllPath))
            return InjectionResult.Failure($"DLL de hooks no encontrada: {dllPath}");

        var startupInfo = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
        string commandLine = string.IsNullOrWhiteSpace(arguments)
            ? $"\"{exePath}\""
            : $"\"{exePath}\" {arguments}";

        bool created = CreateProcessW(
            lpApplicationName: exePath,
            lpCommandLine: commandLine,
            lpProcessAttributes: IntPtr.Zero,
            lpThreadAttributes: IntPtr.Zero,
            bInheritHandles: false,
            dwCreationFlags: CREATE_SUSPENDED,
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: Path.GetDirectoryName(exePath),
            lpStartupInfo: ref startupInfo,
            lpProcessInformation: out PROCESS_INFORMATION processInfo);

        if (!created)
        {
            int err = Marshal.GetLastPInvokeError();
            string msg = $"CreateProcessW fallo. Win32Error={err}";
            Log(msg);
            OnInjectionFailed?.Invoke(msg);
            return InjectionResult.Failure(msg);
        }

        Log($"Proceso creado en estado SUSPENDED. PID={processInfo.dwProcessId}");

        try
        {
            if (!IsTarget64Bit(processInfo.hProcess))
            {
                const string msg = "Proceso objetivo es de 32 bits. SteamOSHooks64.dll es x64-only.";
                Log(msg);
                ResumeThread(processInfo.hThread);
                OnInjectionFailed?.Invoke(msg);
                return InjectionResult.Failure(msg, processInfo.dwProcessId, resumedWithoutHooks: true);
            }

            bool injected = InjectDll(processInfo.hProcess, dllPath);

            if (!injected)
            {
                Log("La inyeccion fallo. El juego se ejecutara en modo DEGRADADO.");
            }

            uint resumeResult = ResumeThread(processInfo.hThread);
            if (resumeResult == unchecked((uint)-1))
            {
                Log($"ResumeThread fallo. Win32Error={Marshal.GetLastPInvokeError()}");
            }

            if (injected)
            {
                OnGameLaunchedHooked?.Invoke(exePath);
                return InjectionResult.SuccessResult(processInfo.dwProcessId);
            }
            else
            {
                OnInjectionFailed?.Invoke("LoadLibraryW remoto fallo o hizo timeout.");
                return InjectionResult.Failure("Fallo la carga remota de la DLL.", processInfo.dwProcessId, resumedWithoutHooks: true);
            }
        }
        finally
        {
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    public bool InjectOnly(uint pid, string dllPath)
    {
        if (!File.Exists(dllPath))
        {
            Log($"DLL no encontrada: {dllPath}");
            return false;
        }

        IntPtr hProcess = OpenProcess(0x1F0FFF, false, pid); // PROCESS_ALL_ACCESS
        if (hProcess == IntPtr.Zero)
        {
            Log($"OpenProcess fallo para PID {pid}. Error: {Marshal.GetLastPInvokeError()}");
            return false;
        }

        try
        {
            if (!IsTarget64Bit(hProcess))
            {
                Log("Proceso objetivo es de 32 bits. SteamOSHooks64.dll es x64-only.");
                return false;
            }

            bool injected = InjectDll(hProcess, dllPath);
            if (injected) Log($"Inyectada DLL en PID {pid} exitosamente.");
            else Log($"Fallo al inyectar DLL en PID {pid}.");
            return injected;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private System.Diagnostics.Process? LaunchNormally(string exePath, string? arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = false
            };
            return System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log($"Fallo al lanzar juego protegido: {ex.Message}");
            return null;
        }
    }

    private bool InjectDll(IntPtr hProcess, string dllPath)
    {
        byte[] dllPathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
        nuint bufferSize = (nuint)dllPathBytes.Length;

        IntPtr remoteBuffer = VirtualAllocEx(hProcess, IntPtr.Zero, bufferSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);

        if (remoteBuffer == IntPtr.Zero)
        {
            Log($"VirtualAllocEx fallo. Win32Error={Marshal.GetLastPInvokeError()}");
            return false;
        }

        try
        {
            if (!WriteProcessMemory(hProcess, remoteBuffer, dllPathBytes, bufferSize, out nuint written)
                || written != bufferSize)
            {
                Log($"WriteProcessMemory fallo. Win32Error={Marshal.GetLastPInvokeError()}");
                return false;
            }

            IntPtr hKernel32 = GetModuleHandleW("kernel32.dll");
            IntPtr loadLibraryAddr = GetProcAddress(hKernel32, "LoadLibraryW");

            if (loadLibraryAddr == IntPtr.Zero)
            {
                Log("No se pudo resolver LoadLibraryW.");
                return false;
            }

            IntPtr hRemoteThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, loadLibraryAddr, remoteBuffer, 0, out _);

            if (hRemoteThread == IntPtr.Zero)
            {
                Log($"CreateRemoteThread fallo. Win32Error={Marshal.GetLastPInvokeError()}");
                return false;
            }

            try
            {
                uint waitResult = WaitForSingleObject(hRemoteThread, InjectionTimeoutMs);

                if (waitResult != WAIT_OBJECT_0)
                {
                    Log($"Wait fallo o timeout. Resultado={waitResult}");
                    return false;
                }

                if (!GetExitCodeThread(hRemoteThread, out uint exitCode) || exitCode == 0)
                {
                    Log("LoadLibraryW devolvio NULL en el proceso remoto.");
                    return false;
                }

                Log($"Inyeccion exitosa. HMODULE=0x{exitCode:X}");
                return true;
            }
            finally
            {
                CloseHandle(hRemoteThread);
            }
        }
        finally
        {
            VirtualFreeEx(hProcess, remoteBuffer, 0, MEM_RELEASE);
        }
    }

    private static bool IsTarget64Bit(IntPtr hProcess)
    {
        if (!Environment.Is64BitOperatingSystem) return false;

        if (IsWow64Process2(hProcess, out ushort processMachine, out _))
        {
            return processMachine == IMAGE_FILE_MACHINE_UNKNOWN;
        }
        return true;
    }
}
