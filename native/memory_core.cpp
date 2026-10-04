#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "memory_core.h"
#include "trace_registry.h"
#include <algorithm>
#include <atomic>
#include <bit>
#include <cmath>
#include <deque>
#include <cstdio>
#include <cstring>
#include <limits>
#include <new>
#include <memory>
#include <type_traits>
#include <vector>

static_assert(sizeof(void*) == 8, "MemoryStudio native core requires a 64-bit build.");
static_assert(sizeof(ms_scan_request) == 56 && sizeof(ms_progress) == 40, "Unexpected public ABI packing.");
static_assert(sizeof(ms_scan_options) == 56 && sizeof(ms_scan_history_info) == 48, "Unexpected extended ABI packing.");

namespace {
constexpr uint64_t ResultLimit = 2'000'000;
constexpr size_t BlockSize = 1024 * 1024;
constexpr uint32_t PatternLimit = 1024 * 1024;
constexpr uint32_t HistorySteps = 16;
constexpr uint64_t HistoryBudget = 64 * 1024 * 1024;
constexpr uint64_t DenseFileLimit = 16ULL * 1024 * 1024 * 1024;
constexpr uint64_t HistoryDiskBudget = 4ULL * 1024 * 1024 * 1024;
thread_local char thread_error[512]{};

struct DenseBlock {
    uint64_t start = 0, first = 0, prefix = 0, data_offset = 0, mask_offset = 0;
    uint32_t bytes = 0, slots = 0, count = 0, mask_bytes = 0;
};
struct DenseSnapshot {
    HANDLE file = INVALID_HANDLE_VALUE;
    uint64_t file_bytes = 0, count = 0;
    uint32_t width = 0, stride = 0;
    std::vector<DenseBlock> blocks;
    uint64_t metadata_bytes() const noexcept { return sizeof(DenseSnapshot) + blocks.capacity() * sizeof(DenseBlock); }
    ~DenseSnapshot() { if (file != INVALID_HANDLE_VALUE) CloseHandle(file); }
};

struct HistoryEntry {
    std::vector<uint64_t> addresses;
    std::vector<uint8_t> snapshot;
    uint32_t type = 0, width = 0;
    bool has_scan = false;
    bool unknown_origin = false;
    std::shared_ptr<DenseSnapshot> dense;
    uint64_t bytes() const noexcept { return addresses.capacity() * sizeof(uint64_t) + snapshot.capacity() + (dense ? dense->metadata_bytes() : 0); }
    uint64_t disk_bytes() const noexcept { return dense ? dense->file_bytes : 0; }
};
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
    bool unknown_origin = false;
    std::shared_ptr<DenseSnapshot> dense;
    std::deque<HistoryEntry> history;
    uint64_t history_bytes = 0, history_disk_bytes = 0, generation = 0;
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
    bool approximate = false;
    double absolute_tolerance = 0, relative_tolerance = 0;
    std::vector<uint8_t> value, upper, mask;
};
struct CodeRegion { uint64_t address, size; DWORD protection; bool changed = false; };
struct CodeRestore {
    Session* session;
    std::vector<CodeRegion>& regions;
    uint64_t address;
    uint32_t size;
    bool flush = false;
    DWORD restore_error = ERROR_SUCCESS, flush_error = ERROR_SUCCESS;
    void restore() noexcept {
        for (auto it = regions.rbegin(); it != regions.rend(); ++it) if (it->changed) {
            DWORD unused = 0;
            if (VirtualProtectEx(session->write_process, reinterpret_cast<LPVOID>(it->address), it->size, it->protection, &unused)) it->changed = false;
            else if (!restore_error) restore_error = GetLastError();
        }
        if (flush) {
            if (!FlushInstructionCache(session->process, reinterpret_cast<LPCVOID>(address), size)) flush_error = GetLastError();
            flush = false;
        }
    }
    ~CodeRestore() noexcept { restore(); }
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
bool approximately_equal(const ScanSpec& spec, long double a, long double b) noexcept {
    if (!std::isfinite(a) || !std::isfinite(b)) return false;
    const long double bound = std::max(static_cast<long double>(spec.absolute_tolerance),
        static_cast<long double>(spec.relative_tolerance) * std::max(std::fabs(a), std::fabs(b)));
    return std::fabs(a - b) <= bound;
}
template<class T> bool compare_scalar(const ScanSpec& spec, const uint8_t* now, const uint8_t* previous) noexcept {
    const T a = load_value<T>(now);
    const bool uses_value = spec.mode == MS_EXACT || spec.mode == MS_GREATER_THAN || spec.mode == MS_LESS_THAN || spec.mode == MS_BETWEEN;
    const T b = load_value<T>(uses_value ? spec.value.data() : previous);
    if (spec.mode == MS_EXACT) return spec.approximate ? approximately_equal(spec, a, b) : a == b;
    if (spec.mode == MS_CHANGED || spec.mode == MS_UNCHANGED) {
        const bool equal = approximately_equal(spec, a, b);
        return spec.mode == MS_CHANGED ? (!equal && std::isfinite(a) && std::isfinite(b)) : equal;
    }
    if (spec.mode == MS_INCREASED || spec.mode == MS_GREATER_THAN) return a > b;
    if (spec.mode == MS_DECREASED || spec.mode == MS_LESS_THAN) return a < b;
    if (spec.mode == MS_BETWEEN) return a >= b && a <= load_value<T>(spec.upper.data());
    const T delta = load_value<T>(spec.value.data());
    if constexpr (std::is_floating_point_v<T>) {
        const long double target = spec.mode == MS_INCREASED_BY ? static_cast<long double>(b) + delta : static_cast<long double>(b) - delta;
        if (spec.approximate) return approximately_equal(spec, a, target);
        const T rounded = static_cast<T>(target);
        return std::isfinite(a) && std::isfinite(b) && std::isfinite(rounded) && a == rounded;
    } else {
        if (spec.mode == MS_INCREASED_BY) {
            if (b > std::numeric_limits<T>::max() - delta) return false;
            return a == static_cast<T>(b + delta);
        }
        if (b < std::numeric_limits<T>::min() + delta) return false;
        return a == static_cast<T>(b - delta);
    }
}
bool matches(const ScanSpec& spec, const uint8_t* now, const uint8_t* previous) noexcept {
    if (spec.mode == MS_UNKNOWN) return true;
    if (!spec.approximate && spec.mode == MS_CHANGED) return std::memcmp(now, previous, spec.width) != 0;
    if (!spec.approximate && spec.mode == MS_UNCHANGED) return std::memcmp(now, previous, spec.width) == 0;
    switch (spec.type) {
    case MS_U8: return compare_scalar<uint8_t>(spec, now, previous);
    case MS_I16: return compare_scalar<int16_t>(spec, now, previous);
    case MS_I32: return compare_scalar<int32_t>(spec, now, previous);
    case MS_I64: return compare_scalar<int64_t>(spec, now, previous);
    case MS_F32: return compare_scalar<float>(spec, now, previous);
    case MS_F64: return compare_scalar<double>(spec, now, previous);
    default:
        if (!spec.mask.empty()) {
            for (uint32_t index = 0; index < spec.width; ++index)
                if ((now[index] & spec.mask[index]) != (spec.value[index] & spec.mask[index])) return false;
            return true;
        }
        return std::memcmp(now, spec.value.data(), spec.width) == 0;
    }
}

template<class T> bool valid_bounds(const ScanSpec& spec) noexcept {
    const T low = load_value<T>(spec.value.data());
    if (spec.mode == MS_BETWEEN) {
        const T high = load_value<T>(spec.upper.data());
        if constexpr (std::is_floating_point_v<T>) return std::isfinite(low) && std::isfinite(high) && low <= high;
        return low <= high;
    }
    if (spec.mode == MS_INCREASED_BY || spec.mode == MS_DECREASED_BY) {
        if constexpr (std::is_floating_point_v<T>) return std::isfinite(low) && low >= 0;
        return low >= 0;
    }
    return true;
}
int32_t validate(Session* s, const ms_scan_request& request, bool next, const ms_scan_options* options, ScanSpec& spec) {
    if (request.type > MS_BYTES || request.mode > MS_DECREASED_BY || request.reserved != 0)
        return fail(s, MS_INVALID, "Invalid scan type, mode, or reserved field.");
    if (next && !s->has_scan) return fail(s, MS_INVALID, "A successful initial scan is required before filtering.");
    if (!next && request.mode != MS_EXACT && request.mode != MS_UNKNOWN && request.mode != MS_GREATER_THAN && request.mode != MS_LESS_THAN && request.mode != MS_BETWEEN)
        return fail(s, MS_INVALID, "An initial scan supports exact, unknown, greater/less than, or inclusive range.");
    if (next && request.mode == MS_UNKNOWN)
        return fail(s, MS_INVALID, "Unknown initial value cannot be used for a subsequent scan.");
    spec.type = request.type;
    spec.mode = request.mode;
    spec.width = scalar_width(request.type);
    if (!spec.width) {
        if (request.mode != MS_EXACT && request.mode != MS_CHANGED && request.mode != MS_UNCHANGED)
            return fail(s, MS_INVALID, "This scan mode is available only for scalar numeric values.");
        spec.width = request.mode == MS_EXACT ? request.value_size : s->previous_width;
        if (spec.width == 0 || spec.width > PatternLimit || (request.type == MS_UTF16 && (spec.width % 2 != 0)))
            return fail(s, MS_INVALID, "Text/byte patterns must be 1 to 1,048,576 bytes; UTF-16 must have even length.");
    }
    if (next && (request.type != s->previous_type || spec.width != s->previous_width))
        return fail(s, MS_INVALID, "Subsequent scans must retain the previous value type and byte width.");
    const bool value_needed = request.mode == MS_EXACT || request.mode >= MS_GREATER_THAN;
    if (value_needed) {
        if (!request.value || request.value_size != spec.width)
            return fail(s, MS_INVALID, "The exact value buffer must match the selected value width.");
        spec.value.assign(request.value, request.value + spec.width);
    }
    if (options) {
        if (options->struct_size != sizeof(ms_scan_options) || options->reserved || options->reserved2 || (options->flags & ~MS_APPROXIMATE))
            return fail(s, MS_INVALID, "Invalid extended scan options or structure size.");
        if (!std::isfinite(options->absolute_tolerance) || !std::isfinite(options->relative_tolerance) || options->absolute_tolerance < 0 || options->relative_tolerance < 0)
            return fail(s, MS_INVALID, "Float tolerances must be finite and nonnegative.");
        spec.approximate = (options->flags & MS_APPROXIMATE) != 0;
        if (spec.approximate && request.type != MS_F32 && request.type != MS_F64)
            return fail(s, MS_INVALID, "Approximate comparison is available only for float32 and float64.");
        spec.absolute_tolerance = options->absolute_tolerance;
        spec.relative_tolerance = options->relative_tolerance;
        if (request.mode == MS_BETWEEN) {
            if (!options->upper_value || options->upper_value_size != spec.width)
                return fail(s, MS_INVALID, "The inclusive range requires a same-width upper endpoint.");
            spec.upper.assign(options->upper_value, options->upper_value + spec.width);
        } else if (options->upper_value || options->upper_value_size)
            return fail(s, MS_INVALID, "An upper endpoint is used only for inclusive range scans.");
        if (options->pattern_mask || options->pattern_mask_size) {
            if (request.type != MS_BYTES || request.mode != MS_EXACT || !options->pattern_mask || options->pattern_mask_size != spec.width)
                return fail(s, MS_INVALID, "AOB bit masks require an exact byte scan and the same pattern length.");
            spec.mask.assign(options->pattern_mask, options->pattern_mask + spec.width);
        }
    }
    if (request.mode == MS_BETWEEN && spec.upper.empty()) return fail(s, MS_INVALID, "An inclusive range requires extended scan options with an upper endpoint.");
    if (request.mode >= MS_BETWEEN) {
        bool valid = false;
        switch (request.type) {
        case MS_U8: valid = valid_bounds<uint8_t>(spec); break;
        case MS_I16: valid = valid_bounds<int16_t>(spec); break;
        case MS_I32: valid = valid_bounds<int32_t>(spec); break;
        case MS_I64: valid = valid_bounds<int64_t>(spec); break;
        case MS_F32: valid = valid_bounds<float>(spec); break;
        case MS_F64: valid = valid_bounds<double>(spec); break;
        }
        if (!valid) return fail(s, MS_INVALID, "Range endpoints must be ordered finite values and specified deltas must be finite and nonnegative.");
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
    if (spec.limit > ResultLimit && request.mode != MS_UNKNOWN && !(next && s->unknown_origin))
        return fail(s, MS_INVALID, "The maximum supported result count is 2,000,000 for ordinary scans.");
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
    std::shared_ptr<DenseSnapshot> dense;
    int32_t add(Session* s, const ScanSpec& spec, uint64_t address, const uint8_t* value) {
        if (addresses.size() >= spec.limit)
            return fail(s, MS_LIMIT, "Result limit exceeded; narrow the range or use a more specific value. Previous results have been preserved.");
        addresses.push_back(address);
        snapshot.insert(snapshot.end(), value, value + spec.width);
        return MS_OK;
    }
};

int32_t create_dense(Session* s, uint32_t width, uint32_t stride, std::shared_ptr<DenseSnapshot>& result) {
    auto pending = std::make_shared<DenseSnapshot>();
    WCHAR directory[MAX_PATH + 1]{}, path[MAX_PATH + 1]{};
    const DWORD length = GetTempPathW(MAX_PATH, directory);
    if (!length || length >= MAX_PATH) return os_fail(s, "GetTempPath for unknown snapshot", length ? ERROR_FILENAME_EXCED_RANGE : GetLastError());
    if (!GetTempFileNameW(directory, L"mss", 0, path)) return os_fail(s, "Create temporary unknown snapshot");
    pending->file = CreateFileW(path, GENERIC_READ | GENERIC_WRITE | DELETE, FILE_SHARE_READ | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (pending->file == INVALID_HANDLE_VALUE) {
        const DWORD error = GetLastError(); DeleteFileW(path);
        return os_fail(s, "Open temporary unknown snapshot", error);
    }
    pending->width = width; pending->stride = stride; result = std::move(pending); return MS_OK;
}
int32_t dense_read(Session* s, DenseSnapshot& snapshot, uint64_t offset, uint8_t* bytes, uint32_t size) {
    if (!size) return MS_OK;
    LARGE_INTEGER position{}; position.QuadPart = static_cast<LONGLONG>(offset);
    if (!SetFilePointerEx(snapshot.file, position, nullptr, FILE_BEGIN)) return os_fail(s, "Seek unknown snapshot");
    DWORD received = 0;
    if (!ReadFile(snapshot.file, bytes, size, &received, nullptr)) return os_fail(s, "Read unknown snapshot");
    if (received != size) return os_fail(s, "Read unknown snapshot", ERROR_HANDLE_EOF);
    return MS_OK;
}
int32_t dense_write(Session* s, DenseSnapshot& snapshot, const uint8_t* bytes, uint32_t size) {
    if (!size) return MS_OK;
    LARGE_INTEGER position{}; position.QuadPart = static_cast<LONGLONG>(snapshot.file_bytes);
    if (!SetFilePointerEx(snapshot.file, position, nullptr, FILE_BEGIN)) return os_fail(s, "Seek new unknown snapshot");
    DWORD written = 0;
    if (!WriteFile(snapshot.file, bytes, size, &written, nullptr)) return os_fail(s, "Write unknown snapshot");
    if (written != size) return os_fail(s, "Write unknown snapshot", ERROR_DISK_FULL);
    snapshot.file_bytes += written; return MS_OK;
}
int32_t append_dense(Session* s, DenseSnapshot& snapshot, DenseBlock block, const uint8_t* bytes, const std::vector<uint8_t>& mask) {
    if (!block.count) return MS_OK;
    block.mask_bytes = static_cast<uint32_t>(mask.size());
    const uint64_t payload = uint64_t{block.bytes} + block.mask_bytes;
    if (payload > DenseFileLimit - snapshot.file_bytes)
        return fail(s, MS_LIMIT, "Unknown snapshot exceeds its 16 GiB file budget; narrow the address range. Previous results have been preserved.");
    block.prefix = snapshot.count; block.data_offset = snapshot.file_bytes; block.mask_offset = block.data_offset + block.bytes;
    snapshot.blocks.push_back(block);
    if (snapshot.metadata_bytes() > HistoryBudget)
        return fail(s, MS_LIMIT, "Unknown snapshot metadata exceeds 64 MiB; narrow the address range. Previous results have been preserved.");
    int32_t status = dense_write(s, snapshot, bytes, block.bytes);
    if (status != MS_OK) return status;
    if ((status = dense_write(s, snapshot, mask.data(), block.mask_bytes)) != MS_OK) return status;
    snapshot.count += block.count; return MS_OK;
}

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
            if (spec.mode == MS_UNKNOWN && candidate <= last) {
                DenseBlock block{}; block.start = begin; block.first = candidate;
                block.bytes = static_cast<uint32_t>(combined.size());
                block.slots = block.count = static_cast<uint32_t>((last - candidate) / spec.alignment + 1);
                const int32_t result = append_dense(s, *pending.dense, block, combined.data(), {});
                if (result != MS_OK) return result;
            }
            for (; spec.mode != MS_UNKNOWN && candidate <= last; candidate += spec.alignment) {
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
        s->progress_results.store(pending.dense ? pending.dense->count : pending.addresses.size());
        return MS_OK;
    };
    for (const auto& region : regions) {
        tail.clear();
        previous_end = 0;
        for (uint64_t address = region.start; address < region.end;) {
            if (cancelled(s)) return fail(s, MS_CANCELLED, "Scan cancelled; previous results have been preserved.");
            if (ensure_alive(s) != MS_OK) return MS_OS;
            std::unique_lock<std::mutex> trace_lock(ms_trace_registry_mutex());
            uint64_t skip_end = address;
            const uint64_t block_limit = std::min(region.end, address + std::min<uint64_t>(BlockSize, region.end - address));
            const uint64_t readable_end = ms_trace_read_end_locked(s->pid, address, block_limit, skip_end);
            if (readable_end == address) {
                s->scanned.fetch_add(skip_end - address); address = skip_end; tail.clear(); previous_end = 0; continue;
            }
            const size_t size = static_cast<size_t>(readable_end - address);
            SIZE_T received = 0;
            if (ReadProcessMemory(s->process, reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(address)), read_buffer.data(), size, &received) && received == size) {
                trace_lock.unlock();
                const auto result = consume(address, read_buffer.data(), size);
                if (result != MS_OK) return result;
                s->scanned.fetch_add(size);
            } else {
                trace_lock.unlock();
                // A protection change or inaccessible page must not discard the readable remainder of a large block.
                const uint64_t block_end = address + size;
                for (uint64_t page = address; page < block_end;) {
                    if (cancelled(s)) return fail(s, MS_CANCELLED, "Scan cancelled; previous results have been preserved.");
                    const uint64_t page_end = std::min(block_end, ((page / s->page_size) + 1) * s->page_size);
                    const size_t page_bytes = static_cast<size_t>(page_end - page);
                    received = 0;
                    std::unique_lock<std::mutex> page_trace_lock(ms_trace_registry_mutex());
                    if (ms_trace_overlaps_locked(s->pid, page, page_end)) {
                        tail.clear(); previous_end = 0; s->scanned.fetch_add(page_bytes); page = page_end; continue;
                    }
                    const BOOL read_ok = ReadProcessMemory(s->process, reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(page)), read_buffer.data(), page_bytes, &received);
                    page_trace_lock.unlock();
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

int32_t read_dense_current(Session* s, const DenseBlock& block, const std::vector<Region>& regions,
    std::vector<uint8_t>& bytes, std::vector<uint8_t>& valid) {
    const uint64_t end = block.start + block.bytes;
    auto region = std::lower_bound(regions.begin(), regions.end(), block.start,
        [](const Region& item, uint64_t address) { return item.end <= address; });
    for (; region != regions.end() && region->start < end; ++region) {
        uint64_t cursor = std::max(block.start, region->start), limit = std::min(end, region->end);
        while (cursor < limit) {
            if (cancelled(s)) return fail(s, MS_CANCELLED, "Scan cancelled; previous results have been preserved.");
            std::unique_lock<std::mutex> trace_lock(ms_trace_registry_mutex());
            uint64_t skip_end = cursor;
            const uint64_t read_end = ms_trace_read_end_locked(s->pid, cursor, limit, skip_end);
            if (read_end == cursor) { cursor = skip_end; continue; }
            const size_t offset = static_cast<size_t>(cursor - block.start), size = static_cast<size_t>(read_end - cursor);
            SIZE_T received = 0;
            const BOOL ok = ReadProcessMemory(s->process, reinterpret_cast<LPCVOID>(cursor), bytes.data() + offset, size, &received);
            trace_lock.unlock();
            if (ok && received == size) std::fill_n(valid.data() + offset, size, uint8_t{1});
            else {
                // Re-read page by page so one disappearing/protected page cannot
                // discard readable neighbours or create a comparison across a hole.
                for (uint64_t page = cursor; page < read_end;) {
                    if (cancelled(s)) return fail(s, MS_CANCELLED, "Scan cancelled; previous results have been preserved.");
                    const uint64_t page_end = std::min(read_end, ((page / s->page_size) + 1) * s->page_size);
                    const size_t page_offset = static_cast<size_t>(page - block.start), page_size = static_cast<size_t>(page_end - page);
                    std::unique_lock<std::mutex> page_lock(ms_trace_registry_mutex());
                    if (!ms_trace_overlaps_locked(s->pid, page, page_end)) {
                        received = 0;
                        ReadProcessMemory(s->process, reinterpret_cast<LPCVOID>(page), bytes.data() + page_offset, page_size, &received);
                        if (received && received <= page_size) std::fill_n(valid.data() + page_offset, static_cast<size_t>(received), uint8_t{1});
                    }
                    page = page_end;
                }
            }
            cursor = read_end;
        }
    }
    return ensure_alive(s);
}

int32_t subsequent_dense_scan(Session* s, const ScanSpec& spec, const std::vector<Region>& regions, PendingResults& pending) {
    const auto& previous = *s->dense;
    s->total.store(previous.count * spec.width);
    std::vector<uint8_t> old_bytes, current, valid, old_mask, new_mask;
    uint64_t iterations = 0;
    for (const auto& saved : previous.blocks) {
        if (cancelled(s)) return fail(s, MS_CANCELLED, "Scan cancelled; previous results have been preserved.");
        if (ensure_alive(s) != MS_OK) return MS_OS;
        old_bytes.resize(saved.bytes); current.assign(saved.bytes, 0); valid.assign(saved.bytes, 0);
        old_mask.resize(saved.mask_bytes); new_mask.assign((saved.slots + 7) / 8, 0);
        int32_t status = dense_read(s, *s->dense, saved.data_offset, old_bytes.data(), saved.bytes);
        if (status != MS_OK) return status;
        if ((status = dense_read(s, *s->dense, saved.mask_offset, old_mask.data(), saved.mask_bytes)) != MS_OK) return status;
        if ((status = read_dense_current(s, saved, regions, current, valid)) != MS_OK) return status;
        uint32_t kept = 0;
        for (uint32_t word = 0; word < saved.slots; word += 64) {
            uint64_t bits = UINT64_MAX;
            if (saved.mask_bytes) {
                bits = 0; const size_t at = word / 8;
                std::memcpy(&bits, old_mask.data() + at, std::min<size_t>(8, old_mask.size() - at));
            }
            if (saved.slots - word < 64) bits &= (uint64_t{1} << (saved.slots - word)) - 1;
            while (bits) {
                const uint32_t slot = word + std::countr_zero(bits); bits &= bits - 1;
                if ((++iterations & 1023) == 0) {
                    s->progress_results.store(pending.dense->count + kept);
                    if (cancelled(s)) return fail(s, MS_CANCELLED, "Scan cancelled; previous results have been preserved.");
                }
                const uint64_t address = saved.first + uint64_t{slot} * previous.stride;
                if (address % spec.alignment) continue;
                const size_t at = static_cast<size_t>(address - saved.start);
                bool readable_value = true;
                for (uint32_t byte = 0; byte < spec.width; ++byte) if (!valid[at + byte]) { readable_value = false; break; }
                if (readable_value && matches(spec, current.data() + at, old_bytes.data() + at)) {
                    new_mask[slot / 8] |= static_cast<uint8_t>(1u << (slot % 8)); ++kept;
                }
            }
        }
        DenseBlock block = saved; block.count = kept;
        if (kept == saved.slots) new_mask.clear(); // The common unchanged case needs no bitmap file payload.
        if ((status = append_dense(s, *pending.dense, block, current.data(), new_mask)) != MS_OK) return status;
        s->scanned.fetch_add(uint64_t{saved.count} * spec.width);
        s->progress_results.store(pending.dense->count);
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
        std::unique_lock<std::mutex> trace_lock(ms_trace_registry_mutex());
        if (ms_trace_overlaps_locked(s->pid, address, address + spec.width)) continue;
        if (address < cache_start || address >= cache_covered_end) {
            cache_start = address;
            uint64_t skip_end = address;
            const uint64_t read_end = ms_trace_read_end_locked(s->pid, address, address + std::min<uint64_t>(BlockSize, region.end - address), skip_end);
            const size_t size = static_cast<size_t>(read_end - address);
            SIZE_T received = 0;
            const BOOL read_ok = ReadProcessMemory(s->process, reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(address)), cache.data(), size, &received);
            if (read_ok && received == size) {
                cache_end = address + size;
                cache_covered_end = cache_end;
            } else {
                const uint64_t page_end = std::min(read_end, ((address / s->page_size) + 1) * s->page_size);
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
        trace_lock.unlock();
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
MS_API int32_t ms_scan_ex(void* opaque, const ms_scan_request* request, uint32_t next_scan, const ms_scan_options* options) {
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
        int32_t status = validate(s, *request, next_scan != 0, options, spec);
        if (status != MS_OK) return status;
        if ((status = ensure_alive(s)) != MS_OK) return status;
        std::vector<Region> regions;
        if ((status = regions_for(s, spec, regions)) != MS_OK) return status;
        PendingResults pending;
        const bool dense_origin = request->mode == MS_UNKNOWN || (next_scan && s->unknown_origin);
        if (dense_origin) {
            status = create_dense(s, spec.width, next_scan ? s->dense->stride : spec.alignment, pending.dense);
            if (status != MS_OK) return status;
        }
        status = next_scan ? (dense_origin ? subsequent_dense_scan(s, spec, regions, pending) : subsequent_scan(s, spec, regions, pending))
            : initial_scan(s, spec, regions, pending);
        if (status != MS_OK) return status;
        if (cancelled(s)) return fail(s, MS_CANCELLED, "Scan cancelled; previous results have been preserved.");
        const uint64_t previous_bytes = s->addresses.capacity() * sizeof(uint64_t) + s->snapshot.capacity() + (s->dense ? s->dense->metadata_bytes() : 0);
        if (previous_bytes <= HistoryBudget) {
            // Allocate the history node before mutating the live scan. Vector swaps
            // and subsequent eviction cannot throw: cancellation/failure stays transactional.
            s->history.emplace_back();
            auto& entry = s->history.back();
            entry.type = s->previous_type; entry.width = s->previous_width; entry.has_scan = s->has_scan;
            entry.unknown_origin = s->unknown_origin;
            entry.addresses.swap(s->addresses); entry.snapshot.swap(s->snapshot);
            entry.dense.swap(s->dense);
            s->history_bytes += entry.bytes();
            s->history_disk_bytes += entry.disk_bytes();
            while (s->history.size() > HistorySteps || s->history_bytes > HistoryBudget ||
                (s->history_disk_bytes > HistoryDiskBudget && s->history.size() > 1)) {
                s->history_bytes -= s->history.front().bytes(); s->history_disk_bytes -= s->history.front().disk_bytes(); s->history.pop_front();
            }
        } else { s->history.clear(); s->history_bytes = 0; s->history_disk_bytes = 0; }
        s->addresses.swap(pending.addresses);
        s->snapshot.swap(pending.snapshot);
        s->dense.swap(pending.dense); s->unknown_origin = dense_origin;
        s->previous_type = spec.type;
        s->previous_width = spec.width;
        s->has_scan = true;
        ++s->generation;
        s->progress_results.store(s->dense ? s->dense->count : s->addresses.size());
        clear_error(s);
        return MS_OK;
    } catch (const std::bad_alloc&) { return fail(s, MS_OS, "Not enough memory for the scan. Previous results have been preserved."); }
    catch (...) { return fail(s, MS_OS, "Unexpected native scan error. Previous results have been preserved."); }
}
MS_API int32_t ms_scan(void* opaque, const ms_scan_request* request, uint32_t next_scan) {
    return ms_scan_ex(opaque, request, next_scan, nullptr);
}
MS_API int32_t ms_undo_scan(void* opaque) {
    auto* s = static_cast<Session*>(opaque);
    try {
        if (!s) return fail(s, MS_INVALID, "A process session is required.");
        if (s->state.load() & 1) return fail(s, MS_BUSY, "Cannot undo while a scan is running.");
        if (s->history.empty()) return fail(s, MS_INVALID, "No retained scan history is available to undo.");
        auto& entry = s->history.back();
        s->history_bytes -= entry.bytes();
        s->history_disk_bytes -= entry.disk_bytes();
        s->addresses.swap(entry.addresses); s->snapshot.swap(entry.snapshot);
        s->dense.swap(entry.dense); s->unknown_origin = entry.unknown_origin;
        s->previous_type = entry.type; s->previous_width = entry.width; s->has_scan = entry.has_scan;
        s->history.pop_back(); ++s->generation;
        s->progress_results.store(s->dense ? s->dense->count : s->addresses.size());
        clear_error(s); return MS_OK;
    } catch (...) { return fail(s, MS_OS, "Unexpected error while restoring scan history."); }
}
MS_API void ms_get_scan_history(void* opaque, ms_scan_history_info* info) {
    try {
        if (!info) return;
        *info = {}; auto* s = static_cast<Session*>(opaque); if (!s) return;
        info->undo_count = static_cast<uint32_t>(s->history.size()); info->max_steps = HistorySteps;
        info->type = s->previous_type; info->byte_width = s->previous_width; info->has_scan = s->has_scan;
        info->reserved = s->unknown_origin ? 1u : 0u;
        info->used_bytes = s->history_bytes; info->budget_bytes = HistoryBudget; info->generation = s->generation;
    } catch (...) { if (info) *info = {}; }
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
    try { return s ? (s->dense ? s->dense->count : s->addresses.size()) : 0; }
    catch (...) { fail(s, MS_OS, "Unexpected native result error."); return 0; }
}
MS_API uint32_t ms_get_results(void* opaque, uint64_t offset, uint64_t* addresses, uint32_t capacity) {
    auto* s = static_cast<Session*>(opaque);
    try {
        if (!s || (!addresses && capacity)) { fail(s, MS_INVALID, "A session and result buffer are required."); return 0; }
        if (s->dense) {
            const auto& dense = *s->dense;
            if (offset >= dense.count || !capacity) return 0;
            uint32_t copied = 0;
            auto block = std::lower_bound(dense.blocks.begin(), dense.blocks.end(), offset,
                [](const DenseBlock& item, uint64_t index) { return item.prefix + item.count <= index; });
            std::vector<uint8_t> mask;
            uint64_t skip = offset - block->prefix;
            for (; block != dense.blocks.end() && copied < capacity; ++block, skip = 0) {
                if (!block->mask_bytes) {
                    const uint32_t take = static_cast<uint32_t>(std::min<uint64_t>(capacity - copied, block->count - skip));
                    for (uint32_t index = 0; index < take; ++index)
                        addresses[copied++] = block->first + (skip + index) * dense.stride;
                    continue;
                }
                mask.resize(block->mask_bytes);
                if (dense_read(s, *s->dense, block->mask_offset, mask.data(), block->mask_bytes) != MS_OK) return 0;
                for (uint32_t word = 0; word < block->slots && copied < capacity; word += 64) {
                    uint64_t bits = 0; const size_t at = word / 8;
                    std::memcpy(&bits, mask.data() + at, std::min<size_t>(8, mask.size() - at));
                    const uint32_t hits = std::popcount(bits);
                    if (skip >= hits) { skip -= hits; continue; }
                    while (skip) { bits &= bits - 1; --skip; }
                    while (bits && copied < capacity) {
                        const uint32_t slot = word + std::countr_zero(bits); bits &= bits - 1;
                        addresses[copied++] = block->first + uint64_t{slot} * dense.stride;
                    }
                }
            }
            return copied;
        }
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
        std::lock_guard<std::mutex> trace_lock(ms_trace_registry_mutex());
        const uint64_t end = address + size;
        if (ms_trace_overlaps_locked(s->pid, address, end)) return fail(s, MS_BUSY, "This page is reserved by an active access trace; reading it would disturb its guard.");
        for (uint64_t cursor = address; cursor < end;) {
            MEMORY_BASIC_INFORMATION info{};
            if (!VirtualQueryEx(s->process, reinterpret_cast<LPCVOID>(cursor), &info, sizeof(info))) return os_fail(s, "VirtualQueryEx before reading");
            if (info.State != MEM_COMMIT || !readable(info.Protect)) return fail(s, MS_ACCESS, "The requested read includes inaccessible or guard-protected memory.");
            const uint64_t base = reinterpret_cast<uintptr_t>(info.BaseAddress);
            if (!info.RegionSize || base > UINT64_MAX - info.RegionSize || base + info.RegionSize <= cursor) return fail(s, MS_OS, "Invalid region before reading.");
            cursor = std::min(end, base + info.RegionSize);
        }
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
        std::lock_guard<std::mutex> trace_lock(ms_trace_registry_mutex());
        const uint64_t end = address + size;
        if (ms_trace_overlaps_locked(s->pid, address, end)) return fail(s, MS_BUSY, "This page is reserved by an active access trace; writing it would disturb the captured instruction.");
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
MS_API int32_t ms_write_code(void* opaque, uint64_t address, const uint8_t* buffer, uint32_t size) {
    auto* s = static_cast<Session*>(opaque);
    try {
        if (!s || !buffer || !size || size > PatternLimit || address > UINT64_MAX - size)
            return fail(s, MS_INVALID, "An explicit code patch requires a valid address and 1..1,048,576 bytes.");
        if (ensure_alive(s) != MS_OK) return MS_OS;
        std::lock_guard<std::mutex> trace_lock(ms_trace_registry_mutex());
        std::vector<CodeRegion> regions;
        const uint64_t end = address + size;
        if (ms_trace_overlaps_locked(s->pid, address, end)) return fail(s, MS_BUSY, "Cannot patch a page reserved by an active access trace.");
        for (uint64_t cursor = address; cursor < end;) {
            MEMORY_BASIC_INFORMATION info{};
            if (!VirtualQueryEx(s->process, reinterpret_cast<LPCVOID>(cursor), &info, sizeof(info))) return os_fail(s, "VirtualQueryEx before code patch");
            if (info.State != MEM_COMMIT || (info.Protect & (PAGE_GUARD | PAGE_NOACCESS)) || !(info.Protect & 0xff))
                return fail(s, MS_ACCESS, "Code patches cannot include uncommitted, guard, or inaccessible pages.");
            const uint64_t base = reinterpret_cast<uintptr_t>(info.BaseAddress);
            if (!info.RegionSize || base > UINT64_MAX - info.RegionSize || base + info.RegionSize <= cursor)
                return fail(s, MS_OS, "Invalid memory region before code patch.");
            const uint64_t region_end = std::min(end, base + info.RegionSize);
            regions.push_back({cursor, region_end - cursor, info.Protect}); cursor = region_end;
        }
        if (!s->write_process) {
            s->write_process = OpenProcess(PROCESS_VM_WRITE | PROCESS_VM_OPERATION, FALSE, s->pid);
            if (!s->write_process) return os_fail(s, "OpenProcess for code patch");
        }
        CodeRestore restore{s, regions, address, size};
        for (auto& region : regions) {
            const DWORD base = region.protection & 0xff;
            const bool executable = base == PAGE_EXECUTE || base == PAGE_EXECUTE_READ || base == PAGE_EXECUTE_READWRITE || base == PAGE_EXECUTE_WRITECOPY;
            DWORD previous = 0;
            if (!VirtualProtectEx(s->write_process, reinterpret_cast<LPVOID>(region.address), region.size,
                                  executable ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE, &previous)) {
                const DWORD error = GetLastError(); restore.restore();
                if (restore.restore_error) return os_fail(s, "Restore page protection after rejected code patch", restore.restore_error);
                return os_fail(s, "Make code patch region writable", error);
            }
            region.protection = previous; region.changed = true;
        }
        SIZE_T written = 0;
        restore.flush = true; // Flush even a partially failed write before reporting its failure.
        const BOOL ok = WriteProcessMemory(s->write_process, reinterpret_cast<LPVOID>(address), buffer, size, &written);
        const DWORD error = ok ? ERROR_PARTIAL_COPY : GetLastError();
        restore.restore();
        if (restore.restore_error) return os_fail(s, "Restore original code page protection", restore.restore_error);
        if (restore.flush_error) return os_fail(s, "FlushInstructionCache after code patch", restore.flush_error);
        if (!ok || written != size) return os_fail(s, "WriteProcessMemory code patch", error);
        clear_error(s); return MS_OK;
    } catch (const std::bad_alloc&) { return fail(s, MS_OS, "Not enough memory for the code patch protection list."); }
    catch (...) { return fail(s, MS_OS, "Unexpected native code patch error."); }
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
