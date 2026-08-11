using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsLikeSteamOS.Services
{
    public class GameProfile
    {
        public int MasterVolume { get; set; } = 80;
        public int FpsLimit { get; set; } = 0;
        public int AaMode { get; set; } = 0; // 0=Off, 1=SMAA, 2=TAA, 3=CMAA2
        public int SharpenMode { get; set; } = 1; // 0=Off, 1=CAS, 2=RCAS
        public float SharpenStrength { get; set; } = 0.2f;
        public bool CrtEnabled { get; set; } = false;
        public float CrtIntensity { get; set; } = 0.5f;

        public GameProfile Clone()
        {
            return (GameProfile)this.MemberwiseClone();
        }
    }

    public class ProfileService
    {
        private const string PROFILES_DIR = @"C:\ProgramData\SteamOS\profiles\";

        public static string ObtenerGameId(string exePath, uint appId = 0)
        {
            if (appId > 0) return appId.ToString();
            return Math.Abs(exePath.ToLowerInvariant().GetHashCode()).ToString();
        }

        private static string GetProfilePath(string gameId)
        {
            return Path.Combine(PROFILES_DIR, $"{gameId}.json");
        }

        public static GameProfile CargarPerfil(string gameId)
        {
            try
            {
                Directory.CreateDirectory(PROFILES_DIR);
                string path = GetProfilePath(gameId);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var profile = JsonSerializer.Deserialize<GameProfile>(json);
                    if (profile != null) return profile;
                }
            }
            catch { }
            return new GameProfile();
        }

        public static async Task SaveAsync(string gameId, GameProfile profile)
        {
            var path = GetProfilePath(gameId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }

        public static bool GuardarPerfil(string gameId, GameProfile profile)
        {
            try
            {
                SaveAsync(gameId, profile).GetAwaiter().GetResult();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public sealed class ProfileSaveDebouncer : IDisposable
    {
        private readonly TimeSpan _delay;
        private readonly Func<string, object, Task> _saveFunc; // (gameId, profileSnapshot) => SaveAsync(...)
        private readonly object _sync = new();

        private CancellationTokenSource? _cts;
        private Task? _pendingTask;

        private string? _pendingGameId;
        private object? _pendingProfileSnapshot;

        public ProfileSaveDebouncer(TimeSpan delay, Func<string, object, Task> saveFunc)
        {
            _delay = delay;
            _saveFunc = saveFunc;
        }

        public void Request(string gameId, object profileSnapshot)
        {
            lock (_sync)
            {
                _pendingGameId = gameId;
                _pendingProfileSnapshot = profileSnapshot;

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                _pendingTask = DebounceWorkerAsync(_cts.Token);
            }
        }

        public async Task FlushAsync()
        {
            Task? taskToWait = null;

            lock (_sync)
            {
                _cts?.Cancel(); 
                taskToWait = _pendingTask;
            }

            if (taskToWait != null)
            {
                try { await taskToWait.ConfigureAwait(false); } catch { /* ignore */ }
            }

            string? gameId;
            object? snap;

            lock (_sync)
            {
                gameId = _pendingGameId;
                snap = _pendingProfileSnapshot;
            }

            if (gameId != null && snap != null)
                await _saveFunc(gameId, snap).ConfigureAwait(false);
        }

        private async Task DebounceWorkerAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(_delay, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            string? gameId;
            object? snap;

            lock (_sync)
            {
                gameId = _pendingGameId;
                snap = _pendingProfileSnapshot;
            }

            if (gameId != null && snap != null)
                await _saveFunc(gameId, snap).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
