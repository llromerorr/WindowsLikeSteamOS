using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

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

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 60)]
        public byte[] reserved;

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
            showOverlay           = 0,
            reserved              = new byte[60]
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
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 13)]
        public uint[] _reservedTelemetry;
    }

    public sealed class SteamOSSharedMemory : IDisposable
    {
        private const string MMF_NAME       = "Local\\SteamOSHooks_IPC_v1";
        private const int    MMF_SIZE       = 4096;
        private const uint   IPC_MAGIC      = 0x53544D53;
        private const uint   LAYOUT_VERSION = 1;

        private readonly MemoryMappedFile          _mmf;
        private readonly MemoryMappedViewAccessor _view;

        private EffectParams _current = EffectParams.CreateDefault();

        private static readonly long OFFSET_SEQUENCE = Marshal.OffsetOf<IPCSharedBlock>(nameof(IPCSharedBlock.sequence)).ToInt64();
        private static readonly long OFFSET_WRITER_LOCK = Marshal.OffsetOf<IPCSharedBlock>(nameof(IPCSharedBlock.writerLock)).ToInt64();
        private static readonly long OFFSET_PARAMS = Marshal.OffsetOf<IPCSharedBlock>(nameof(IPCSharedBlock.@params)).ToInt64();
        private static readonly long OFFSET_TELEMETRY_BACKEND = Marshal.OffsetOf<IPCSharedBlock>(nameof(IPCSharedBlock.detectedBackend)).ToInt64();
        private static readonly long OFFSET_TELEMETRY_FRAMES = Marshal.OffsetOf<IPCSharedBlock>(nameof(IPCSharedBlock.framesRendered)).ToInt64();
        private static readonly long OFFSET_TELEMETRY_FRAMEMS = Marshal.OffsetOf<IPCSharedBlock>(nameof(IPCSharedBlock.lastFrameMs)).ToInt64();

        public SteamOSSharedMemory()
        {
            _mmf  = MemoryMappedFile.CreateOrOpen(MMF_NAME, MMF_SIZE, MemoryMappedFileAccess.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, MMF_SIZE, MemoryMappedFileAccess.ReadWrite);

            _view.Write(0, IPC_MAGIC);
            _view.Write(4, LAYOUT_VERSION);
            _view.Write(OFFSET_SEQUENCE, 0u);
            _view.Write(OFFSET_WRITER_LOCK, 0u);
            WriteParamsInternal(_current);
        }

        private unsafe void WriteParamsInternal(EffectParams p)
        {
            byte* basePtr = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
            try
            {
                int* pLock = (int*)(basePtr + OFFSET_WRITER_LOCK);
                int* pSeq  = (int*)(basePtr + OFFSET_SEQUENCE);

                int spins = 0;
                while (Interlocked.CompareExchange(ref *pLock, 1, 0) != 0)
                {
                    if (++spins > 10_000)
                    {
                        Interlocked.Exchange(ref *pLock, 0);
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
            finally
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
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

        public EffectParams ReadCurrentParams()
        {
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
            return (
                _view.ReadUInt32(OFFSET_TELEMETRY_BACKEND),
                _view.ReadUInt32(OFFSET_TELEMETRY_FRAMES),
                _view.ReadSingle(OFFSET_TELEMETRY_FRAMEMS)
            );
        }

        public string GetBackendName()
        {
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
