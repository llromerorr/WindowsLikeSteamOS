namespace WindowsLikeSteamOS.Injection;

public interface IAntiCheatGuard
{
    bool IsGameProtected(string exePath);
}
