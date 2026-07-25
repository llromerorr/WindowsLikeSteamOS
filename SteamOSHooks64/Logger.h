#pragma once
#include <windows.h>
#include <cstdio>
#include <cstdarg>

namespace Logger {
    inline void Log(const char* fmt, ...) {
        char buffer[1024];
        va_list args;
        va_start(args, fmt);
        vsnprintf(buffer, sizeof(buffer), fmt, args);
        va_end(args);

        char final[1100];
        sprintf_s(final, "[SteamOSHooks] %s\n", buffer);
        OutputDebugStringA(final); // Visible con DebugView / Visual Studio

        // Opcional: log a archivo en %TEMP%
        FILE* f = nullptr;
        if (fopen_s(&f, "C:\\ProgramData\\SteamOS\\SteamOSHooks_Log.txt", "a") == 0 && f) {
            fputs(final, f);
            fclose(f);
        }
    }
}
