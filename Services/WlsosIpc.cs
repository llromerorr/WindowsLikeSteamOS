using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsLikeSteamOS.Services
{
    [StructLayout(LayoutKind.Sequential)]
    public struct HostToAddonState
    {
        public uint protocol_version;
        public uint _pad0;
        public ulong host_pid;
        public ulong host_heartbeat;
        public uint seq;

        public byte overlay_visible;
        public byte fsr_enabled;
        public byte crt_enabled;
        public byte _pad1;
        public float fsr_sharpness;
        public float crt_intensity;
        
        public unsafe fixed byte reserved[64];
    }

    public enum PowerAction : byte
    {
        None = 0,
        Suspend = 1,
        Hibernate = 2,
        Restart = 3,
        Shutdown = 4,
        Desktop = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AddonToHostState
    {
        public uint protocol_version;
        public uint _pad0;
        public ulong addon_pid;
        public ulong addon_heartbeat;
        public uint seq;

        public uint request_epoch;
        public byte desired_fsr_mode;     // 0 = OFF, 1 = 720p, 2 = 900p
        public byte desired_fps_limit;    // 0 = OFF, 30, 45, 60
        public byte requested_volume;     // 0 a 100%
        public byte requested_power_action; // enum PowerAction
        public float desired_fsr_sharpness;
        public float desired_crt_intensity;
        
        public unsafe fixed byte reserved[64];
    }

    public class WlsosIpc : IDisposable
    {
        private const uint IPC_PROTOCOL_VERSION = 1;
        private const string MMF_H2A_PREFIX = "Local\\WLSOS_IPC_H2A_";
        private const string MMF_A2H_PREFIX = "Local\\WLSOS_IPC_A2H_";
        private const int MMF_SIZE = 4096;

        private MemoryMappedFile _h2aMmf;
        private MemoryMappedViewAccessor _h2aView;
        
        private MemoryMappedFile _a2hMmf;
        private MemoryMappedViewAccessor _a2hView;

        private OverlayTextureWriter _textureWriter;

        private CancellationTokenSource _cts;
        private Task _pollingTask;

        private ulong _currentHostHeartbeat = 0;
        private uint _lastProcessedEpoch = 0;
        
        private ulong _lastAddonHeartbeat = 0;
        private long _lastAddonHeartbeatChangeTimeMs = 0;

        // Host master state
        private bool _overlayVisible = false;
        private bool _fsrEnabled = false;
        private float _fsrSharpness = 0.5f;
        private bool _crtEnabled = false;
        private float _crtIntensity = 0.15f;
        
        private int _targetPid;
        private ulong _hostPid;

        public event Action<bool> OnFSRChanged;
        public event Action<float> OnFSRSharpnessChanged;
        public event Action<bool> OnCRTChanged;
        public event Action<float> OnCRTIntensityChanged;

        public event Action<byte> OnVolumeRequested;
        public event Action<PowerAction> OnPowerActionRequested;
        public event Action<byte> OnFSRModeRequested;
        public event Action<byte> OnFPSLimitRequested;

        public WlsosIpc(int targetPid)
        {
            _targetPid = targetPid;
            _hostPid = (ulong)Environment.ProcessId;
            
            _h2aMmf = MemoryMappedFile.CreateOrOpen(MMF_H2A_PREFIX + targetPid, MMF_SIZE, MemoryMappedFileAccess.ReadWrite);
            _h2aView = _h2aMmf.CreateViewAccessor(0, MMF_SIZE, MemoryMappedFileAccess.ReadWrite);

            _a2hMmf = MemoryMappedFile.CreateOrOpen(MMF_A2H_PREFIX + targetPid, MMF_SIZE, MemoryMappedFileAccess.ReadWrite);
            _a2hView = _a2hMmf.CreateViewAccessor(0, MMF_SIZE, MemoryMappedFileAccess.ReadWrite);

            // Initialize protocol versions
            _h2aView.Write(0, IPC_PROTOCOL_VERSION);
            _a2hView.Write(0, IPC_PROTOCOL_VERSION);

            // Force initial ephemeral state and perform synchronous first write
            _overlayVisible = false;
            _fsrEnabled = false;
            _fsrSharpness = 0.5f;
            _crtEnabled = false;
            _crtIntensity = 0.15f;
            _currentHostHeartbeat = 1;
            WriteHostToAddonState();

            _textureWriter = new OverlayTextureWriter();
            _textureWriter.Attach(targetPid);

            _cts = new CancellationTokenSource();
            _pollingTask = Task.Run(PollingLoop, _cts.Token);
        }

        public void WriteOverlayTexture(byte[] pixels, int width, int height, bool visible)
        {
            _textureWriter?.WriteTexture(pixels, width, height, visible);
        }

        public bool IsAddonAlive(long timeoutMs = 3000)
        {
            if (_lastAddonHeartbeat == 0) return false;
            return (Environment.TickCount64 - _lastAddonHeartbeatChangeTimeMs) < timeoutMs;
        }

        private async Task PollingLoop()
        {
            var offsetSeq = Marshal.OffsetOf<AddonToHostState>(nameof(AddonToHostState.seq)).ToInt64();
            var offsetState = 0; // The whole struct

            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    // 1. Update Heartbeat and Write H2A state
                    _currentHostHeartbeat++;
                    WriteHostToAddonState();

                    // 2. Poll A2H state
                    if (_a2hView != null && !_a2hView.SafeMemoryMappedViewHandle.IsClosed)
                    {
                        for (int attempt = 0; attempt < 8; attempt++)
                        {
                            uint seq1 = _a2hView.ReadUInt32(offsetSeq);
                            if ((seq1 & 1) != 0) 
                            {
                                Thread.SpinWait(10);
                                continue;
                            }

                            _a2hView.Read(offsetState, out AddonToHostState a2h);

                            uint seq2 = _a2hView.ReadUInt32(offsetSeq);
                            if (seq1 == seq2)
                            {
                                // Valid read
                                ProcessAddonState(a2h);
                                break;
                            }
                        }
                    }

                    await Task.Delay(16, _cts.Token); // ~60Hz polling
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WLSOS IPC Error]: {ex.Message}");
            }
        }

        private void ProcessAddonState(AddonToHostState a2h)
        {
            if (a2h.protocol_version != IPC_PROTOCOL_VERSION) return;
            
            // Watchdog heartbeat track
            if (a2h.addon_heartbeat != _lastAddonHeartbeat)
            {
                _lastAddonHeartbeat = a2h.addon_heartbeat;
                _lastAddonHeartbeatChangeTimeMs = Environment.TickCount64;
            }

            if (a2h.request_epoch != _lastProcessedEpoch)
            {
                _lastProcessedEpoch = a2h.request_epoch;

                if (a2h.requested_volume <= 100)
                {
                    OnVolumeRequested?.Invoke(a2h.requested_volume);
                }

                if (a2h.requested_power_action != (byte)PowerAction.None)
                {
                    OnPowerActionRequested?.Invoke((PowerAction)a2h.requested_power_action);
                }

                OnFSRModeRequested?.Invoke(a2h.desired_fsr_mode);
                OnFPSLimitRequested?.Invoke(a2h.desired_fps_limit);
            }
        }

        private unsafe void WriteHostToAddonState()
        {
            if (_h2aView == null || _h2aView.SafeMemoryMappedViewHandle.IsClosed) return;

            byte* ptr = null;
            try
            {
                _h2aView.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                
                // Pointers to fields
                uint* pSeq = (uint*)(ptr + Marshal.OffsetOf<HostToAddonState>(nameof(HostToAddonState.seq)).ToInt64());
                
                uint seq = Volatile.Read(ref *pSeq) & ~1u;
                Volatile.Write(ref *pSeq, seq + 1); // Release to odd
                Thread.MemoryBarrier();
                
                // Write payload
                HostToAddonState* pState = (HostToAddonState*)ptr;
                pState->protocol_version = IPC_PROTOCOL_VERSION;
                pState->host_pid = _hostPid;
                pState->host_heartbeat = _currentHostHeartbeat;
                
                pState->overlay_visible = (byte)(_overlayVisible ? 1 : 0);
                pState->fsr_enabled = (byte)(_fsrEnabled ? 1 : 0);
                pState->fsr_sharpness = _fsrSharpness;
                pState->crt_enabled = (byte)(_crtEnabled ? 1 : 0);
                pState->crt_intensity = _crtIntensity;

                Thread.MemoryBarrier();
                Volatile.Write(ref *pSeq, seq + 2); // Release to even
            }
            catch (ObjectDisposedException) { }
            finally
            {
                if (ptr != null)
                {
                    _h2aView.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
        
        // Public API for Host to change state
        public void SetOverlayVisible(bool visible) { _overlayVisible = visible; WriteHostToAddonState(); }
        public void SetFSR(bool enabled, float sharpness) { _fsrEnabled = enabled; _fsrSharpness = Math.Clamp(sharpness, 0f, 1f); WriteHostToAddonState(); }
        public void SetCRT(bool enabled, float intensity) { _crtEnabled = enabled; _crtIntensity = Math.Clamp(intensity, 0f, 1f); WriteHostToAddonState(); }

        public void Dispose()
        {
            try
            {
                _cts?.Cancel();
                _pollingTask?.Wait(500);
            }
            catch { }
            
            _cts?.Dispose();
            
            _textureWriter?.Dispose();

            _h2aView?.Dispose();
            _h2aMmf?.Dispose();
            
            _a2hView?.Dispose();
            _a2hMmf?.Dispose();
        }
    }
}
