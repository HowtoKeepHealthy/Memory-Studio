#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include <cstdio>
static volatile uintptr_t root_pointer = 0;
int main() {
    auto* first = static_cast<uint8_t*>(VirtualAlloc(nullptr,4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE));
    auto* second = static_cast<uint8_t*>(VirtualAlloc(nullptr,4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE));
    if (!first || !second) return 1;
    root_pointer = reinterpret_cast<uintptr_t>(first);
    *reinterpret_cast<uintptr_t*>(first+32) = reinterpret_cast<uintptr_t>(second);
    *reinterpret_cast<int32_t*>(second+64) = 73421;
    std::printf("POINTER %lu %016llX %016llX %016llX\n",GetCurrentProcessId(),
        static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(&root_pointer)),
        static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(first)),
        static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(second+64)));
    std::fflush(stdout); Sleep(60000); VirtualFree(first,0,MEM_RELEASE);VirtualFree(second,0,MEM_RELEASE); return 0;
}
