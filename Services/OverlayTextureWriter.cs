using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowsLikeSteamOS.Services
{
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct OverlayTextureHeader
    {
        [FieldOffset(0)] public uint seq;
        [FieldOffset(4)] public uint width;
        [FieldOffset(8)] public uint height;
        [FieldOffset(12)] public uint stride;
        [FieldOffset(16)] public byte visible;
        [FieldOffset(17)] public byte pad1;
        [FieldOffset(18)] public byte pad2;
        [FieldOffset(19)] public byte pad3;
        [FieldOffset(20)] public float pos_x;
        [FieldOffset(24)] public float pos_y;
        [FieldOffset(28)] public float scale;
        [FieldOffset(32)] public uint frame_id;
    }

    public class OverlayTextureWriter : IDisposable
    {
        private const string MMF_PREFIX = "Local\\WLSOS_OVERLAY_TEX_";
        private const int MAX_WIDTH = 512;
        private const int MAX_HEIGHT = 800;
        private const int HEADER_SIZE = 64;
        private const int MAX_PIXELS = MAX_WIDTH * MAX_HEIGHT;
        private const int MMF_SIZE = HEADER_SIZE + (MAX_PIXELS * 4);

        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _accessor;
        private uint _frameId = 0;
        private int _currentPid = 0;
        
        public bool IsAttached => _mmf != null;

        public void Attach(int pid)
        {
            if (_currentPid == pid && _mmf != null) return;
            
            Detach();
            
            string mmfName = $"{MMF_PREFIX}{pid}";
            try
            {
                _mmf = MemoryMappedFile.CreateOrOpen(mmfName, MMF_SIZE, MemoryMappedFileAccess.ReadWrite);
                _accessor = _mmf.CreateViewAccessor(0, MMF_SIZE, MemoryMappedFileAccess.ReadWrite);
                _currentPid = pid;
                SteamOSConfigurator.Logger.Log($"[OverlayTextureWriter] Attached to MMF: {mmfName}");
            }
            catch (Exception ex)
            {
                SteamOSConfigurator.Logger.Log($"[OverlayTextureWriter] Error attaching to MMF: {ex.Message}");
                Detach();
            }
        }

        public void Detach()
        {
            _accessor?.Dispose();
            _accessor = null;
            _mmf?.Dispose();
            _mmf = null;
            _currentPid = 0;
        }

        public void WriteTexture(byte[] pixels, int width, int height, bool visible, float posX = 0.85f, float posY = 0.5f, float scale = 1.0f)
        {
            if (_accessor == null || pixels == null || pixels.Length == 0) return;

            // Limitar al máximo soportado
            if (width > MAX_WIDTH) width = MAX_WIDTH;
            if (height > MAX_HEIGHT) height = MAX_HEIGHT;

            int stride = width * 4;
            int sizeToCopy = height * stride;
            if (sizeToCopy > MAX_PIXELS * 4) sizeToCopy = MAX_PIXELS * 4;
            if (sizeToCopy > pixels.Length) sizeToCopy = pixels.Length;

            _frameId++;

            // 1. Iniciar escritura (seq impar)
            OverlayTextureHeader header = new OverlayTextureHeader();
            _accessor.Read(0, out header);
            
            header.seq = (header.seq & ~1u) + 1; // Hacer impar
            _accessor.Write(0, ref header);

            // 2. Copiar payload (píxeles) a partir del offset 64
            _accessor.WriteArray(HEADER_SIZE, pixels, 0, sizeToCopy);

            // 3. Finalizar escritura (seq par) con nuevos datos
            header.width = (uint)width;
            header.height = (uint)height;
            header.stride = (uint)stride;
            header.visible = visible ? (byte)1 : (byte)0;
            header.pos_x = posX;
            header.pos_y = posY;
            header.scale = scale;
            header.frame_id = _frameId;
            header.seq++; // Vuelve a par

            _accessor.Write(0, ref header);
        }

        public void SetVisibility(bool visible)
        {
            if (_accessor == null) return;
            
            OverlayTextureHeader header;
            _accessor.Read(0, out header);
            
            header.seq = (header.seq & ~1u) + 1; // Impar
            _accessor.Write(0, ref header);
            
            header.visible = visible ? (byte)1 : (byte)0;
            header.seq++; // Par
            _accessor.Write(0, ref header);
        }

        public void Dispose()
        {
            Detach();
        }
    }
}
