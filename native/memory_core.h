#pragma once
#include <stdint.h>
#ifdef _WIN32
#define MS_API __declspec(dllexport)
#else
#define MS_API
#endif
#ifdef __cplusplus
extern "C" {
#endif
// ABI uses default 8-byte structure alignment, x64, cdecl, UTF-8 errors.
typedef enum ms_type { MS_U8=0, MS_I16=1, MS_I32=2, MS_I64=3, MS_F32=4, MS_F64=5, MS_UTF8=6, MS_UTF16=7, MS_BYTES=8 } ms_type;
typedef enum ms_mode { MS_EXACT=0, MS_UNKNOWN=1, MS_CHANGED=2, MS_UNCHANGED=3, MS_INCREASED=4, MS_DECREASED=5,
    MS_GREATER_THAN=6, MS_LESS_THAN=7, MS_BETWEEN=8, MS_INCREASED_BY=9, MS_DECREASED_BY=10 } ms_mode;
typedef enum ms_status { MS_OK=0, MS_INVALID=1, MS_ACCESS=2, MS_OS=3, MS_BUSY=4, MS_CANCELLED=5, MS_LIMIT=6 } ms_status;
typedef struct ms_scan_request {
    uint32_t type;
    uint32_t mode;
    uint64_t start_address; // Inclusive. Zero means default minimum.
    uint64_t end_address;   // Exclusive. Zero means process maximum.
    const uint8_t* value;
    uint32_t value_size;
    uint32_t alignment;    // 1 or scalar width.
    uint64_t max_results;  // Ordinary scans: zero means 2,000,000; exceeding fails transactionally.
                          // Unknown initial snapshots and all their subsequent filters ignore this candidate limit.
    uint32_t writable_only;
    uint32_t reserved; // Must be zero.
} ms_scan_request;
typedef enum ms_scan_flags { MS_APPROXIMATE=1 } ms_scan_flags;
typedef struct ms_scan_options {
    uint32_t struct_size; // sizeof(ms_scan_options), currently 56.
    uint32_t flags;       // MS_APPROXIMATE; only float32/float64.
    double absolute_tolerance;
    double relative_tolerance;
    const uint8_t* upper_value; // Inclusive upper endpoint for MS_BETWEEN, encoded in the selected numeric type.
    uint32_t upper_value_size;
    uint32_t reserved;
    const uint8_t* pattern_mask; // MS_BYTES+MS_EXACT: (actual & mask) == (value & mask); 0 ignores, FF exact.
    uint32_t pattern_mask_size;
    uint32_t reserved2;
} ms_scan_options;
typedef struct ms_scan_history_info {
    uint32_t undo_count;
    uint32_t max_steps;
    uint32_t type;
    uint32_t byte_width;
    uint32_t has_scan;
    uint32_t reserved; // Output flags: bit 0 = unknown-initial-value origin (file-backed snapshot); other bits zero.
    uint64_t used_bytes;
    uint64_t budget_bytes;
    uint64_t generation;
} ms_scan_history_info;
typedef struct ms_progress {
    uint64_t scanned_bytes;
    uint64_t total_bytes;
    uint64_t result_count;
    uint32_t running;
    uint32_t cancelled;
    double elapsed_ms;
} ms_progress;
MS_API void* ms_open(uint32_t pid);
MS_API void ms_close(void* session);
MS_API int32_t ms_scan(void* session, const ms_scan_request* request, uint32_t next_scan);
MS_API int32_t ms_scan_ex(void* session, const ms_scan_request* request, uint32_t next_scan, const ms_scan_options* options);
// Approximate equality: finite abs(a-b) <= max(abs_tol, rel_tol*max(abs(a),abs(b))).
// Applies to EXACT/CHANGED/UNCHANGED and specified-delta modes; ordered comparisons stay strict.
// INCREASED_BY/DECREASED_BY compare to the previous successful snapshot; delta must be nonnegative.
// Historical candidates AND snapshots are retained up to 16 steps/64 MiB of metadata/vector RAM.
// Unknown-origin scans use 1 MiB raw chunks plus at most width-1 boundary bytes and candidate bitmaps
// in delete-on-close temporary files. Each file is limited to 16 GiB, each snapshot's metadata to 64 MiB.
// History metadata/vector RAM is limited to 64 MiB. History files have a 4 GiB soft budget,
// retaining at least the latest undo snapshot even when that single file exceeds 4 GiB.
// used_bytes reports retained metadata/vector RAM; file payload is excluded from that field.
// Undo can restore the no-scan state; oldest entries are evicted to fit budget. No redo.
// Failure/cancellation changes neither current scan nor history; ms_scan also records history.
MS_API int32_t ms_undo_scan(void* session);
MS_API void ms_get_scan_history(void* session, ms_scan_history_info* info);
MS_API void ms_cancel(void* session);
MS_API void ms_get_progress(void* session, ms_progress* progress);
MS_API uint64_t ms_result_count(void* session);
MS_API uint32_t ms_get_results(void* session, uint64_t offset, uint64_t* addresses, uint32_t capacity);
MS_API int32_t ms_read(void* session, uint64_t address, uint8_t* buffer, uint32_t size);
MS_API int32_t ms_write(void* session, uint64_t address, const uint8_t* buffer, uint32_t size);
// Explicit code patch: at most 1 MiB; temporarily makes committed non-guard pages writable,
// restores each original region's protection, and flushes the target instruction cache.
// Does not suspend target threads. Ordinary ms_write remains strictly writable-only.
MS_API int32_t ms_write_code(void* session, uint64_t address, const uint8_t* buffer, uint32_t size);
MS_API uint32_t ms_error(void* session, char* buffer, uint32_t capacity);
// Calls on one session must be serialized except ms_cancel/ms_get_progress.
// A cancelled/failed scan preserves the previous successful scan and snapshot.
#ifdef __cplusplus
}
#endif
