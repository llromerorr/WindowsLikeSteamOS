#pragma once
#include <cstdint>
#include <atomic>

namespace wls
{
    constexpr wchar_t OVERLAY_TEX_MMF_PREFIX[] = L"Local\\WLSOS_OVERLAY_TEX_";
    
    // Tamaño máximo de textura soportado (512 x 800 @ BGRA = 1,638,400 bytes)
    constexpr uint32_t OVERLAY_TEX_MAX_WIDTH  = 512;
    constexpr uint32_t OVERLAY_TEX_MAX_HEIGHT = 800;
    constexpr uint32_t OVERLAY_TEX_MAX_PIXELS = OVERLAY_TEX_MAX_WIDTH * OVERLAY_TEX_MAX_HEIGHT;
    constexpr uint32_t OVERLAY_TEX_HEADER_SIZE = 64;  // Alineado a 64 bytes
    constexpr uint32_t OVERLAY_TEX_MMF_SIZE = OVERLAY_TEX_HEADER_SIZE + (OVERLAY_TEX_MAX_PIXELS * 4);
    
    struct OverlayTextureHeader
    {
        std::atomic<uint32_t> seq;     // Seqlock (par=listo, impar=escribiendo)
        uint32_t width;                // Ancho real del frame actual (<=512)
        uint32_t height;               // Alto real del frame actual (<=800)
        uint32_t stride;               // Bytes por fila (width * 4)
        uint8_t  visible;              // 1 = mostrar overlay, 0 = ocultar
        uint8_t  _pad[3];
        float    pos_x;                // Posición X normalizada [0..1] en pantalla
        float    pos_y;                // Posición Y normalizada [0..1] en pantalla
        float    scale;                // Escala del overlay (1.0 = tamaño real)
        uint32_t frame_id;             // Incrementa cuando hay un nuevo frame
        uint8_t  reserved[28];         // Padding hasta 64 bytes
    };
    
    // Después del header (offset 64): width * height * 4 bytes de píxeles BGRA
    static_assert(sizeof(OverlayTextureHeader) == OVERLAY_TEX_HEADER_SIZE, "OverlayTextureHeader must be exactly 64 bytes");
}
