using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using SteamOSConfigurator;

namespace WindowsLikeSteamOS.Services
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EffectParams
    {
        public uint  enablePostProcess;
        public uint  enableCRT;
        public float curvature;
        public float scanlineIntensity;
        public uint  enableFSR;
        public float fsrSharpness;
        public uint  enableResolutionSpoof;
        public uint  fakeWidth;
        public uint  fakeHeight;
        public uint  showOverlay;

        public uint reserved0;
        public uint reserved1;
        public uint reserved2;
        public uint reserved3;
        public uint reserved4;
        public uint reserved5;
        public uint reserved6;
        public uint reserved7;
        public uint reserved8;
        public uint reserved9;
        public uint reserved10;
        public uint reserved11;
        public uint reserved12;
        public uint reserved13;
        public uint reserved14;

        public static EffectParams CreateDefault() => new EffectParams
        {
            enablePostProcess     = 1,
            enableCRT             = 0,
            curvature             = 6.0f,
            scanlineIntensity     = 0.15f,
            enableFSR             = 0,
            fsrSharpness          = 0.5f,
            enableResolutionSpoof = 0,
            fakeWidth             = 1920,
            fakeHeight            = 1080,
            showOverlay           = 0
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IPCSharedBlock
    {
        public uint         magic;
        public uint         layoutVersion;
        public uint         sequence;
        public uint         writerLock;
        public EffectParams @params;
        public uint         detectedBackend;
        public uint         framesRendered;
        public float        lastFrameMs;
        public uint _reservedTelemetry0;
        public uint _reservedTelemetry1;
        public uint _reservedTelemetry2;
        public uint _reservedTelemetry3;
        public uint _reservedTelemetry4;
        public uint _reservedTelemetry5;
        public uint _reservedTelemetry6;
        public uint _reservedTelemetry7;
        public uint _reservedTelemetry8;
        public uint _reservedTelemetry9;
        public uint _reservedTelemetry10;
        public uint _reservedTelemetry11;
        public uint _reservedTelemetry12;
    }

    public sealed class SteamOSSharedMemory : IDisposable
    {
        private const string MMF_NAME       = "Local\\SteamOSHooks_IPC_v2";
        private const int    MMF_SIZE       = 4096;
        private const uint   IPC_MAGIC      = 0x53544D53;
        private const uint   LAYOUT_VERSION = 2;

        private static readonly Lazy<SteamOSSharedMemory> _lazyInstance = new Lazy<SteamOSSharedMemory>(() => new SteamOSSharedMemory());
        public static SteamOSSharedMemory Instance => _lazyInstance.Value;

        private MemoryMappedFile?          _mmf;
        private MemoryMappedViewAccessor? _view;

        private EffectParams _current = EffectParams.CreateDefault();

        private static readonly long OFFSET_SEQUENCE = Marshal.OffsetOf<IPCSharedBlock>(nameof(IPCSharedBlock.sequence)).ToInt64();
        private static readonly long OFFSET_WRITER_LOCK = Marshal.OffsetOf<IPCSharedBlock>(nameof(IPCSharedBlock.writerLock)).ToInt64();
        private static readonly long OFFSET_PARAMS = Marshal.OffsetOf<IPCSharedBlock>(nameof(IPCSharedBlock.@params)).ToInt64();
        private static readonly long OFFSET_TELEMETRY_BACKEND = Marshal.OffsetOf<IPCSharedBlock>(nameof(IPCSharedBlock.detectedBackend)).ToInt64();
        private static readonly long OFFSET_TELEMETRY_FRAMES = Marshal.OffsetOf<IPCSharedBlock>(nameof(IPCSharedBlock.framesRendered)).ToInt64();
        private static readonly long OFFSET_TELEMETRY_FRAMEMS = Marshal.OffsetOf<IPCSharedBlock>(nameof(IPCSharedBlock.lastFrameMs)).ToInt64();

        private SteamOSSharedMemory()
        {
            EnsureIPCConnected();
        }

        private bool EnsureIPCConnected()
        {
            if (_view != null && _mmf != null) return true;

            try
            {
                _mmf = MemoryMappedFile.CreateOrOpen(MMF_NAME, MMF_SIZE, MemoryMappedFileAccess.ReadWrite);
                _view = _mmf.CreateViewAccessor(0, MMF_SIZE, MemoryMappedFileAccess.ReadWrite);

                _view.Write(0, IPC_MAGIC);
                _view.Write(4, LAYOUT_VERSION);
                _view.Write(OFFSET_SEQUENCE, 0u);
                _view.Write(OFFSET_WRITER_LOCK, 0u);
                WriteParamsInternal(_current);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamOSSharedMemory] Error al conectar IPC: {ex.Message}");
                _view?.Dispose();
                _mmf?.Dispose();
                _view = null;
                _mmf = null;
                return false;
            }
        }

        private unsafe void WriteParamsInternal(EffectParams p)
        {
            if (!EnsureIPCConnected() || _view == null) return;
            byte* basePtr = null;
            try
            {
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                int* pLock = (int*)(basePtr + OFFSET_WRITER_LOCK);
                int* pSeq  = (int*)(basePtr + OFFSET_SEQUENCE);

                int spins = 0;
                while (Interlocked.CompareExchange(ref *pLock, 1, 0) != 0)
                {
                    if (++spins > 500)
                    {
                        Interlocked.Exchange(ref *pLock, 0);
                        break;
                    }
                    Thread.SpinWait(20);
                }

                int seq = *pSeq;
                Volatile.Write(ref *pSeq, seq + 1);
                Thread.MemoryBarrier();

                _view.Write(OFFSET_PARAMS, ref p);

                Thread.MemoryBarrier();
                Volatile.Write(ref *pSeq, seq + 2);

                Volatile.Write(ref *pLock, 0);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamOSSharedMemory] Error en WriteParamsInternal: {ex.Message}");
            }
            finally
            {
                if (basePtr != null)
                {
                    _view.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }

        public void SetCRT(bool enabled, float curvature, float scanlines)
        {
            _current.enableCRT         = enabled ? 1u : 0u;
            _current.curvature         = curvature;
            _current.scanlineIntensity = scanlines;
            WriteParamsInternal(_current);
        }

        public void SetPostProcessEnabled(bool enabled)
        {
            _current.enablePostProcess = enabled ? 1u : 0u;
            WriteParamsInternal(_current);
        }

        public void SetFSR(bool enabled, float sharpness)
        {
            _current.enableFSR    = enabled ? 1u : 0u;
            _current.fsrSharpness = sharpness;
            WriteParamsInternal(_current);
        }

        public void SetResolutionSpoof(bool enabled, uint fakeW, uint fakeH)
        {
            _current.enableResolutionSpoof = enabled ? 1u : 0u;
            _current.fakeWidth  = fakeW;
            _current.fakeHeight = fakeH;
            WriteParamsInternal(_current);
        }

        public void SetCurvatureLive(float value)
        {
            _current.curvature = value;
            WriteParamsInternal(_current);
        }

        public void ToggleOverlay(bool visible)
        {
            _current.showOverlay = visible ? 1u : 0u;
            WriteParamsInternal(_current);
        }

        public (IntPtr handle, uint width, uint height, long adapterLuid) ReadSharedTextureInfo()
        {
            EffectParams p = ReadCurrentParams();
            ulong hLow = p.reserved0;
            ulong hHigh = p.reserved1;
            ulong handleVal = hLow | (hHigh << 32);
            
            ulong luidLow = p.reserved4;
            ulong luidHigh = p.reserved5;
            long luid = (long)(luidLow | (luidHigh << 32));
            
            return ((IntPtr)handleVal, p.reserved2, p.reserved3, luid);
        }

        public EffectParams ReadCurrentParams()
        {
            if (!EnsureIPCConnected() || _view == null) return _current;
            var pSeqOffset = OFFSET_SEQUENCE;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint seq1 = _view.ReadUInt32(pSeqOffset);
                if ((seq1 & 1) != 0) { Thread.SpinWait(10); continue; }

                _view.Read(OFFSET_PARAMS, out EffectParams temp);

                uint seq2 = _view.ReadUInt32(pSeqOffset);
                if (seq1 == seq2)
                {
                    _current = temp;
                    return temp;
                }
            }
            return _current;
        }

        public (uint backend, uint frames, float lastFrameMs) ReadTelemetry()
        {
            if (!EnsureIPCConnected() || _view == null) return (0, 0, 0f);
            return (
                _view.ReadUInt32(OFFSET_TELEMETRY_BACKEND),
                _view.ReadUInt32(OFFSET_TELEMETRY_FRAMES),
                _view.ReadSingle(OFFSET_TELEMETRY_FRAMEMS)
            );
        }

        public string GetBackendName()
        {
            if (!EnsureIPCConnected() || _view == null) return "No detectado";
            uint b = _view.ReadUInt32(OFFSET_TELEMETRY_BACKEND);
            return b switch
            {
                11 => "DirectX 11",
                12 => "DirectX 12",
                _  => "No detectado"
            };
        }

        public void Dispose()
        {
            _view?.Dispose();
            _mmf?.Dispose();
        }
    }
}
