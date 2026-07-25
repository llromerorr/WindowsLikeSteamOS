#include <windows.h>
#include <stdio.h>
#include "C:\Program Files (x86)\RivaTuner Statistics Server\SDK\Include\RTSSSharedMemory.h"

int main() {
    printf("Offset of dwFramerateLimit: %zu\n", offsetof(RTSS_SHARED_MEMORY_APP_ENTRY, dwFramerateLimit));
    return 0;
}
