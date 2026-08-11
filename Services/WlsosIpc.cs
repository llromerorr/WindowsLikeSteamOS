using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsLikeSteamOS.Services
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public unsafe struct HostToAddonState
    {
        public uint protocol_version;
        public uint _pad0;
        public ulong host_pid;
        public ulong host_heartbeat;
        public uint seq;

        public byte overlay_visible;
        public byte aa_mode;
        public byte sharpen_mode;
        public byte crt_enabled;

        public float master_volume;
        public uint fps_limit;
        public float sharpen_strength;
        public float crt_intensity;

        public float taa_jitter;
        public float taa_seeking;
        public float cmaa2_edge_threshold;

        public fixed byte reserved[68];
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

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public unsafe struct AddonToHostState
    {
        public uint protocol_version;
        public uint _pad0;
        public ulong addon_pid;
        public ulong addon_heartbeat;
        public uint seq;

        public uint request_epoch;
        public uint request_mask;

        public byte desired_overlay_visible;
        public byte desired_aa_mode;
        public byte desired_sharpen_mode;
        public byte desired_crt_enabled;

        public float desired_master_volume;
        public uint desired_fps_limit;
        public float desired_sharpen_strength;
        public float desired_crt_intensity;

        public float desired_taa_jitter;
        public float desired_taa_seeking;
        public float desired_cmaa2_edge_threshold;

        public byte requested_power_action;
        public fixed byte reserved[55];
    }

    public enum IpcRequestMask : uint
    {
        REQ_OVERLAY   = 1u << 0,
        REQ_VOLUME    = 1u << 1,
        REQ_FPS_LIMIT = 1u << 2,
        REQ_AA        = 1u << 3,
        REQ_SHARPEN   = 1u << 4,
        REQ_CRT       = 1u << 5,
        REQ_POWER     = 1u << 6,
        REQ_RESERVED7 = 1u << 7
    }

    public class WlsosIpc : IDisposable
    {
        private const uint IPC_PROTOCOL_VERSION = 2;
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
        public event Action<byte> OnAAModeRequested;
        public event Action<byte> OnSharpenModeRequested;
        public event Action<byte> OnCRTModeRequested;
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
                uint mask = a2h.request_mask;

                if ((mask & (uint)IpcRequestMask.REQ_VOLUME) != 0)
                {
                    OnVolumeRequested?.Invoke((byte)(Math.Clamp(a2h.desired_master_volume, 0f, 1f) * 100f));
                }

                if ((mask & (uint)IpcRequestMask.REQ_AA) != 0)
                {
                    OnAAModeRequested?.Invoke(a2h.desired_aa_mode);
                }

                if ((mask & (uint)IpcRequestMask.REQ_SHARPEN) != 0)
                {
                    OnSharpenModeRequested?.Invoke(a2h.desired_sharpen_mode);
                    // could also trigger event for sharpen strength if needed
                }

                if ((mask & (uint)IpcRequestMask.REQ_CRT) != 0)
                {
                    OnCRTModeRequested?.Invoke(a2h.desired_crt_enabled);
                    // could also trigger event for crt intensity if needed
                }

                if ((mask & (uint)IpcRequestMask.REQ_FPS_LIMIT) != 0)
                {
                    OnFPSLimitRequested?.Invoke((byte)a2h.desired_fps_limit);
                }

                if ((mask & (uint)IpcRequestMask.REQ_POWER) != 0)
                {
                    OnPowerActionRequested?.Invoke((PowerAction)a2h.requested_power_action);
                }
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
                pState->aa_mode = (byte)_aaMode;
                pState->sharpen_mode = (byte)_sharpenMode;
                pState->crt_enabled = (byte)(_crtEnabled ? 1 : 0);
                
                pState->master_volume = _masterVolume;
                pState->fps_limit = _fpsLimit;
                pState->sharpen_strength = _sharpenStrength;
                pState->crt_intensity = _crtIntensity;

                pState->taa_jitter = 0.5f; // Default for now
                pState->taa_seeking = 0.1f; // Default for now
                pState->cmaa2_edge_threshold = 0.05f; // Default for now

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
        
        // Internal state backing fields
        private int _aaMode = 0;
        private int _sharpenMode = 0;
        private float _masterVolume = 0.8f;
        private uint _fpsLimit = 0;
        private float _sharpenStrength = 0.5f;

        // Public API for Host to change state
        public void SetOverlayVisible(bool visible) { _overlayVisible = visible; WriteHostToAddonState(); }
        public void SetMasterState(float volume, uint fpsLimit, int aaMode, int sharpenMode, float sharpenStrength, bool crtEnabled, float crtIntensity)
        {
            _masterVolume = volume;
            _fpsLimit = fpsLimit;
            _aaMode = aaMode;
            _sharpenMode = sharpenMode;
            _sharpenStrength = sharpenStrength;
            _crtEnabled = crtEnabled;
            _crtIntensity = crtIntensity;
            WriteHostToAddonState();
        }

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
