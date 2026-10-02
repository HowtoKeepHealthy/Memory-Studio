#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <conio.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <new>

// One private, writable page makes the demo easy to inspect without the values
// moving between stack frames. Only Tick changes until the user presses a key.
struct DemoValues {
    std::int32_t health = 100;
    std::int32_t gold = 2500;
    float speed = 1.25f;
    std::uint32_t tick = 0;
    char message[96] = "MemoryStudio demo - find me";
    char16_t greeting[32] = u"MemoryStudio \u4f60\u597d";
    std::uint8_t signature[8] = {0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44};
};

static void show_values(const DemoValues& v) {
    std::printf("Health = %-4d  Gold = %-7d  Speed = %.2f  Tick = %u\n",
                v.health, v.gold, v.speed, v.tick);
}

int main(int argc, char** argv) {
    if (argc == 2 && std::strcmp(argv[1], "--self-test") == 0) {
        DemoValues sample;
        return sample.health == 100 && sample.gold == 2500 && sample.speed == 1.25f ? 0 : 1;
    }
    SetConsoleOutputCP(CP_UTF8);
    SetConsoleTitleW(L"MemoryStudio - Demo Target");
    void* memory = VirtualAlloc(nullptr, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!memory) {
        std::fprintf(stderr, "VirtualAlloc failed: %lu\n", GetLastError());
        return 1;
    }
    auto* values = new (memory) DemoValues;
    std::printf("MemoryStudio Demo Target\nPID: %lu (0x%lX)\n\n",
                GetCurrentProcessId(), GetCurrentProcessId());
    std::printf("Search Int32 100 for Health; press h, then scan Decreased.\n");
    std::printf("Search Int32 2500 for Gold; press g, then scan Increased.\n");
    std::printf("Search Float32 1.25 for Speed; UTF-8: MemoryStudio demo - find me\n");
    std::printf("Bytes: DE AD BE EF 11 22 33 44\n\n");
    std::printf("Health @ %p\nGold   @ %p\nSpeed  @ %p\nTick   @ %p\n",
                static_cast<void*>(&values->health), static_cast<void*>(&values->gold),
                static_cast<void*>(&values->speed), static_cast<void*>(&values->tick));
    std::printf("UTF-8  @ %p\nUTF-16 @ %p\nBytes  @ %p\n\n",
                static_cast<void*>(values->message), static_cast<void*>(values->greeting),
                static_cast<void*>(values->signature));
    std::printf("Keys: h = health - 10 | g = gold + 100 | s = speed + 0.25\n");
    std::printf("      r = reset | p = print values | q = exit\n");
    std::printf("If a value is frozen in MemoryStudio, it will return to its frozen value.\n\n");
    show_values(*values);

    bool running = true;
    ULONGLONG last_tick = GetTickCount64();
    while (running) {
        if (GetTickCount64() - last_tick >= 1000) {
            ++values->tick;
            last_tick = GetTickCount64();
        }
        if (_kbhit()) {
            const int key = _getch();
            switch (key) {
            case 'h': case 'H': values->health -= 10; break;
            case 'g': case 'G': values->gold += 100; break;
            case 's': case 'S': values->speed += 0.25f; break;
            case 'r': case 'R':
                values->health = 100;
                values->gold = 2500;
                values->speed = 1.25f;
                break;
            case 'q': case 'Q': running = false; break;
            case 'p': case 'P': break;
            default: continue;
            }
            if (running) show_values(*values);
        }
        Sleep(30);
    }
    values->~DemoValues();
    VirtualFree(memory, 0, MEM_RELEASE);
    return 0;
}
