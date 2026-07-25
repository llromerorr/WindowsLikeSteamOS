using System;
using System.Collections.Generic;
using System.IO;

namespace WindowsLikeSteamOS.Injection;

public sealed class AntiCheatGuard : IAntiCheatGuard
{
    private readonly HashSet<string> _blacklistedExeNames;

    private static readonly string[] _antiCheatFolderMarkers =
    {
        "EasyAntiCheat",
        "BattlEye",
        "vanguard",
        "PunkBuster",
    };

    public AntiCheatGuard(IEnumerable<string>? knownProtectedExeNames = null)
    {
        _blacklistedExeNames = new HashSet<string>(
            knownProtectedExeNames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool IsGameProtected(string exePath)
    {
        string exeName = Path.GetFileName(exePath);

        if (_blacklistedExeNames.Contains(exeName))
            return true;

        string? gameDir = Path.GetDirectoryName(exePath);
        if (gameDir is null || !Directory.Exists(gameDir))
            return false;

        foreach (var marker in _antiCheatFolderMarkers)
        {
            if (Directory.Exists(Path.Combine(gameDir, marker)))
                return true;

            string? parentDir = Directory.GetParent(gameDir)?.FullName;
            if (parentDir != null && Directory.Exists(Path.Combine(parentDir, marker)))
                return true;
        }

        return false;
    }
}
