#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "memory_core.h"
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <limits>
#include <new>
#include <vector>

static_assert(sizeof(void*) == 8, "MemoryStudio native core requires a 64-bit build.");
static_assert(sizeof(ms_scan_request) == 56 && sizeof(ms_progress) == 40, "Unexpected public ABI packing.");

namespace {
constexpr uint64_t ResultLimit = 2'000'000;
constexpr size_t BlockSize = 1024 * 1024;
constexpr uint32_t PatternLimit = 1024 * 1024;
thread_local char thread_error[512]{};

struct Session {
    HANDLE process = nullptr;
    HANDLE write_process = nullptr;
    DWORD pid = 0;
    uint64_t minimum = 0;
    uint64_t maximum = 0; // Exclusive.
    uint64_t page_size = 4096;
    std::atomic<uint32_t> state{0}; // Bit 0: running; bit 1: cancellation requested.
    std::atomic<uint64_t> scanned{0}, total{0}, progress_results{0}, start_tick{0}, elapsed{0};
    std::vector<uint64_t> addresses;
    std::vector<uint8_t> snapshot;
    uint32_t previous_type = 0;
    uint32_t previous_width = 0;
    bool has_scan = false;
    char error[512]{};
    ~Session() {
        if (write_process) CloseHandle(write_process);
        if (process) CloseHandle(process);
    }
};

struct Region { uint64_t start, end; };
struct ScanSpec {
    uint32_t type, mode, width, alignment;
    uint64_t start, end, limit;
    bool writable_only;
    std::vector<uint8_t> value;
};

char* error_buffer(Session* s) noexcept { return s ? s->error : thread_error; }
int32_t fail(Session* s, int32_t code, const char* message) noexcept {
    std::snprintf(error_buffer(s), 512, "%s", message);
    return code;
}
int32_t os_fail(Session* s, const char* operation, DWORD code = GetLastError()) noexcept {
    WCHAR wide[256]{};
    char detail[320]{};
    FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr,
                   code, 0, wide, static_cast<DWORD>(std::size(wide)), nullptr);
    WideCharToMultiByte(CP_UTF8, 0, wide, -1, detail, sizeof(detail), nullptr, nullptr);
    for (char* p = detail; *p; ++p) if (*p == '\r' || *p == '\n') *p = ' ';
    std::snprintf(error_buffer(s), 512, "%s failed (Windows error %lu): %s",
                  operation, static_cast<unsigned long>(code), detail);
    return code == ERROR_ACCESS_DENIED || code == ERROR_PRIVILEGE_NOT_HELD ? MS_ACCESS : MS_OS;
}
void clear_error(Session* s) noexcept { error_buffer(s)[0] = '\0'; }
bool cancelled(const Session* s) noexcept { return (s->state.load(std::memory_order_relaxed) & 2) != 0; }
bool alive(Session* s) noexcept { return WaitForSingleObject(s->process, 0) == WAIT_TIMEOUT; }
int32_t ensure_alive(Session* s) noexcept {
    return alive(s) ? MS_OK : fail(s, MS_OS, "The target process has exited or is unavailable.");
}
bool readable(DWORD protect) noexcept {
    if (protect & (PAGE_GUARD | PAGE_NOACCESS)) return false;
    switch (protect & 0xff) {
    case PAGE_READONLY: case PAGE_READWRITE: case PAGE_WRITECOPY:
    case PAGE_EXECUTE_READ: case PAGE_EXECUTE_READWRITE: case PAGE_EXECUTE_WRITECOPY: return true;
    default: return false;
    }
}
bool writable(DWORD protect) noexcept {
    switch (protect & 0xff) {
    case PAGE_READWRITE: case PAGE_WRITECOPY: case PAGE_EXECUTE_READWRITE: case PAGE_EXECUTE_WRITECOPY: return true;
    default: return false;
    }
}
uint32_t scalar_width(uint32_t type) noexcept {
    switch (type) {
    case MS_U8: return 1;
    case MS_I16: return 2;
    case MS_I32: case MS_F32: return 4;
    case MS_I64: case MS_F64: return 8;
    default: return 0;
    }
}
template<class T> T load_value(const uint8_t* data) noexcept {
    T value;
    std::memcpy(&value, data, sizeof(value));
    return value;
}
template<class T> bool compare_scalar(const uint8_t* now, const uint8_t* previous, uint32_t mode) noexcept {
    const T a = load_value<T>(now), b = load_value<T>(previous);
    if (mode == MS_EXACT) return a == b;
    if (mode == MS_INCREASED) return a > b;
    return a < b;
}
bool matches(const ScanSpec& spec, const uint8_t* now, const uint8_t* previous) noexcept {
    if (spec.mode == MS_UNKNOWN) return true;
    if (spec.mode == MS_CHANGED) return std::memcmp(now, previous, spec.width) != 0;
    if (spec.mode == MS_UNCHANGED) return std::memcmp(now, previous, spec.width) == 0;
    const uint8_t* comparison = spec.mode == MS_EXACT ? spec.value.data() : previous;
    switch (spec.type) {
    case MS_U8: return compare_scalar<uint8_t>(now, comparison, spec.mode);
    case MS_I16: return compare_scalar<int16_t>(now, comparison, spec.mode);
    case MS_I32: return compare_scalar<int32_t>(now, comparison, spec.mode);
    case MS_I64: return compare_scalar<int64_t>(now, comparison, spec.mode);
    case MS_F32: return compare_scalar<float>(now, comparison, spec.mode);
    case MS_F64: return compare_scalar<double>(now, comparison, spec.mode);
    default: return std::memcmp(now, comparison, spec.width) == 0;
    }
}

int32_t validate(Session* s, const ms_scan_request& request, bool next, ScanSpec& spec) {
    if (request.type > MS_BYTES || request.mode > MS_DECREASED || request.reserved != 0)
        return fail(s, MS_INVALID, "Invalid scan type, mode, or reserved field.");
    if (next && !s->has_scan) return fail(s, MS_INVALID, "A successful initial scan is required before filtering.");
    if (!next && request.mode != MS_EXACT && request.mode != MS_UNKNOWN)
        return fail(s, MS_INVALID, "An initial scan supports exact value or unknown initial value.");
    if (next && request.mode == MS_UNKNOWN)
        return fail(s, MS_INVALID, "Unknown initial value cannot be used for a subsequent scan.");
    spec.type = request.type;
    spec.mode = request.mode;
    spec.width = scalar_width(request.type);
    if (!spec.width) {
        if (request.mode == MS_UNKNOWN || request.mode == MS_INCREASED || request.mode == MS_DECREASED)
            return fail(s, MS_INVALID, "This scan mode is available only for scalar numeric values.");
        spec.width = request.mode == MS_EXACT ? request.value_size : s->previous_width;
        if (spec.width == 0 || spec.width > PatternLimit || (request.type == MS_UTF16 && (spec.width % 2 != 0)))
            return fail(s, MS_INVALID, "Text/byte patterns must be 1 to 1,048,576 bytes; UTF-16 must have even length.");
    }
    if (next && (request.type != s->previous_type || spec.width != s->previous_width))
        return fail(s, MS_INVALID, "Subsequent scans must retain the previous value type and byte width.");
    if (request.mode == MS_EXACT) {
        if (!request.value || request.value_size != spec.width)
            return fail(s, MS_INVALID, "The exact value buffer must match the selected value width.");
        spec.value.assign(request.value, request.value + spec.width);
    }
    spec.alignment = request.alignment;
    const uint32_t natural_alignment = scalar_width(request.type) ? spec.width : (request.type == MS_UTF16 ? 2 : 1);
    if (spec.alignment != 1 && spec.alignment != natural_alignment)
        return fail(s, MS_INVALID, "Alignment must be 1 or the natural scalar alignment.");
    spec.start = request.start_address ? request.start_address : s->minimum;
    spec.end = request.end_address ? std::min(request.end_address, s->maximum) : s->maximum;
    if (spec.start >= spec.end || spec.start >= s->maximum)
        return fail(s, MS_INVALID, "The scan address range is empty or outside the target address space.");
    spec.limit = request.max_results ? request.max_results : ResultLimit;
    if (spec.limit > ResultLimit) return fail(s, MS_INVALID, "The maximum supported result count is 2,000,000.");
    spec.writable_only = request.writable_only != 0;
    return MS_OK;
}

int32_t regions_for(Session* s, const ScanSpec& spec, std::vector<Region>& regions) {
    uint64_t cursor = spec.start;
    while (cursor < spec.end) {
        if (cancelled(s)) return fail(s, MS_CANCELLED, "Scan cancelled; previous results have been preserved.");
        MEMORY_BASIC_INFORMATION info{};
        if (!VirtualQueryEx(s->process, reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(cursor)), &info, sizeof(info))) {
            const DWORD query_error = GetLastError();
            if (ensure_alive(s) != MS_OK) return MS_OS;
            return os_fail(s, "VirtualQueryEx", query_error);
        }
        const uint64_t base = reinterpret_cast<uintptr_t>(info.BaseAddress);
        if (info.RegionSize == 0 || base > std::numeric_limits<uint64_t>::max() - info.RegionSize)
            return fail(s, MS_OS, "The target returned an invalid memory region.");
        const uint64_t region_end = std::min(base + info.RegionSize, spec.end);
        if (region_end <= cursor) return fail(s, MS_OS, "Memory region enumeration did not advance.");
        if (info.State == MEM_COMMIT && readable(info.Protect) && (!spec.writable_only || writable(info.Protect))) {
            const uint64_t begin = std::max(cursor, base);
            if (!regions.empty() && regions.back().end == begin) regions.back().end = region_end;
            else regions.push_back({begin, region_end});
        }
        cursor = region_end;
    }
    return ensure_alive(s);
}

struct RunningGuard {
    Session* s;
    ~RunningGuard() noexcept {
        s->elapsed.store(GetTickCount64() - s->start_tick.load());
        s->state.fetch_and(2, std::memory_order_release);
    }
};

struct PendingResults {
    std::vector<uint64_t> addresses;
    std::vector<uint8_t> snapshot;
    int32_t add(Session* s, const ScanSpec& spec, uint64_t address, const uint8_t* value) {
        if (addresses.size() >= spec.limit)
            return fail(s, MS_LIMIT, "Result limit exceeded; narrow the range or use a more specific value. Previous results have been preserved.");
        addresses.push_back(address);
        snapshot.insert(snapshot.end(), value, value + spec.width);
        return MS_OK;
    }
};

int32_t initial_scan(Session* s, const ScanSpec& spec, const std::vector<Region>& regions, PendingResults& pending) {
    uint64_t total = 0;
    for (const auto& r : regions) total += r.end - r.start;
    s->total.store(total);
    std::vector<uint8_t> read_buffer(BlockSize);
    std::vector<uint8_t> combined;
    combined.reserve(BlockSize + spec.width);
    std::vector<uint8_t> tail;
    uint64_t previous_end = 0;
    uint64_t iterations = 0;
    auto consume = [&](uint64_t address, const uint8_t* data, size_t size) -> int32_t {
        combined.clear();
        if (previous_end != address) tail.clear();
        const uint64_t begin = address - tail.size();
        combined.insert(combined.end(), tail.begin(), tail.end());
        combined.insert(combined.end(), data, data + size);
        if (combined.size() >= spec.width) {
            uint64_t candidate = begin;
            const uint64_t remainder = candidate % spec.alignment;
            if (remainder) candidate += spec.alignment - remainder;
            const uint64_t last = begin + combined.size() - spec.width;
            for (; candidate <= last; candidate += spec.alignment) {
                if ((++iterations & 1023) == 0) {
                    s->progress_results.store(pending.addresses.size());
                    if (cancelled(s)) return fail(s, MS_CANCELLED, "Scan cancelled; previous results have been preserved.");
                }
                const uint8_t* current = combined.data() + static_cast<size_t>(candidate - begin);
                if (matches(spec, current, nullptr)) {
                    const auto result = pending.add(s, spec, candidate, current);
                    if (result != MS_OK) return result;
                }
            }
        }
        const size_t carry = std::min<size_t>(spec.width - 1, combined.size());
        tail.assign(combined.end() - static_cast<ptrdiff_t>(carry), combined.end());
        previous_end = address + size;
        s->progress_results.store(pending.addresses.size());
        return MS_OK;
    };
    for (const auto& region : regions) {
        tail.clear();
        previous_end = 0;
        for (uint64_t address = region.start; address < region.end;) {
            if (cancelled(s)) return fail(s, MS_CANCELLED, "Scan cancelled; previous results have been preserved.");
            if (ensure_alive(s) != MS_OK) return MS_OS;
            const size_t size = static_cast<size_t>(std::min<uint64_t>(BlockSize, region.end - address));
            SIZE_T received = 0;
            if (ReadProcessMemory(s->process, reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(address)), read_buffer.data(), size, &received) && received == size) {
                const auto result = consume(address, read_buffer.data(), size);
                if (result != MS_OK) return result;
                s->scanned.fetch_add(size);
            } else {
                // A protection change or inaccessible page must not discard the readable remainder of a large block.
                const uint64_t block_end = address + size;
                for (uint64_t page = address; page < block_end;) {
                    if (cancelled(s)) return fail(s, MS_CANCELLED, "Scan cancelled; previous results have been preserved.");
                    const uint64_t page_end = std::min(block_end, ((page / s->page_size) + 1) * s->page_size);
                    const size_t page_bytes = static_cast<size_t>(page_end - page);
                    received = 0;
                    const BOOL read_ok = ReadProcessMemory(s->process, reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(page)), read_buffer.data(), page_bytes, &received);
                    if (received > 0 && received <= page_bytes) {
                        const auto result = consume(page, read_buffer.data(), static_cast<size_t>(received));
                        if (result != MS_OK) return result;
                    }
                    if (!read_ok || received != page_bytes) { tail.clear(); previous_end = 0; }
                    s->scanned.fetch_add(page_bytes);
                    page = page_end;
                }
            }
            address += size;
        }
    }
    return ensure_alive(s);
}

int32_t subsequent_scan(Session* s, const ScanSpec& spec, const std::vector<Region>& regions, PendingResults& pending) {
    s->total.store(static_cast<uint64_t>(s->addresses.size()) * spec.width);
    std::vector<uint8_t> cache(BlockSize);
    std::vector<uint8_t> single(spec.width);
    uint64_t cache_start = 0, cache_end = 0, cache_covered_end = 0;
    size_t region_index = 0;
    for (size_t index = 0; index < s->addresses.size(); ++index) {
        if ((index & 1023) == 0) {
            s->progress_results.store(pending.addresses.size());
            if (cancelled(s)) return fail(s, MS_CANCELLED, "Scan cancelled; previous results have been preserved.");
            if (ensure_alive(s) != MS_OK) return MS_OS;
        }
        const uint64_t address = s->addresses[index];
        s->scanned.fetch_add(spec.width);
        while (region_index < regions.size() && regions[region_index].end <= address) ++region_index;
        if (region_index == regions.size()) continue;
        const auto& region = regions[region_index];
        if (address < region.start || region.end - address < spec.width || address % spec.alignment != 0) continue;
        if (address < cache_start || address >= cache_covered_end) {
            cache_start = address;
            const size_t size = static_cast<size_t>(std::min<uint64_t>(BlockSize, region.end - address));
            SIZE_T received = 0;
            const BOOL read_ok = ReadProcessMemory(s->process, reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(address)), cache.data(), size, &received);
            if (read_ok && received == size) {
                cache_end = address + size;
                cache_covered_end = cache_end;
            } else {
                const uint64_t page_end = std::min(region.end, ((address / s->page_size) + 1) * s->page_size);
                received = 0;
                ReadProcessMemory(s->process, reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(address)), cache.data(), static_cast<SIZE_T>(page_end - address), &received);
                cache_end = address + std::min<uint64_t>(received, page_end - address);
                cache_covered_end = page_end;
            }
        }
        const uint8_t* current = nullptr;
        if (address >= cache_start && address <= cache_end && cache_end - address >= spec.width) {
            current = cache.data() + static_cast<size_t>(address - cache_start);
        } else if (address + spec.width > cache_covered_end) {
            // Values crossing a page or cache boundary need a complete contiguous read.
            SIZE_T received = 0;
            if (ReadProcessMemory(s->process, reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(address)), single.data(), spec.width, &received) && received == spec.width)
                current = single.data();
        }
        if (!current) continue;
        const uint8_t* previous = s->snapshot.data() + index * static_cast<size_t>(spec.width);
        if (matches(spec, current, previous)) {
            const auto result = pending.add(s, spec, address, current);
            if (result != MS_OK) return result;
        }
    }
    s->progress_results.store(pending.addresses.size());
    return ensure_alive(s);
}
} // namespace

extern "C" {
MS_API void* ms_open(uint32_t pid) {
    Session* session = nullptr;
    try {
        clear_error(nullptr);
        if (!pid) { fail(nullptr, MS_INVALID, "The process ID must be nonzero."); return nullptr; }
        session = new (std::nothrow) Session;
        if (!session) { fail(nullptr, MS_OS, "Not enough memory to create a process session."); return nullptr; }
        session->pid = pid;
        session->process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | SYNCHRONIZE, FALSE, pid);
        if (!session->process) { os_fail(nullptr, "OpenProcess"); delete session; return nullptr; }
        SYSTEM_INFO info{};
        GetNativeSystemInfo(&info);
        session->minimum = reinterpret_cast<uintptr_t>(info.lpMinimumApplicationAddress);
        session->maximum = reinterpret_cast<uintptr_t>(info.lpMaximumApplicationAddress) + uint64_t{1};
        session->page_size = info.dwPageSize;
        BOOL wow64 = FALSE;
        if (IsWow64Process(session->process, &wow64) && wow64) session->maximum = std::min(session->maximum, uint64_t{0x100000000});
        if (!alive(session)) { fail(nullptr, MS_OS, "The selected process has already exited."); delete session; return nullptr; }
        return session;
    } catch (...) { delete session; fail(nullptr, MS_OS, "Unexpected native error while opening the process."); return nullptr; }
}
MS_API void ms_close(void* opaque) {
    try { delete static_cast<Session*>(opaque); }
    catch (...) { fail(nullptr, MS_OS, "Unexpected native error while closing the process."); }
}
MS_API int32_t ms_scan(void* opaque, const ms_scan_request* request, uint32_t next_scan) {
    auto* s = static_cast<Session*>(opaque);
    try {
        if (!s || !request) return fail(s, MS_INVALID, "A process session and scan request are required.");
        uint32_t state = s->state.load(std::memory_order_acquire);
        do {
            if (state & 1) return fail(s, MS_BUSY, "A scan is already running.");
        } while (!s->state.compare_exchange_weak(state, 1, std::memory_order_acq_rel));
        s->start_tick.store(GetTickCount64());
        s->elapsed.store(0);
        s->scanned.store(0);
        s->total.store(0);
        s->progress_results.store(0);
        RunningGuard guard{s};
        ScanSpec spec{};
        int32_t status = validate(s, *request, next_scan != 0, spec);
        if (status != MS_OK) return status;
        if ((status = ensure_alive(s)) != MS_OK) return status;
        std::vector<Region> regions;
        if ((status = regions_for(s, spec, regions)) != MS_OK) return status;
        PendingResults pending;
        status = next_scan ? subsequent_scan(s, spec, regions, pending) : initial_scan(s, spec, regions, pending);
        if (status != MS_OK) return status;
        if (cancelled(s)) return fail(s, MS_CANCELLED, "Scan cancelled; previous results have been preserved.");
        s->addresses.swap(pending.addresses);
        s->snapshot.swap(pending.snapshot);
        s->previous_type = spec.type;
        s->previous_width = spec.width;
        s->has_scan = true;
        s->progress_results.store(s->addresses.size());
        clear_error(s);
        return MS_OK;
    } catch (const std::bad_alloc&) { return fail(s, MS_OS, "Not enough memory for the scan. Previous results have been preserved."); }
    catch (...) { return fail(s, MS_OS, "Unexpected native scan error. Previous results have been preserved."); }
}
MS_API void ms_cancel(void* opaque) {
    auto* s = static_cast<Session*>(opaque);
    try {
        if (!s) return;
        uint32_t state = s->state.load(std::memory_order_relaxed);
        while ((state & 1) && !s->state.compare_exchange_weak(state, state | 2, std::memory_order_relaxed)) {}
    } catch (...) { /* This concurrently callable entry point must not mutate the error buffer. */ }
}
MS_API void ms_get_progress(void* opaque, ms_progress* progress) {
    try {
        if (!progress) return;
        *progress = {};
        auto* s = static_cast<Session*>(opaque);
        if (!s) return;
        const uint32_t state = s->state.load(std::memory_order_acquire);
        progress->scanned_bytes = s->scanned.load(std::memory_order_relaxed);
        progress->total_bytes = s->total.load(std::memory_order_relaxed);
        progress->result_count = s->progress_results.load(std::memory_order_relaxed);
        progress->running = state & 1;
        progress->cancelled = (state & 2) ? 1 : 0;
        progress->elapsed_ms = static_cast<double>((state & 1) ? GetTickCount64() - s->start_tick.load() : s->elapsed.load());
    } catch (...) { if (progress) *progress = {}; }
}
MS_API uint64_t ms_result_count(void* opaque) {
    auto* s = static_cast<Session*>(opaque);
    try { return s ? s->addresses.size() : 0; }
    catch (...) { fail(s, MS_OS, "Unexpected native result error."); return 0; }
}
MS_API uint32_t ms_get_results(void* opaque, uint64_t offset, uint64_t* addresses, uint32_t capacity) {
    auto* s = static_cast<Session*>(opaque);
    try {
        if (!s || (!addresses && capacity)) { fail(s, MS_INVALID, "A session and result buffer are required."); return 0; }
        if (offset >= s->addresses.size() || !capacity) return 0;
        const auto count = static_cast<uint32_t>(std::min<uint64_t>(capacity, s->addresses.size() - offset));
        std::copy_n(s->addresses.data() + static_cast<size_t>(offset), count, addresses);
        return count;
    } catch (...) { fail(s, MS_OS, "Unexpected native result copy error."); return 0; }
}
MS_API int32_t ms_read(void* opaque, uint64_t address, uint8_t* buffer, uint32_t size) {
    auto* s = static_cast<Session*>(opaque);
    try {
        if (!s || !buffer || !size || address > std::numeric_limits<uint64_t>::max() - size)
            return fail(s, MS_INVALID, "A session, nonempty buffer, and valid address are required.");
        if (ensure_alive(s) != MS_OK) return MS_OS;
        SIZE_T received = 0;
        const BOOL read_ok = ReadProcessMemory(s->process, reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(address)), buffer, size, &received);
        const DWORD read_error = read_ok ? ERROR_SUCCESS : GetLastError();
        if (!read_ok || received != size)
            return os_fail(s, "ReadProcessMemory", read_ok ? ERROR_PARTIAL_COPY : read_error);
        clear_error(s);
        return MS_OK;
    } catch (...) { return fail(s, MS_OS, "Unexpected native read error."); }
}
MS_API int32_t ms_write(void* opaque, uint64_t address, const uint8_t* buffer, uint32_t size) {
    auto* s = static_cast<Session*>(opaque);
    try {
        if (!s || !buffer || !size || address > std::numeric_limits<uint64_t>::max() - size)
            return fail(s, MS_INVALID, "A session, nonempty buffer, and valid address are required.");
        if (ensure_alive(s) != MS_OK) return MS_OS;
        const uint64_t end = address + size;
        for (uint64_t cursor = address; cursor < end;) {
            MEMORY_BASIC_INFORMATION info{};
            if (!VirtualQueryEx(s->process, reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(cursor)), &info, sizeof(info)))
                return os_fail(s, "VirtualQueryEx before writing");
            if (info.State != MEM_COMMIT || !readable(info.Protect) || !writable(info.Protect))
                return fail(s, MS_ACCESS, "The requested write includes memory that is not writable.");
            const uint64_t base = reinterpret_cast<uintptr_t>(info.BaseAddress);
            if (info.RegionSize == 0 || base > std::numeric_limits<uint64_t>::max() - info.RegionSize || base + info.RegionSize <= cursor)
                return fail(s, MS_OS, "The target returned an invalid memory region before writing.");
            cursor = std::min(end, base + info.RegionSize);
        }
        if (!s->write_process) {
            s->write_process = OpenProcess(PROCESS_VM_WRITE | PROCESS_VM_OPERATION, FALSE, s->pid);
            if (!s->write_process) return os_fail(s, "OpenProcess for writing");
        }
        SIZE_T written = 0;
        const BOOL write_ok = WriteProcessMemory(s->write_process, reinterpret_cast<LPVOID>(static_cast<uintptr_t>(address)), buffer, size, &written);
        const DWORD write_error = write_ok ? ERROR_SUCCESS : GetLastError();
        if (!write_ok || written != size)
            return os_fail(s, "WriteProcessMemory", write_ok ? ERROR_PARTIAL_COPY : write_error);
        clear_error(s);
        return MS_OK;
    } catch (...) { return fail(s, MS_OS, "Unexpected native write error."); }
}
MS_API uint32_t ms_error(void* opaque, char* buffer, uint32_t capacity) {
    try {
        const char* message = error_buffer(static_cast<Session*>(opaque));
        const auto length = static_cast<uint32_t>(std::strlen(message));
        if (buffer && capacity) {
            const size_t copy = std::min<size_t>(length, capacity - 1);
            std::memcpy(buffer, message, copy);
            buffer[copy] = '\0';
        }
        return length;
    } catch (...) { if (buffer && capacity) buffer[0] = '\0'; return 0; }
}
} // extern "C"
