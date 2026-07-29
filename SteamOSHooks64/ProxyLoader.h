#pragma once
#include <windows.h>

namespace ProxyLoader {
    bool Initialize();
    void Shutdown();
    HMODULE GetRealModule();
    FARPROC GetRealProcAddress(const char* procName);
}
