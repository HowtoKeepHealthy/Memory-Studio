#pragma once
#include <stdint.h>
#ifdef _WIN32
#define MS_TRACE_API __declspec(dllexport)
#else
#define MS_TRACE_API
#endif
#ifdef __cplusplus
extern "C" {
#endif
// x64 cdecl, default 8-byte packing. All errors are UTF-8. Status codes match memory_core.h.
typedef enum ms_trace_mode { MS_TRACE_ACCESS=0, MS_TRACE_WRITE=1 } ms_trace_mode;
typedef enum ms_trace_access { MS_TRACE_READ_ACCESS=0, MS_TRACE_WRITE_ACCESS=1, MS_TRACE_EXECUTE_ACCESS=8 } ms_trace_access;
typedef struct ms_trace_event {
    uint64_t sequence;
    uint64_t instruction_pointer; // Exact faulting instruction, before execution; never a post-trap RIP.
    uint64_t fault_address;       // OS-reported access start. May precede watch range for wide instructions.
    uint64_t timestamp_ms;        // GetTickCount64.
    uint32_t thread_id;
    uint32_t access_kind;         // 0=read, 1=write, 8=execute.
    uint32_t bitness;             // 32 or 64.
    uint32_t code_size;           // Valid code_bytes count, 0..15.
    uint64_t registers[16];       // RAX,RCX,RDX,RBX,RSP,RBP,RSI,RDI,R8..R15; x86 high registers zero.
    uint64_t flags;               // EFLAGS/RFLAGS at fault.
    uint8_t code_bytes[16];       // Up to 15 original instruction bytes; final byte padding.
    uint32_t reserved[2];
} ms_trace_event;
typedef struct ms_trace_state {
    uint32_t running;
    uint32_t attached;
    uint32_t bitness;
    uint32_t mode;
    uint64_t total_events;
    uint64_t dropped_events;
    uint64_t watch_address;
    uint32_t watch_size;
    uint32_t status;
} ms_trace_state;
// Watches the page(s) containing the explicitly supplied 1..4096-byte range.
// Events are page-level candidates. The frontend must decode code_bytes + registers to
// filter the actual memory operand overlap; do not count unrelated same-page fields.
// ACCESS includes reads and writes (RMW is also an access). WRITE emits only writes.
// Existing PAGE_GUARD or PAGE_NOACCESS pages and self-attachment are rejected.
// Debugger must be exclusive: a target already under another debugger cannot attach.
MS_TRACE_API void* ms_trace_start(uint32_t pid, uint64_t address, uint32_t size, uint32_t mode);
MS_TRACE_API uint32_t ms_trace_poll(void* session, ms_trace_event* events, uint32_t capacity);
MS_TRACE_API void ms_trace_get_state(void* session, ms_trace_state* state);
MS_TRACE_API int32_t ms_trace_stop(void* session); // One cleanup attempt; joins only after detach. Retained attachment can retry Stop.
MS_TRACE_API void ms_trace_close(void* session); // Stop + free only when detached; otherwise retains session for retry. Never terminates target.
MS_TRACE_API uint32_t ms_trace_error(void* session, char* buffer, uint32_t capacity);
// Startup failures normally return null; ms_trace_error(null, ...) reads the thread-local error.
// If startup failed but cleanup could not detach, returns a recovery session with
// running=0/status!=0/attached=1. Retain it and retry Stop until attached=0 before Close.
// Poll/state/error may run concurrently with the worker. Stop/close must be serialized;
// the owner must keep the session alive during poll/state/error. Only one trace per target.
#ifdef __cplusplus
}
#endif
