namespace WindowsLikeSteamOS.Injection;

public sealed class InjectionResult
{
    public bool   Success                { get; init; }
    public bool   WasProtectedByAntiCheat { get; init; }
    public bool   RunningWithoutHooks     { get; init; }
    public int    ProcessId               { get; init; } = -1;
    public string Message                 { get; init; } = string.Empty;

    public static InjectionResult SuccessResult(int pid) => new()
    {
        Success = true,
        ProcessId = pid,
        Message = "DLL inyectada correctamente antes del primer frame."
    };

    public static InjectionResult ProtectedLaunch(int pid) => new()
    {
        Success = true,
        WasProtectedByAntiCheat = true,
        ProcessId = pid,
        Message = "Juego protegido por Anti-Cheat. Ejecutado en modo nativo (sin overlay)."
    };

    public static InjectionResult Failure(string message, int pid = -1, bool resumedWithoutHooks = false) => new()
    {
        Success = false,
        ProcessId = pid,
        RunningWithoutHooks = resumedWithoutHooks,
        Message = message
    };
}
