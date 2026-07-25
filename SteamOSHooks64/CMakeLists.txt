cmake_minimum_required(VERSION 3.14)
project(SteamOSHooks64 LANGUAGES CXX C)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Recomendado para DLLs en Windows
set(CMAKE_SHARED_LINKER_FLAGS "${CMAKE_SHARED_LINKER_FLAGS} /SUBSYSTEM:WINDOWS")

include(FetchContent)

# 1. Descargar y compilar MinHook automáticamente
FetchContent_Declare(
    minhook
    GIT_REPOSITORY https://github.com/TsudaKageyu/minhook.git
    GIT_TAG v1.3.3
)
FetchContent_MakeAvailable(minhook)

# 2. Descargar ImGui automáticamente
FetchContent_Declare(
    imgui
    GIT_REPOSITORY https://github.com/ocornut/imgui.git
    GIT_TAG v1.90.8
)
FetchContent_MakeAvailable(imgui)

# Recopilar los archivos fuente de MinHook
set(MINHOOK_DIR ${minhook_SOURCE_DIR})
set(MINHOOK_SOURCES
    ${MINHOOK_DIR}/src/buffer.c
    ${MINHOOK_DIR}/src/hook.c
    ${MINHOOK_DIR}/src/trampoline.c
    ${MINHOOK_DIR}/src/hde/hde32.c
    ${MINHOOK_DIR}/src/hde/hde64.c
)

# Recopilar los archivos fuente de ImGui que necesitamos (Core + Win32 + DX11 + DX12)
set(IMGUI_DIR ${imgui_SOURCE_DIR})
set(IMGUI_SOURCES
    ${IMGUI_DIR}/imgui.cpp
    ${IMGUI_DIR}/imgui_draw.cpp
    ${IMGUI_DIR}/imgui_tables.cpp
    ${IMGUI_DIR}/imgui_widgets.cpp
    ${IMGUI_DIR}/backends/imgui_impl_win32.cpp
    ${IMGUI_DIR}/backends/imgui_impl_dx11.cpp
    ${IMGUI_DIR}/backends/imgui_impl_dx12.cpp
)

# 3. Recopilar nuestro código fuente
file(GLOB SOURCES "*.cpp")
file(GLOB HEADERS "*.h")

# Crear la DLL combinando nuestro código + ImGui + MinHook
add_library(SteamOSHooks64 SHARED ${SOURCES} ${HEADERS} ${IMGUI_SOURCES} ${MINHOOK_SOURCES})

# Configurar directorios de inclusión (para que #include <imgui.h> funcione)
target_include_directories(SteamOSHooks64 PRIVATE 
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${IMGUI_DIR}
    ${IMGUI_DIR}/backends
    ${minhook_SOURCE_DIR}/include
)

# Enlazar las librerías del sistema de Windows/DirectX
target_link_libraries(SteamOSHooks64 PRIVATE
    d3d11
    d3d12
    dxgi
    d3dcompiler
)
