#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "../native/memory_core.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <functional>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

// Standalone public-ABI regression. The child is this executable in --child mode;
// every target range is an allocation made by this test, never a third-party process.
// Build (PowerShell, repository root):
//   New-Item -ItemType Directory artifacts/native-unknown-check -Force | Out-Null
//   .tools/llvm-mingw/bin/clang++.exe -std=c++20 -O2 -Wall -Wextra -Werror -static -municode tests/native_unknown_scan.cpp -o artifacts/native-unknown-check/native_unknown_scan.exe
// Run: artifacts/native-unknown-check/native_unknown_scan.exe <absolute memory_core.dll>
// --probe-limit runs only the >2M unknown request, to record the old failure explicitly.

namespace {
constexpr size_t MiB = 1024 * 1024;
constexpr size_t LargeBytes = 12 * MiB;
constexpr uint64_t RegularLimit = 2000000;
void check(bool condition, const std::string& message) {
    if (!condition) throw std::runtime_error(message);
}

struct Core {
    HMODULE module{};
    decltype(&ms_open) open{};
    decltype(&ms_close) close{};
    decltype(&ms_scan) scan{};
    decltype(&ms_result_count) count{};
    decltype(&ms_get_results) results{};
    decltype(&ms_undo_scan) undo{};
    decltype(&ms_get_scan_history) history{};
    decltype(&ms_get_progress) progress{};
    decltype(&ms_cancel) cancel{};
    decltype(&ms_error) error{};
    template<class T> T symbol(const char* name) {
        auto pointer = GetProcAddress(module, name);
        check(pointer != nullptr, std::string("Missing export: ") + name);
        return reinterpret_cast<T>(pointer);
    }
    explicit Core(const wchar_t* path) : module(LoadLibraryW(path)) {
        check(module != nullptr, "LoadLibraryW failed: " + std::to_string(GetLastError()));
        open = symbol<decltype(open)>("ms_open"); close = symbol<decltype(close)>("ms_close");
        scan = symbol<decltype(scan)>("ms_scan"); count = symbol<decltype(count)>("ms_result_count");
        results = symbol<decltype(results)>("ms_get_results"); undo = symbol<decltype(undo)>("ms_undo_scan");
        history = symbol<decltype(history)>("ms_get_scan_history"); progress = symbol<decltype(progress)>("ms_get_progress");
        cancel = symbol<decltype(cancel)>("ms_cancel"); error = symbol<decltype(error)>("ms_error");
    }
    ~Core() { if (module) FreeLibrary(module); }
};

struct Child {
    PROCESS_INFORMATION info{};
    Child() {
        wchar_t executable[32768]{};
        check(GetModuleFileNameW(nullptr, executable, 32768) != 0, "GetModuleFileNameW failed");
        std::wstring command = L"\"" + std::wstring(executable) + L"\" --child";
        STARTUPINFOW startup{}; startup.cb = sizeof(startup);
        check(CreateProcessW(executable, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
            nullptr, nullptr, &startup, &info) != FALSE, "Create owned child failed");
    }
    ~Child() {
        TerminateProcess(info.hProcess, 0); WaitForSingleObject(info.hProcess, 5000);
        CloseHandle(info.hThread); CloseHandle(info.hProcess);
    }
};

struct Memory {
    HANDLE process;
    uint64_t address{};
    size_t size;
    Memory(HANDLE target, size_t bytes = LargeBytes) : process(target), size(bytes) {
        address = reinterpret_cast<uint64_t>(VirtualAllocEx(process, nullptr, bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
        check(address != 0, "VirtualAllocEx failed");
        // Committed Windows pages are initially zero; no per-candidate fixture array is allocated.
    }
    ~Memory() { VirtualFreeEx(process, reinterpret_cast<void*>(address), 0, MEM_RELEASE); }
    template<class T> void put(size_t offset, T value) {
        SIZE_T written = 0;
        check(offset <= size && sizeof(value) <= size - offset, "Fixture write outside allocation");
        check(WriteProcessMemory(process, reinterpret_cast<void*>(address + offset), &value, sizeof(value), &written) && written == sizeof(value), "WriteProcessMemory seed failed");
    }
    void protect(size_t offset, size_t bytes, DWORD protection) {
        DWORD old = 0;
        check(VirtualProtectEx(process, reinterpret_cast<void*>(address + offset), bytes, protection, &old) != FALSE, "VirtualProtectEx failed");
    }
};

struct Session {
    Core& core;
    void* handle{};
    Session(Core& owner, DWORD pid) : core(owner), handle(core.open(pid)) { check(handle != nullptr, "ms_open failed"); }
    ~Session() { core.close(handle); }
    std::string error() const { char text[2048]{}; core.error(handle, text, sizeof(text)); return text; }
    ms_scan_history_info state() const { ms_scan_history_info value{}; core.history(handle, &value); return value; }
    void ok(const ms_scan_request& request, bool next = false) {
        int status = core.scan(handle, &request, next ? 1u : 0u);
        check(status == MS_OK, "scan status=" + std::to_string(status) + ": " + error());
    }
    void undo() { check(core.undo(handle) == MS_OK, "ms_undo_scan failed: " + error()); }
    void expect(const std::vector<uint64_t>& expected) const {
        check(core.count(handle) == expected.size(), "Count differs: actual=" + std::to_string(core.count(handle)) + " expected=" + std::to_string(expected.size()));
        std::vector<uint64_t> actual(expected.size());
        if (!actual.empty()) check(core.results(handle, 0, actual.data(), static_cast<uint32_t>(actual.size())) == actual.size(), "Result collection incomplete");
        check(actual == expected, "Result addresses differ");
    }
};

ms_scan_request request(const Memory& memory, uint32_t type = MS_I32, uint32_t alignment = 4) {
    ms_scan_request value{};
    value.type = type; value.mode = MS_UNKNOWN; value.start_address = memory.address;
    value.end_address = memory.address + memory.size; value.alignment = alignment; value.writable_only = 1;
    return value;
}

void page(Session& session, uint64_t base, uint64_t count, uint32_t step, uint64_t offset, uint32_t capacity) {
    std::vector<uint64_t> output(capacity, UINT64_MAX);
    uint32_t copied = session.core.results(session.handle, offset, output.data(), capacity);
    uint32_t expected = static_cast<uint32_t>(std::min<uint64_t>(capacity, offset < count ? count - offset : 0));
    check(copied == expected, "ms_get_results copied wrong count at offset " + std::to_string(offset));
    for (uint32_t i = 0; i < copied; ++i)
        check(output[i] == base + (offset + i) * step, "ms_get_results address differs at ordinal " + std::to_string(offset + i));
}

void cancel_scan(Session& session, const ms_scan_request& query, bool next) {
    std::atomic<bool> done{false}; int status = -1;
    std::thread worker([&] { status = session.core.scan(session.handle, &query, next ? 1u : 0u); done.store(true); });
    bool observed = false;
    const ULONGLONG started = GetTickCount64();
    while (!done.load() && GetTickCount64() - started < 10000) {
        ms_progress progress{}; session.core.progress(session.handle, &progress);
        if (progress.running) { observed = true; session.core.cancel(session.handle); break; }
        Sleep(1);
    }
    if (!observed && !done.load()) session.core.cancel(session.handle);
    worker.join();
    check(observed, "Could not observe active scan to cancel");
    check(status == MS_CANCELLED, "Cancelled scan status=" + std::to_string(status) + ": " + session.error());
}

struct InvalidTemporaryDirectory {
    std::wstring old_tmp, old_temp;
    bool had_tmp{}, had_temp{};
    static std::wstring environment(const wchar_t* name, bool& present) {
        DWORD size = GetEnvironmentVariableW(name, nullptr, 0); present = size != 0;
        if (!size) return {};
        std::wstring value(size, L'\0'); GetEnvironmentVariableW(name, value.data(), size); value.resize(size - 1); return value;
    }
    InvalidTemporaryDirectory() {
        old_tmp = environment(L"TMP", had_tmp); old_temp = environment(L"TEMP", had_temp);
        wchar_t directory[32768]{}; check(GetTempPathW(32768, directory) != 0, "GetTempPathW failed");
        std::wstring unavailable = std::wstring(directory) + L"MemoryStudio-Unknown-Missing-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()) + L"\\";
        check(GetFileAttributesW(unavailable.c_str()) == INVALID_FILE_ATTRIBUTES, "Temp fault fixture path unexpectedly exists");
        check(SetEnvironmentVariableW(L"TMP", unavailable.c_str()) && SetEnvironmentVariableW(L"TEMP", unavailable.c_str()), "Set temporary fault path failed");
    }
    ~InvalidTemporaryDirectory() {
        SetEnvironmentVariableW(L"TMP", had_tmp ? old_tmp.c_str() : nullptr);
        SetEnvironmentVariableW(L"TEMP", had_temp ? old_temp.c_str() : nullptr);
    }
};
} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc > 1 && std::wcscmp(argv[1], L"--child") == 0) { Sleep(INFINITE); return 0; }
    bool probe = argc > 2 && std::wcscmp(argv[2], L"--probe-limit") == 0;
    int passed = 0, failed = 0;
    try {
        Core core(argc > 1 ? argv[1] : L"artifacts\\native\\memory_core.dll"); Child child;
        auto run = [&](const char* name, const std::function<void()>& body) {
            try { body(); ++passed; std::printf("PASS %s\n", name); }
            catch (const std::exception& error) { ++failed; std::printf("FAIL %s: %s\n", name, error.what()); }
            std::fflush(stdout);
        };
        run("Unknown initial has no value input and exceeds default/explicit 2M cap", [&] {
            Memory memory(child.info.hProcess); Session session(core, child.info.dwProcessId);
            auto query = request(memory); constexpr uint64_t count = LargeBytes / 4;
            std::printf("FIXTURE pid=%lu bytes=%zu Int32 candidates=%llu unknown value=null/0\n", child.info.dwProcessId, memory.size, static_cast<unsigned long long>(count));
            session.ok(query); check(core.count(session.handle) == count && count > RegularLimit, "Large unknown count is truncated");
            page(session, memory.address, count, 4, 0, 5);
            page(session, memory.address, count, 4, RegularLimit - 3, 7);
            page(session, memory.address, count, 4, MiB / 4 - 1, 3);
            page(session, memory.address, count, 4, count - 3, 8);
            page(session, memory.address, count, 4, count, 4);
            page(session, memory.address, count, 4, UINT64_MAX, 4);
            query.max_results = RegularLimit; session.ok(query);
            check(core.count(session.handle) == count, "Unknown with explicit ordinary 2M limit was truncated");
            query.mode = MS_UNCHANGED; session.ok(query, true);
            check(core.count(session.handle) == count, "Unchanged compact scan loses >2M candidates");
            session.undo(); check(core.count(session.handle) == count, "Undo unchanged failed to restore large candidates");
            int32_t zero = 0; query.mode = MS_EXACT; query.value = reinterpret_cast<const uint8_t*>(&zero); query.value_size = sizeof(zero);
            session.ok(query, true); check(core.count(session.handle) == count, "Unknown-origin subsequent exact is incorrectly capped");
        });
        if (!probe) {
            run("Changed/increased/decreased/unchanged and undo keep the original large baseline", [&] {
                Memory memory(child.info.hProcess); Session session(core, child.info.dwProcessId); auto query = request(memory);
                constexpr uint64_t count = LargeBytes / 4; session.ok(query);
                const std::array<size_t, 5> offsets{0, 64, MiB - 4, MiB, LargeBytes - 4};
                const std::array<int32_t, 5> values{10, -10, 20, -20, 30};
                std::vector<uint64_t> changed, increased, decreased;
                for (size_t i = 0; i < offsets.size(); ++i) {
                    memory.put(offsets[i], values[i]); changed.push_back(memory.address + offsets[i]);
                    (values[i] > 0 ? increased : decreased).push_back(memory.address + offsets[i]);
                }
                query.mode = MS_CHANGED; session.ok(query, true); session.expect(changed); session.undo();
                check(core.count(session.handle) == count, "Undo changed did not restore large candidate set");
                query.mode = MS_INCREASED; session.ok(query, true); session.expect(increased); session.undo();
                query.mode = MS_DECREASED; session.ok(query, true); session.expect(decreased); session.undo();
                query.mode = MS_UNCHANGED; session.ok(query, true);
                check(core.count(session.handle) == count - offsets.size() && core.count(session.handle) > RegularLimit, "Unchanged filter is capped or uses a mutated baseline");
                session.undo(); query.mode = MS_CHANGED; session.ok(query, true); session.expect(changed);
                memory.put<int32_t>(offsets[0], 11); memory.put<int32_t>(offsets[1], -9);
                memory.put<int32_t>(offsets[2], 19); memory.put<int32_t>(offsets[3], -21);
                query.mode = MS_INCREASED; session.ok(query, true); session.expect({changed[0], changed[1]}); session.undo();
                query.mode = MS_DECREASED; session.ok(query, true); session.expect({changed[2], changed[3]}); session.undo();
                query.mode = MS_UNCHANGED; session.ok(query, true); session.expect({changed[4]});
            });
            run("Ordinary exact keeps its 2M cap and failure preserves compact candidates/history", [&] {
                Memory memory(child.info.hProcess); Session session(core, child.info.dwProcessId); auto query = request(memory);
                session.ok(query); auto before = session.state(); int32_t zero = 0;
                query.mode = MS_EXACT; query.value = reinterpret_cast<const uint8_t*>(&zero); query.value_size = sizeof(zero);
                check(core.scan(session.handle, &query, 0) == MS_LIMIT, "Ordinary exact no longer enforces the default 2M cap");
                auto after = session.state(); check(before.generation == after.generation && before.undo_count == after.undo_count, "MS_LIMIT changed history");
                check(core.count(session.handle) == LargeBytes / 4, "MS_LIMIT destroyed compact results");
                page(session, memory.address, LargeBytes / 4, 4, LargeBytes / 4 - 2, 4);
                memory.put<int32_t>(64, 123); query.mode = MS_CHANGED; query.value = nullptr; query.value_size = 0;
                session.ok(query, true); session.expect({memory.address + 64});
            });
            run("Unaligned Int64 crosses blocks but never unreadable page gaps", [&] {
                constexpr size_t hole = MiB, page_size = 4096, bytes = 2 * MiB + 3 * page_size;
                Memory memory(child.info.hProcess, bytes); memory.protect(hole, page_size, PAGE_NOACCESS);
                Session session(core, child.info.dwProcessId); auto query = request(memory, MS_I64, 1); session.ok(query);
                const uint64_t left_count = hole - 7, right_count = bytes - hole - page_size - 7;
                check(core.count(session.handle) == left_count + right_count && left_count + right_count > RegularLimit, "Gap/unaligned unknown count differs");
                std::array<uint64_t, 4> border{};
                check(core.results(session.handle, left_count - 2, border.data(), 4) == 4, "Gap pagination incomplete");
                check(border == std::array<uint64_t, 4>{memory.address + hole - 9, memory.address + hole - 8,
                    memory.address + hole + page_size, memory.address + hole + page_size + 1}, "Candidate spans an unreadable gap");
                memory.put<int64_t>(hole - 8, 0x0101010101010101LL);
                memory.put<int64_t>(hole + page_size + 1, 0x0101010101010101LL);
                const size_t crossing = hole + page_size + MiB - 3;
                memory.put<int64_t>(crossing, 0x0101010101010101LL);
                std::vector<uint64_t> changed;
                for (size_t offset = hole - 15; offset <= hole - 8; ++offset) changed.push_back(memory.address + offset);
                for (size_t offset = hole + page_size; offset <= hole + page_size + 8; ++offset) changed.push_back(memory.address + offset);
                for (size_t offset = crossing - 7; offset <= crossing + 7; ++offset) changed.push_back(memory.address + offset);
                query.mode = MS_CHANGED; session.ok(query, true); session.expect(changed); session.undo();
                memory.protect(hole, page_size, PAGE_READWRITE); query.mode = MS_UNCHANGED; session.ok(query, true);
                check(core.count(session.handle) == left_count + right_count - changed.size(), "Rescan added new candidates from the formerly unreadable gap");
            });
            run("Cancelled unknown initial preserves an existing large compact snapshot", [&] {
                Memory memory(child.info.hProcess); Memory wide(child.info.hProcess, 128 * MiB);
                Session session(core, child.info.dwProcessId); auto baseline = request(memory); session.ok(baseline);
                auto before = session.state(); memory.put<int32_t>(64, 777); cancel_scan(session, request(wide), false);
                auto after = session.state(); check(before.generation == after.generation && before.undo_count == after.undo_count, "Cancelled initial changed history");
                check(core.count(session.handle) == LargeBytes / 4, "Cancelled initial discarded large candidates");
                page(session, memory.address, LargeBytes / 4, 4, RegularLimit + 5, 4);
                baseline.mode = MS_CHANGED; session.ok(baseline, true); session.expect({memory.address + 64});
            });
            run("Cancelled compact rescan preserves candidates and previous comparison values", [&] {
                Memory memory(child.info.hProcess); Session session(core, child.info.dwProcessId); auto query = request(memory, MS_U8, 1);
                session.ok(query); auto before = session.state(); memory.put<uint8_t>(1, 7);
                query.mode = MS_UNCHANGED; cancel_scan(session, query, true);
                auto after = session.state(); check(before.generation == after.generation && before.undo_count == after.undo_count, "Cancelled rescan changed history");
                check(core.count(session.handle) == LargeBytes, "Cancelled rescan discarded large byte candidates");
                query.mode = MS_CHANGED; session.ok(query, true); session.expect({memory.address + 1});
            });
            run("Unknown writable-only skips readonly pages but readable mode includes them", [&] {
                Memory memory(child.info.hProcess); memory.protect(MiB, 4096, PAGE_READONLY);
                Session session(core, child.info.dwProcessId); auto query = request(memory); session.ok(query);
                check(core.count(session.handle) == (LargeBytes - 4096) / 4, "Readonly page appeared in writable-only unknown candidates");
                query.writable_only = 0; session.ok(query);
                check(core.count(session.handle) == LargeBytes / 4, "Readable unknown scan excluded readonly candidates");
            });
            run("Actual temporary-file creation failure preserves compact candidates and baseline", [&] {
                Memory memory(child.info.hProcess); Session session(core, child.info.dwProcessId); auto query = request(memory); session.ok(query);
                auto before = session.state(); memory.put<int32_t>(64, 88);
                int status;
                { InvalidTemporaryDirectory invalid; query.mode = MS_UNCHANGED; status = core.scan(session.handle, &query, 1); }
                check(status != MS_OK && status != MS_LIMIT && status != MS_CANCELLED, "Invalid temp directory did not produce an actual storage failure");
                auto after = session.state(); check(before.generation == after.generation && before.undo_count == after.undo_count, "Storage failure changed history");
                check(core.count(session.handle) == LargeBytes / 4, "Storage failure discarded previous compact candidates");
                query.mode = MS_CHANGED; session.ok(query, true); session.expect({memory.address + 64});
            });
        }
    } catch (const std::exception& error) { ++failed; std::printf("FAIL startup: %s\n", error.what()); }
    std::printf("SUMMARY passed=%d failed=%d\n", passed, failed);
    return failed ? 1 : 0;
}
