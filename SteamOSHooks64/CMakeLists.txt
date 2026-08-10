cmake_minimum_required(VERSION 3.14)
project(WLSOS_Addon LANGUAGES CXX C)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(RESHADE_INC "C:/Users/Luis Romero/GitHub/reshade/include")

include(FetchContent)
FetchContent_Declare(
    minhook
    GIT_REPOSITORY https://github.com/TsudaKageyu/minhook.git
    GIT_TAG v1.3.3
)
FetchContent_MakeAvailable(minhook)

set(MINHOOK_DIR ${minhook_SOURCE_DIR})
set(MINHOOK_SOURCES
    ${MINHOOK_DIR}/src/buffer.c
    ${MINHOOK_DIR}/src/hook.c
    ${MINHOOK_DIR}/src/trampoline.c
    ${MINHOOK_DIR}/src/hde/hde32.c
    ${MINHOOK_DIR}/src/hde/hde64.c
)

add_library(WLSOS SHARED 
    addon_main.cpp
    XInputHooks.cpp
    ${MINHOOK_SOURCES}
)
set_target_properties(WLSOS PROPERTIES SUFFIX ".addon")

target_include_directories(WLSOS PRIVATE 
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${RESHADE_INC}
    "C:/Users/Luis Romero/GitHub/reshade/deps/imgui"
    ${minhook_SOURCE_DIR}/include
)

target_link_libraries(WLSOS PRIVATE
    d3d11
    dxgi
)
