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
typedef enum ms_mode { MS_EXACT=0, MS_UNKNOWN=1, MS_CHANGED=2, MS_UNCHANGED=3, MS_INCREASED=4, MS_DECREASED=5 } ms_mode;
typedef enum ms_status { MS_OK=0, MS_INVALID=1, MS_ACCESS=2, MS_OS=3, MS_BUSY=4, MS_CANCELLED=5, MS_LIMIT=6 } ms_status;
typedef struct ms_scan_request {
    uint32_t type;
    uint32_t mode;
    uint64_t start_address; // Inclusive. Zero means default minimum.
    uint64_t end_address;   // Exclusive. Zero means process maximum.
    const uint8_t* value;
    uint32_t value_size;
    uint32_t alignment;    // 1 or scalar width.
    uint64_t max_results;  // Zero means 2,000,000. Exceeding the limit fails transactionally.
    uint32_t writable_only;
    uint32_t reserved;
} ms_scan_request;
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
MS_API void ms_cancel(void* session);
MS_API void ms_get_progress(void* session, ms_progress* progress);
MS_API uint64_t ms_result_count(void* session);
MS_API uint32_t ms_get_results(void* session, uint64_t offset, uint64_t* addresses, uint32_t capacity);
MS_API int32_t ms_read(void* session, uint64_t address, uint8_t* buffer, uint32_t size);
MS_API int32_t ms_write(void* session, uint64_t address, const uint8_t* buffer, uint32_t size);
MS_API uint32_t ms_error(void* session, char* buffer, uint32_t capacity);
// Calls on one session must be serialized except ms_cancel/ms_get_progress.
// A cancelled/failed scan preserves the previous successful scan and snapshot.
#ifdef __cplusplus
}
#endif
