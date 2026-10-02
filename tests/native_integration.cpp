#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "../native/memory_core.h"
#include <algorithm>
#include <array>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <functional>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

// Load the public C ABI exactly as the managed application does. Tests scan
// bounded pages in this process; no third-party process or admin rights needed.
struct Core {
    HMODULE module = nullptr;
    decltype(&ms_open) open;
    decltype(&ms_close) close;
    decltype(&ms_scan) scan;
    decltype(&ms_cancel) cancel;
    decltype(&ms_get_progress) progress;
    decltype(&ms_result_count) count;
    decltype(&ms_get_results) results;
    decltype(&ms_read) read;
    decltype(&ms_write) write;
    decltype(&ms_error) error;

    template<class T> T load(const char* name) {
        FARPROC p = GetProcAddress(module, name);
        if (!p) throw std::runtime_error(std::string("Missing export: ") + name);
        return reinterpret_cast<T>(p);
    }
    explicit Core(const wchar_t* path) {
        module = LoadLibraryW(path);
        if (!module) throw std::runtime_error("LoadLibrary failed (Win32 " + std::to_string(GetLastError()) + ")");
        open = load<decltype(open)>("ms_open");
        close = load<decltype(close)>("ms_close");
        scan = load<decltype(scan)>("ms_scan");
        cancel = load<decltype(cancel)>("ms_cancel");
        progress = load<decltype(progress)>("ms_get_progress");
        count = load<decltype(count)>("ms_result_count");
        results = load<decltype(results)>("ms_get_results");
        read = load<decltype(read)>("ms_read");
        write = load<decltype(write)>("ms_write");
        error = load<decltype(error)>("ms_error");
    }
    ~Core() { if (module) FreeLibrary(module); }
    Core(const Core&) = delete;
};

struct Session {
    Core& core;
    void* handle;
    explicit Session(Core& c, DWORD pid = GetCurrentProcessId()) : core(c), handle(c.open(pid)) {
        if (!handle) throw std::runtime_error("ms_open failed");
    }
    ~Session() { core.close(handle); }
    std::string last_error() const {
        char buffer[2048]{};
        core.error(handle, buffer, static_cast<uint32_t>(sizeof(buffer)));
        return buffer;
    }
};

struct Pages {
    uint8_t* data;
    size_t size;
    explicit Pages(size_t bytes = 4096) : size(bytes) {
        data = static_cast<uint8_t*>(VirtualAlloc(nullptr, bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
        if (!data) throw std::runtime_error("VirtualAlloc failed");
        std::memset(data, 0xCC, bytes);
    }
    ~Pages() { VirtualFree(data, 0, MEM_RELEASE); }
    uint64_t address(size_t offset = 0) const { return reinterpret_cast<uint64_t>(data + offset); }
    template<class T> void put(size_t offset, T value) { std::memcpy(data + offset, &value, sizeof(value)); }
};

static void require(bool condition, const std::string& message) {
    if (!condition) throw std::runtime_error(message);
}

static ms_scan_request request(const Pages& pages, ms_type type, ms_mode mode,
                               const void* value = nullptr, uint32_t width = 0,
                               uint32_t alignment = 1) {
    ms_scan_request r{};
    r.type = type;
    r.mode = mode;
    r.start_address = pages.address();
    r.end_address = pages.address(pages.size);
    r.value = static_cast<const uint8_t*>(value);
    r.value_size = width;
    r.alignment = alignment;
    r.writable_only = 1;
    return r;
}

static void ok_scan(Session& s, const ms_scan_request& r, bool next = false) {
    const int status = s.core.scan(s.handle, &r, next ? 1 : 0);
    require(status == MS_OK, "scan status " + std::to_string(status) + ": " + s.last_error());
}

static std::vector<uint64_t> addresses(Session& s) {
    const auto count = s.core.count(s.handle);
    require(count < 1000000, "unexpected result count while collecting test results");
    std::vector<uint64_t> result(static_cast<size_t>(count));
    if (count) {
        const auto copied = s.core.results(s.handle, 0, result.data(), static_cast<uint32_t>(count));
        require(copied == count, "ms_get_results did not return all results");
    }
    return result;
}

static void expect(Session& s, std::vector<uint64_t> wanted) {
    auto actual = addresses(s);
    std::sort(actual.begin(), actual.end());
    std::sort(wanted.begin(), wanted.end());
    require(actual == wanted, "addresses differ: got " + std::to_string(actual.size()) +
            ", expected " + std::to_string(wanted.size()));
}

static void seed_ints(Pages& p) {
    std::memset(p.data, 0xCC, p.size);
    p.put<int32_t>(64, 100);
    p.put<int32_t>(128, 100);
    p.put<int32_t>(256, 100);
}

int wmain(int argc, wchar_t** argv) {
    // A short-lived owned child lets the parent check stale process handles.
    if (argc > 1 && std::wcscmp(argv[1], L"--child") == 0) {
        Sleep(30000);
        return 0;
    }
    const wchar_t* dll = argc > 1 ? argv[1] : L"artifacts\\native\\memory_core.dll";
    int passed = 0;
    int failed = 0;
    try {
        Core core(dll);
        auto run = [&](const char* name, const std::function<void()>& action) {
            try {
                action();
                ++passed;
                std::printf("PASS  %s\n", name);
            } catch (const std::exception& e) {
                ++failed;
                std::printf("FAIL  %s: %s\n", name, e.what());
            }
        };

        run("Int32 exact range and paged results", [&] {
            Pages p;
            Session s(core);
            seed_ints(p);
            int32_t value = 100;
            auto r = request(p, MS_I32, MS_EXACT, &value, sizeof(value), 4);
            ok_scan(s, r);
            expect(s, {p.address(64), p.address(128), p.address(256)});
            uint64_t page[2]{};
            require(core.results(s.handle, 1, page, 2) == 2, "middle page length");
            require(page[0] == p.address(128) && page[1] == p.address(256), "page order/offset");
            require(core.results(s.handle, 3, page, 2) == 0, "end offset should be empty");
            require(core.results(s.handle, UINT64_MAX, page, 2) == 0, "large offset should be empty");
            require(core.results(s.handle, 0, page, 0) == 0, "zero capacity should be empty");
            r.start_address = p.address(128);
            r.end_address = p.address(256); // Exclusive: the third result must disappear.
            ok_scan(s, r);
            expect(s, {p.address(128)});
            ms_progress progress{};
            core.progress(s.handle, &progress);
            require(progress.running == 0 && progress.result_count == 1, "final progress state");
        });

        for (auto mode : {MS_CHANGED, MS_INCREASED, MS_DECREASED, MS_UNCHANGED}) {
            const char* names[] = {"Exact", "Unknown", "Changed", "Unchanged", "Increased", "Decreased"};
            const auto name = std::string("Int32 next ") + names[mode];
            run(name.c_str(), [&, mode] {
                Pages p;
                Session s(core);
                seed_ints(p);
                int32_t value = 100;
                auto r = request(p, MS_I32, MS_EXACT, &value, sizeof(value), 4);
                ok_scan(s, r);
                p.put<int32_t>(64, 110);
                p.put<int32_t>(128, 90);
                r.mode = mode;
                r.value = nullptr;
                r.value_size = 0;
                ok_scan(s, r, true);
                if (mode == MS_CHANGED) expect(s, {p.address(64), p.address(128)});
                if (mode == MS_INCREASED) expect(s, {p.address(64)});
                if (mode == MS_DECREASED) expect(s, {p.address(128)});
                if (mode == MS_UNCHANGED) expect(s, {p.address(256)});
            });
        }

        run("Unknown initial and successive snapshots", [&] {
            Pages p;
            Session s(core);
            p.put<int32_t>(64, 200);
            auto r = request(p, MS_I32, MS_UNKNOWN, nullptr, 0, 4);
            ok_scan(s, r);
            require(core.count(s.handle) == p.size / 4, "unknown must retain every aligned Int32");
            p.put<int32_t>(64, 210);
            r.mode = MS_CHANGED;
            ok_scan(s, r, true);
            expect(s, {p.address(64)});
            r.mode = MS_UNCHANGED;
            ok_scan(s, r, true);
            expect(s, {p.address(64)}); // Compared to 210, not the original 200.
            int32_t value = 210;
            r.mode = MS_EXACT;
            r.value = reinterpret_cast<const uint8_t*>(&value);
            r.value_size = sizeof(value);
            ok_scan(s, r, true);
            expect(s, {p.address(64)});
        });

        run("Float32 and Float64 exact", [&] {
            Pages p;
            Session s(core);
            float f = 1.25f;
            double d = -17.125;
            p.put<float>(64, f);
            p.put<double>(128, d);
            ok_scan(s, request(p, MS_F32, MS_EXACT, &f, sizeof(f), 4));
            expect(s, {p.address(64)});
            ok_scan(s, request(p, MS_F64, MS_EXACT, &d, sizeof(d), 8));
            expect(s, {p.address(128)});
        });

        run("Scalar alignment and unaligned scanning", [&] {
            Pages p;
            Session s(core);
            int32_t value = 0x13572468;
            p.put<int32_t>(65, value);
            p.put<int32_t>(128, value);
            ok_scan(s, request(p, MS_I32, MS_EXACT, &value, sizeof(value), 4));
            expect(s, {p.address(128)});
            ok_scan(s, request(p, MS_I32, MS_EXACT, &value, sizeof(value), 1));
            expect(s, {p.address(65), p.address(128)});
        });

        run("UTF-8, UTF-16 and bytes across read block boundaries", [&] {
            Pages p(2 * 1024 * 1024 + 4096);
            Session s(core);
            const uint8_t utf8[] = {'M', 'S', '-', 0xE4, 0xBD, 0xA0, 0xE5, 0xA5, 0xBD, '-', 'X'};
            const char16_t utf16[] = u"MS-\u4f60\u597d-X";
            const uint8_t bytes[] = {0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44};
            for (const auto type : {MS_UTF8, MS_UTF16, MS_BYTES}) {
                std::memset(p.data, 0xCC, p.size);
                const void* needle = type == MS_UTF8 ? static_cast<const void*>(utf8)
                    : type == MS_UTF16 ? static_cast<const void*>(utf16) : static_cast<const void*>(bytes);
                const uint32_t size = type == MS_UTF8 ? sizeof(utf8)
                    : type == MS_UTF16 ? sizeof(utf16) - sizeof(char16_t) : sizeof(bytes);
                std::vector<uint64_t> wanted;
                for (size_t boundary : {64 * 1024, 256 * 1024, 1024 * 1024}) {
                    const size_t offset = boundary - (type == MS_UTF16 ? 4 : 3);
                    std::memcpy(p.data + offset, needle, size);
                    wanted.push_back(p.address(offset));
                }
                ok_scan(s, request(p, type, MS_EXACT, needle, size, type == MS_UTF16 ? 2 : 1));
                expect(s, wanted);
            }
        });

        run("Read and write round trip; invalid address", [&] {
            Pages p;
            Session s(core);
            const int32_t value = 73421;
            int32_t actual = 0;
            require(core.write(s.handle, p.address(64), reinterpret_cast<const uint8_t*>(&value), sizeof(value)) == MS_OK,
                    "valid write failed");
            require(core.read(s.handle, p.address(64), reinterpret_cast<uint8_t*>(&actual), sizeof(actual)) == MS_OK,
                    "valid read failed");
            require(actual == value, "read/write value differs");
            require(core.read(s.handle, 1, reinterpret_cast<uint8_t*>(&actual), sizeof(actual)) != MS_OK,
                    "invalid address read succeeded");
            require(core.write(s.handle, 1, reinterpret_cast<const uint8_t*>(&value), sizeof(value)) != MS_OK,
                    "invalid address write succeeded");
            require(!s.last_error().empty(), "failed access should expose an error");
        });

        run("Read-only and inaccessible page handling", [&] {
            Pages p;
            Session s(core);
            int32_t value = 54321;
            p.put<int32_t>(64, value);
            DWORD old = 0;
            require(VirtualProtect(p.data, p.size, PAGE_READONLY, &old) != 0, "protect read-only failed");
            auto r = request(p, MS_I32, MS_EXACT, &value, sizeof(value), 4);
            ok_scan(s, r);
            expect(s, {});
            r.writable_only = 0;
            ok_scan(s, r);
            expect(s, {p.address(64)});
            require(core.write(s.handle, p.address(64), reinterpret_cast<const uint8_t*>(&value), sizeof(value)) != MS_OK,
                    "read-only write should fail");
            require(VirtualProtect(p.data, p.size, PAGE_NOACCESS, &old) != 0, "protect no-access failed");
            int32_t readback = 0;
            require(core.read(s.handle, p.address(64), reinterpret_cast<uint8_t*>(&readback), sizeof(readback)) != MS_OK,
                    "no-access read should fail");
            ok_scan(s, r);
            expect(s, {});
        });

        run("Result limit and invalid request preserve results and snapshot", [&] {
            Pages p;
            Session s(core);
            seed_ints(p);
            int32_t value = 100;
            auto r = request(p, MS_I32, MS_EXACT, &value, sizeof(value), 4);
            ok_scan(s, r);
            p.put<int32_t>(64, 110);
            auto limited = request(p, MS_I32, MS_UNKNOWN, nullptr, 0, 4);
            limited.max_results = 2;
            require(core.scan(s.handle, &limited, 0) == MS_LIMIT, "result limit should fail with MS_LIMIT");
            expect(s, {p.address(64), p.address(128), p.address(256)});
            auto invalid = r;
            invalid.end_address = invalid.start_address - 1;
            require(core.scan(s.handle, &invalid, 0) == MS_INVALID, "reversed range should be invalid");
            expect(s, {p.address(64), p.address(128), p.address(256)});
            r.mode = MS_CHANGED;
            r.value = nullptr;
            r.value_size = 0;
            ok_scan(s, r, true);
            expect(s, {p.address(64)});
        });

        run("Result cap accepts exactly the configured number", [&] {
            Pages p;
            Session s(core);
            seed_ints(p);
            int32_t value = 100;
            auto r = request(p, MS_I32, MS_EXACT, &value, sizeof(value), 4);
            r.max_results = 3;
            ok_scan(s, r);
            expect(s, {p.address(64), p.address(128), p.address(256)});
        });

        run("Cancellation preserves results and snapshot", [&] {
            Pages p;
            Pages large(128 * 1024 * 1024);
            Session s(core);
            seed_ints(p);
            int32_t value = 100;
            auto baseline = request(p, MS_I32, MS_EXACT, &value, sizeof(value), 4);
            ok_scan(s, baseline);
            p.put<int32_t>(64, 110);
            const uint8_t needle = 0x17;
            auto r = request(large, MS_U8, MS_EXACT, &needle, 1, 1);
            int scan_status = -1;
            std::thread worker([&] { scan_status = core.scan(s.handle, &r, 0); });
            bool observed_running = false;
            for (int attempt = 0; attempt < 1000; ++attempt) {
                ms_progress progress{};
                core.progress(s.handle, &progress);
                if (progress.running) {
                    observed_running = true;
                    core.cancel(s.handle);
                    break;
                }
                Sleep(1);
            }
            worker.join();
            require(observed_running, "could not observe running scan to cancel");
            require(scan_status == MS_CANCELLED, "cancelled scan returned " + std::to_string(scan_status));
            expect(s, {p.address(64), p.address(128), p.address(256)});
            baseline.mode = MS_CHANGED;
            baseline.value = nullptr;
            baseline.value_size = 0;
            ok_scan(s, baseline, true);
            expect(s, {p.address(64)});
        });

        run("Invalid process ID", [&] {
            void* session = core.open(0);
            if (session) core.close(session);
            require(session == nullptr, "PID 0 should not open");
        });

        run("Exited process handle fails access", [&] {
            wchar_t path[32768]{};
            require(GetModuleFileNameW(nullptr, path, static_cast<DWORD>(std::size(path))) != 0, "executable path failed");
            std::wstring command = L"\"" + std::wstring(path) + L"\" --child";
            STARTUPINFOW startup{};
            startup.cb = sizeof(startup);
            PROCESS_INFORMATION child{};
            require(CreateProcessW(path, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
                                   nullptr, nullptr, &startup, &child) != 0, "owned child launch failed");
            void* session = core.open(child.dwProcessId);
            // Always reap this test-owned process, even if attachment failed.
            const BOOL stopped = TerminateProcess(child.hProcess, 0);
            WaitForSingleObject(child.hProcess, 5000);
            CloseHandle(child.hThread);
            CloseHandle(child.hProcess);
            require(stopped != 0, "owned child stop failed");
            require(session != nullptr, "live child attachment failed");
            uint8_t data = 0;
            const int status = core.read(session, 0x10000, &data, 1);
            core.close(session);
            require(status != MS_OK, "exited process read succeeded");
        });
    } catch (const std::exception& e) {
        std::fprintf(stderr, "SETUP FAILED: %s\n", e.what());
        return 2;
    }
    std::printf("\n%d passed, %d failed\n", passed, failed);
    return failed ? 1 : 0;
}
