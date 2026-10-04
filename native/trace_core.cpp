#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <tlhelp32.h>
#include "trace_core.h"
#include "memory_core.h"
#include "trace_registry.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <map>
#include <mutex>
#include <thread>
#include <vector>

static_assert(sizeof(ms_trace_event) == 208 && sizeof(ms_trace_state) == 48, "Trace ABI packing changed.");

namespace {
constexpr DWORD GuardException = 0x80000001, WowBreakpoint = 0x4000001f, WowSingleStep = 0x4000001e;
constexpr DWORD TrapFlag = 0x100;
constexpr size_t QueueCapacity = 1024;
thread_local char startup_error[512]{};

struct Page { uint64_t address, allocation; DWORD protection; bool changed = false; };
struct Thread {
    HANDLE handle = nullptr;
    bool suspended = false, trap_owned = false, original_trap = false, stale_trap = false;
};
struct Trace;
std::mutex registry_gate;
std::vector<Trace*> registry;
void release_trace(Trace* t);
struct Trace {
    DWORD pid = 0;
    uint64_t address = 0;
    uint32_t size = 0, mode = 0, bitness = 64;
    HANDLE process = nullptr;
    uint64_t page_size = 4096;
    std::vector<Page> pages;
    std::map<DWORD, Thread> threads;
    DWORD stepping = 0;
    uint64_t step_tick = 0;
    bool initial_break = false, process_exited = false;
    bool cleanup_safe = false, has_pending = false;
    DEBUG_EVENT pending{};
    DWORD pending_status = DBG_CONTINUE;
    std::atomic<bool> stop{false}, running{false}, attached{false};
    std::atomic<uint32_t> status{MS_OK};
    std::atomic<uint64_t> total{0}, dropped{0};
    std::thread worker;
    std::mutex gate;
    std::condition_variable startup;
    std::condition_variable stop_completed_cv;
    bool startup_done = false, startup_ok = false;
    uint64_t stop_requested = 0, stop_completed = 0;
    std::atomic<bool> worker_finished{false};
    char error[512]{};
    std::array<ms_trace_event, QueueCapacity> queue{};
    size_t head = 0, count = 0;
    ~Trace() {
        release_trace(this);
        for (auto& [id, thread] : threads) { (void)id; if (thread.handle) CloseHandle(thread.handle); }
        if (process) CloseHandle(process);
    }
};
void release_trace(Trace* t) {
    std::lock_guard<std::mutex> lock(registry_gate);
    registry.erase(std::remove(registry.begin(), registry.end(), t), registry.end());
}
bool reserve_trace(Trace* t) {
    std::lock_guard<std::mutex> lock(registry_gate);
    for (const auto* other : registry) if (other->pid == t->pid) return false;
    registry.push_back(t); return true;
}
bool has_trace(DWORD pid) {
    std::lock_guard<std::mutex> lock(registry_gate);
    return std::any_of(registry.begin(), registry.end(), [pid](const Trace* t) { return t->pid == pid; });
}

void plain_error(Trace* t, uint32_t status, const char* message) {
    std::lock_guard<std::mutex> lock(t->gate);
    if (t->status.load() == MS_OK || !t->error[0]) {
        std::snprintf(t->error, sizeof(t->error), "%s", message);
        t->status.store(status);
    }
}
void os_error(Trace* t, const char* operation, DWORD code = GetLastError()) {
    WCHAR wide[180]{};
    char detail[300]{}, message[512]{};
    FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr, code, 0, wide, 180, nullptr);
    WideCharToMultiByte(CP_UTF8, 0, wide, -1, detail, sizeof(detail), nullptr, nullptr);
    for (char* p = detail; *p; ++p) if (*p == '\r' || *p == '\n') *p = ' ';
    std::snprintf(message, sizeof(message), "%s failed (Windows %lu): %s", operation, static_cast<unsigned long>(code), detail);
    plain_error(t, code == ERROR_ACCESS_DENIED ? MS_ACCESS : MS_OS, message);
}
void signal_startup(Trace* t, bool ok) {
    std::lock_guard<std::mutex> lock(t->gate);
    if (!t->startup_done) { t->startup_ok = ok; t->startup_done = true; t->startup.notify_all(); }
}
void complete_stop_attempt(Trace* t, bool finished = false) {
    std::lock_guard<std::mutex> lock(t->gate);
    t->stop_completed = t->stop_requested;
    if (finished) t->worker_finished.store(true);
    t->stop_completed_cv.notify_all();
}
bool alive(Trace* t) { return WaitForSingleObject(t->process, 0) == WAIT_TIMEOUT; }
bool is_break(DWORD code) { return code == EXCEPTION_BREAKPOINT || code == WowBreakpoint; }
bool is_step(DWORD code) { return code == EXCEPTION_SINGLE_STEP || code == WowSingleStep; }
Page* owned_page(Trace* t, uint64_t address) {
    for (auto& page : t->pages) if (address >= page.address && address - page.address < t->page_size) return &page;
    return nullptr;
}

struct CpuContext {
    CONTEXT native{};
    WOW64_CONTEXT wow{};
    bool is_wow = false;
    DWORD flags() const { return is_wow ? wow.EFlags : native.EFlags; }
    void flags(DWORD value) { if (is_wow) wow.EFlags = value; else native.EFlags = value; }
    void snapshot(ms_trace_event& event) const {
        event.flags = flags();
        if (is_wow) {
            const uint64_t values[] = {wow.Eax, wow.Ecx, wow.Edx, wow.Ebx, wow.Esp, wow.Ebp, wow.Esi, wow.Edi};
            std::copy_n(values, 8, event.registers);
        } else {
            const uint64_t values[] = {native.Rax, native.Rcx, native.Rdx, native.Rbx, native.Rsp, native.Rbp,
                native.Rsi, native.Rdi, native.R8, native.R9, native.R10, native.R11, native.R12, native.R13, native.R14, native.R15};
            std::copy_n(values, 16, event.registers);
        }
    }
};
bool get_context(Trace* t, Thread& thread, CpuContext& context) {
    context.is_wow = t->bitness == 32;
    if (context.is_wow) {
        context.wow.ContextFlags = WOW64_CONTEXT_CONTROL | WOW64_CONTEXT_INTEGER;
        if (!Wow64GetThreadContext(thread.handle, &context.wow)) { os_error(t, "Wow64GetThreadContext"); return false; }
    } else {
        context.native.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
        if (!GetThreadContext(thread.handle, &context.native)) { os_error(t, "GetThreadContext"); return false; }
    }
    return true;
}
bool set_context(Trace* t, Thread& thread, CpuContext& context) {
    const BOOL ok = context.is_wow ? Wow64SetThreadContext(thread.handle, &context.wow) : SetThreadContext(thread.handle, &context.native);
    if (!ok) { os_error(t, context.is_wow ? "Wow64SetThreadContext" : "SetThreadContext"); return false; }
    return true;
}

Thread* add_thread(Trace* t, DWORD id) {
    auto existing = t->threads.find(id);
    if (existing != t->threads.end()) return &existing->second;
    HANDLE handle = OpenThread(THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_SUSPEND_RESUME | THREAD_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, id);
    if (!handle) { os_error(t, "OpenThread"); return nullptr; }
    const DWORD owner = GetProcessIdOfThread(handle);
    if (owner != t->pid) { const DWORD error = owner ? ERROR_INVALID_PARAMETER : GetLastError(); CloseHandle(handle); os_error(t, "Verify trace thread owner", error); return nullptr; }
    try { return &t->threads.emplace(id, Thread{handle}).first->second; }
    catch (...) { CloseHandle(handle); throw; }
}
bool suspend(Trace* t, Thread& thread) {
    if (thread.suspended || WaitForSingleObject(thread.handle, 0) == WAIT_OBJECT_0) return true;
    if (SuspendThread(thread.handle) == static_cast<DWORD>(-1)) {
        if (WaitForSingleObject(thread.handle, 0) == WAIT_OBJECT_0) return true;
        os_error(t, "SuspendThread"); return false;
    }
    thread.suspended = true;
    return true;
}
bool resume(Trace* t, Thread& thread) {
    if (!thread.suspended) return true;
    if (ResumeThread(thread.handle) == static_cast<DWORD>(-1) && WaitForSingleObject(thread.handle, 0) != WAIT_OBJECT_0) {
        os_error(t, "ResumeThread"); return false;
    }
    thread.suspended = false; // Undo precisely one increment made by this debugger, never other owners' counts.
    return true;
}
bool suspend_peers(Trace* t, DWORD except) {
    for (auto& [id, thread] : t->threads) if (id != except && !suspend(t, thread)) return false;
    return true;
}
bool resume_peers(Trace* t) {
    bool ok = true;
    for (auto& [id, thread] : t->threads) { (void)id; if (!resume(t, thread)) ok = false; }
    return ok;
}
bool restore_trap(Trace* t, Thread& thread, bool stale = false) {
    if (!thread.trap_owned) return true;
    if (WaitForSingleObject(thread.handle, 0) == WAIT_OBJECT_0) { thread.trap_owned = false; return true; }
    CpuContext context;
    if (!get_context(t, thread, context)) return false;
    context.flags(thread.original_trap ? context.flags() | TrapFlag : context.flags() & ~TrapFlag);
    if (!set_context(t, thread, context)) return false;
    thread.trap_owned = false;
    thread.stale_trap = stale;
    return true;
}

bool protect_pages(Trace* t, bool guard) {
    for (auto& page : t->pages) {
        MEMORY_BASIC_INFORMATION info{};
        if (!VirtualQueryEx(t->process, reinterpret_cast<LPCVOID>(page.address), &info, sizeof(info))) { os_error(t, "VirtualQueryEx trace page"); return false; }
        if (info.State != MEM_COMMIT || reinterpret_cast<uintptr_t>(info.AllocationBase) != page.allocation) {
            plain_error(t, MS_OS, "The watched memory was unmapped or replaced; tracing stopped."); return false;
        }
        if ((info.Protect & ~PAGE_GUARD) != page.protection) {
            plain_error(t, MS_OS, "The watched page protection was changed by the target or another tool; tracing stopped."); return false;
        }
        DWORD previous = 0;
        if (!VirtualProtectEx(t->process, reinterpret_cast<LPVOID>(page.address), t->page_size,
                              page.protection | (guard ? PAGE_GUARD : 0), &previous)) {
            os_error(t, "VirtualProtectEx trace page"); return false;
        }
        page.changed = true;
    }
    return true;
}
bool arm_initial(Trace* t) {
    // Target is globally stopped at its initial attach breakpoint. Recheck against the startup query.
    for (auto& page : t->pages) {
        MEMORY_BASIC_INFORMATION info{};
        if (!VirtualQueryEx(t->process, reinterpret_cast<LPCVOID>(page.address), &info, sizeof(info))) { os_error(t, "VirtualQueryEx before arming"); return false; }
        if (info.State != MEM_COMMIT || (info.Protect & (PAGE_GUARD | PAGE_NOACCESS)) ||
            info.Protect != page.protection || reinterpret_cast<uintptr_t>(info.AllocationBase) != page.allocation) {
            plain_error(t, MS_INVALID, "The watched pages changed or already use PAGE_GUARD/PAGE_NOACCESS; attachment cancelled."); return false;
        }
    }
    return protect_pages(t, true);
}

void enqueue(Trace* t, ms_trace_event& event) {
    std::lock_guard<std::mutex> lock(t->gate);
    event.sequence = t->total.fetch_add(1) + 1;
    if (t->count == QueueCapacity) { t->head = (t->head + 1) % QueueCapacity; --t->count; t->dropped.fetch_add(1); }
    t->queue[(t->head + t->count) % QueueCapacity] = event;
    ++t->count;
}

bool handle_guard(Trace* t, const DEBUG_EVENT& debug) {
    const auto& exception = debug.u.Exception.ExceptionRecord;
    if (exception.NumberParameters < 2 || !owned_page(t, exception.ExceptionInformation[1])) return false;
    Thread* thread = add_thread(t, debug.dwThreadId);
    if (!thread) { t->stop.store(true); return true; }
    if (t->stepping && t->stepping != debug.dwThreadId) {
        auto previous = t->threads.find(t->stepping);
        if (previous != t->threads.end() && !restore_trap(t, previous->second, true)) t->stop.store(true);
        resume_peers(t);
        t->stepping = 0;
    }
    if (!suspend_peers(t, debug.dwThreadId) || !protect_pages(t, false)) { t->stop.store(true); return true; }
    CpuContext context;
    if (!get_context(t, *thread, context)) { t->stop.store(true); return true; }
    ms_trace_event event{};
    event.instruction_pointer = reinterpret_cast<uintptr_t>(exception.ExceptionAddress);
    event.fault_address = exception.ExceptionInformation[1];
    event.timestamp_ms = GetTickCount64();
    event.thread_id = debug.dwThreadId;
    event.access_kind = static_cast<uint32_t>(exception.ExceptionInformation[0]);
    event.bitness = t->bitness;
    context.snapshot(event);
    // Guard is removed while all target threads are stopped. Code reads cannot accidentally consume our guards.
    for (uint32_t index = 0; index < 15 && event.instruction_pointer <= UINT64_MAX - index; ++index) {
        SIZE_T read = 0;
        if (!ReadProcessMemory(t->process, reinterpret_cast<LPCVOID>(event.instruction_pointer + index), &event.code_bytes[index], 1, &read) || read != 1) break;
        ++event.code_size;
    }
    if (!thread->trap_owned) thread->original_trap = (context.flags() & TrapFlag) != 0;
    context.flags(context.flags() | TrapFlag);
    if (!set_context(t, *thread, context)) { t->stop.store(true); return true; }
    thread->trap_owned = true;
    t->stepping = debug.dwThreadId;
    t->step_tick = GetTickCount64();
    if ((event.access_kind == MS_TRACE_READ_ACCESS || event.access_kind == MS_TRACE_WRITE_ACCESS) &&
        (t->mode == MS_TRACE_ACCESS || event.access_kind == MS_TRACE_WRITE_ACCESS)) enqueue(t, event);
    return true;
}

DWORD handle_event(Trace* t, const DEBUG_EVENT& debug, bool stopping) {
    switch (debug.dwDebugEventCode) {
    case CREATE_PROCESS_DEBUG_EVENT:
        if (debug.u.CreateProcessInfo.hFile) CloseHandle(debug.u.CreateProcessInfo.hFile);
        if (auto* thread = add_thread(t, debug.dwThreadId); thread && ((stopping && !t->cleanup_safe) || t->stepping)) suspend(t, *thread);
        break;
    case CREATE_THREAD_DEBUG_EVENT:
        if (auto* thread = add_thread(t, debug.dwThreadId); thread && ((stopping && !t->cleanup_safe) || t->stepping)) suspend(t, *thread);
        break;
    case LOAD_DLL_DEBUG_EVENT:
        if (debug.u.LoadDll.hFile) CloseHandle(debug.u.LoadDll.hFile);
        break;
    case EXIT_THREAD_DEBUG_EVENT: {
        auto found = t->threads.find(debug.dwThreadId);
        if (found != t->threads.end()) { CloseHandle(found->second.handle); t->threads.erase(found); }
        if (t->stepping == debug.dwThreadId) {
            t->stepping = 0;
            if (!stopping && !protect_pages(t, true)) t->stop.store(true);
            resume_peers(t);
        }
        break;
    }
    case EXIT_PROCESS_DEBUG_EVENT:
        t->process_exited = true;
        t->stop.store(true);
        plain_error(t, MS_OS, "The target process exited; tracing stopped.");
        break;
    case EXCEPTION_DEBUG_EVENT: {
        const auto& exception = debug.u.Exception.ExceptionRecord;
        DWORD code = exception.ExceptionCode;
        if (is_break(code) && !t->initial_break) {
            t->initial_break = true;
            if (!stopping) {
                if (!arm_initial(t)) { t->stop.store(true); signal_startup(t, false); }
                else { t->running.store(true); signal_startup(t, true); }
            }
            return DBG_CONTINUE;
        }
        if (code == GuardException && exception.NumberParameters >= 2 && owned_page(t, exception.ExceptionInformation[1])) {
            if (!stopping) handle_guard(t, debug);
            return DBG_CONTINUE;
        }
        auto found = t->threads.find(debug.dwThreadId);
        if (is_step(code) && found != t->threads.end() && (found->second.trap_owned || found->second.stale_trap)) {
            const bool original_trap = found->second.original_trap;
            if (found->second.trap_owned && !restore_trap(t, found->second)) t->stop.store(true);
            found->second.stale_trap = false;
            t->stepping = 0;
            if (!stopping && !t->stop.load() && !protect_pages(t, true)) t->stop.store(true);
            if (!stopping) resume_peers(t);
            return original_trap ? DBG_EXCEPTION_NOT_HANDLED : DBG_CONTINUE;
        }
        // Do not swallow the target's own breakpoints, SEH, guard-stack faults, or unrelated single steps.
        return DBG_EXCEPTION_NOT_HANDLED;
    }
    default: break;
    }
    return DBG_CONTINUE;
}

bool suspend_all_known_and_new(Trace* t) {
    bool ok = true;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot != INVALID_HANDLE_VALUE) {
        THREADENTRY32 info{};
        info.dwSize = sizeof(info);
        if (Thread32First(snapshot, &info)) do {
            if (info.th32OwnerProcessID == t->pid) {
                Thread* thread = add_thread(t, info.th32ThreadID);
                if (!thread || !suspend(t, *thread)) ok = false;
            }
        } while (Thread32Next(snapshot, &info));
        CloseHandle(snapshot);
    } else { os_error(t, "CreateToolhelp32Snapshot during detach"); ok = false; }
    return suspend_peers(t, 0) && ok;
}

bool continue_pending(Trace* t) {
    if (!t->has_pending) return true;
    if (!ContinueDebugEvent(t->pending.dwProcessId, t->pending.dwThreadId, t->pending_status)) {
        os_error(t, "ContinueDebugEvent"); return false;
    }
    t->has_pending = false;
    if (t->process_exited) t->attached.store(false);
    return true;
}
bool dispatch_event(Trace* t, const DEBUG_EVENT& debug, bool stopping) {
    t->pending = debug; t->has_pending = true; t->pending_status = DBG_CONTINUE;
    t->pending_status = handle_event(t, debug, stopping);
    return continue_pending(t);
}
bool detach_process(Trace* t) {
#ifdef MS_TRACE_TEST_DETACH_FAILURES
    // Fault injection is compiled only into the isolated cleanup test DLL.
    static thread_local unsigned remaining = MS_TRACE_TEST_DETACH_FAILURES;
    if (remaining) { --remaining; SetLastError(ERROR_BUSY); return false; }
#endif
    return DebugActiveProcessStop(t->pid) != FALSE;
}

bool cleanup(Trace* t) {
    if (!t->attached.load() || !alive(t)) { t->attached.store(false); return resume_peers(t); }
    t->cleanup_safe = false;
    // Own one explicit suspension on every live thread. This closes the detach race without
    // injecting a breakpoint thread and preserves any pre-existing suspension counts.
    bool restored = suspend_all_known_and_new(t);
    // A single-step may already be queued. Keep ownership until it is drained even
    // though its context TF has just been restored; passing it to the target would
    // introduce an exception the target did not request.
    for (auto& [id, thread] : t->threads) { (void)id; if (!restore_trap(t, thread, true)) restored = false; }
    for (auto& page : t->pages) if (page.changed) {
        MEMORY_BASIC_INFORMATION info{};
        if (!VirtualQueryEx(t->process, reinterpret_cast<LPCVOID>(page.address), &info, sizeof(info))) {
            os_error(t, "Query trace page during detach"); restored = false;
        } else if (info.State == MEM_COMMIT && reinterpret_cast<uintptr_t>(info.AllocationBase) == page.allocation) {
            DWORD previous = 0;
            DWORD protection = (info.Protect & ~PAGE_GUARD) == page.protection ? page.protection : info.Protect & ~PAGE_GUARD;
            if (!VirtualProtectEx(t->process, reinterpret_cast<LPVOID>(page.address), t->page_size, protection, &previous)) {
                os_error(t, "Restore trace page protection"); restored = false;
            } else page.changed = false;
        } else page.changed = false; // Unmapped/replaced memory no longer contains our guard.
    }
    if (restored) t->stepping = 0;
    // Drain already queued owned exceptions while the target cannot execute; pass unrelated exceptions normally.
    if (!continue_pending(t)) restored = false;
    DEBUG_EVENT pending{};
    for (unsigned index = 0; index < 4096 && !t->has_pending && WaitForDebugEvent(&pending, 0); ++index) {
        if (!dispatch_event(t, pending, true)) { restored = false; break; }
        if (t->process_exited) break;
    }
    if (t->process_exited) { if (!t->has_pending) t->attached.store(false); resume_peers(t); return !t->attached.load(); }
    // Leave no owned suspension after successful detach. If resuming fails, retain
    // the worker and its handles so the next Stop can retry rather than orphaning it.
    if (restored && resume_peers(t)) t->cleanup_safe = true;
    if (t->cleanup_safe) {
        if (!detach_process(t)) os_error(t, "DebugActiveProcessStop");
        else t->attached.store(false);
    }
    return !t->attached.load();
}

void worker_main(Trace* t) {
    try {
        if (!DebugActiveProcess(t->pid)) {
            os_error(t, "DebugActiveProcess (target may already have a debugger or require elevated access)");
            release_trace(t); signal_startup(t, false); complete_stop_attempt(t, true); return;
        }
        t->attached.store(true);
        if (!DebugSetProcessKillOnExit(FALSE)) { os_error(t, "DebugSetProcessKillOnExit(FALSE)"); t->stop.store(true); }
#ifdef MS_TRACE_TEST_START_FAILURE
        plain_error(t, MS_OS, "Injected startup failure for cleanup lifecycle verification."); t->stop.store(true);
#endif
        uint64_t deadline = GetTickCount64() + 15000;
        while (!t->stop.load()) {
            DEBUG_EVENT active{};
            if (!WaitForDebugEvent(&active, 100)) {
                DWORD error = GetLastError();
                if (error != ERROR_SEM_TIMEOUT) { os_error(t, "WaitForDebugEvent", error); break; }
                if (!alive(t)) { plain_error(t, MS_OS, "The target process exited; tracing stopped."); break; }
                if ((!t->initial_break && GetTickCount64() > deadline) || (t->stepping && GetTickCount64() - t->step_tick > 3000)) {
                    plain_error(t, MS_OS, "Timed out waiting for the debugger attach or instruction single-step; tracing stopped."); break;
                }
                // A kernel/API or external memory access can consume PAGE_GUARD without a user-mode debug event.
                // Detect this instead of silently reporting incomplete continuous tracing.
                if (t->initial_break && !t->stepping) for (const auto& page : t->pages) {
                    MEMORY_BASIC_INFORMATION info{};
                    if (!VirtualQueryEx(t->process, reinterpret_cast<LPCVOID>(page.address), &info, sizeof(info)) ||
                        info.State != MEM_COMMIT || !(info.Protect & PAGE_GUARD)) {
                        plain_error(t, MS_OS, "The guard was consumed by a kernel/API/external access or the page changed; tracing stopped. Pause memory reads and writes while tracing.");
                        t->stop.store(true); break;
                    }
                }
                continue;
            }
            if (!dispatch_event(t, active, false)) break;
        }
    } catch (const std::bad_alloc&) { plain_error(t, MS_OS, "Not enough memory for debugging; tracing stopped."); }
    catch (...) { plain_error(t, MS_OS, "Unexpected native debugger error; tracing stopped."); }
    t->stop.store(true);
    t->running.store(false);
    signal_startup(t, false);
    try { cleanup(t); }
    catch (...) { plain_error(t, MS_OS, "Debugger cleanup failed; Stop can retry on the original debugger thread."); }
    complete_stop_attempt(t);
    // Win32 debugger operations stay on the attaching thread. A failed cleanup
    // must keep that thread and the session alive, rather than joining it and
    // making a subsequent Stop unable to detach. Continue events while waiting
    // for a retry so a safely restored target can continue to run.
    while (t->attached.load()) {
        bool retry = false;
        {
            std::lock_guard<std::mutex> lock(t->gate);
            retry = t->stop_requested > t->stop_completed;
        }
        if (retry) {
            try { cleanup(t); }
            catch (...) { plain_error(t, MS_OS, "Debugger cleanup failed; Stop can retry on the original debugger thread."); }
            complete_stop_attempt(t);
            continue;
        }
        try {
            if (t->has_pending) {
                if (!continue_pending(t)) Sleep(100);
                continue;
            }
            DEBUG_EVENT pending{};
            if (WaitForDebugEvent(&pending, 100)) dispatch_event(t, pending, true);
            else if (!alive(t)) t->attached.store(false);
        } catch (...) { plain_error(t, MS_OS, "Debugger event cleanup failed; Stop can retry."); }
    }
    resume_peers(t);
    release_trace(t);
    complete_stop_attempt(t, true);
}

bool prepare(Trace* t) {
    t->process = OpenProcess(PROCESS_ALL_ACCESS, FALSE, t->pid);
    if (!t->process) { os_error(t, "OpenProcess for tracing"); return false; }
    if (!alive(t)) { plain_error(t, MS_OS, "The selected target process has already exited."); return false; }
    BOOL wow = FALSE;
    if (!IsWow64Process(t->process, &wow)) { os_error(t, "IsWow64Process"); return false; }
    t->bitness = wow ? 32 : 64;
    SYSTEM_INFO system{};
    GetNativeSystemInfo(&system);
    if (system.wProcessorArchitecture != PROCESSOR_ARCHITECTURE_AMD64) { plain_error(t, MS_INVALID, "Instruction access tracing currently requires an x64 Windows host."); return false; }
    t->page_size = system.dwPageSize;
    uint64_t end = t->address + t->size;
    if (t->bitness == 32 && end > 0x100000000ULL) { plain_error(t, MS_INVALID, "The watched range is outside the 32-bit address space."); return false; }
    for (uint64_t address = t->address - t->address % t->page_size; address < end; address += t->page_size) {
        MEMORY_BASIC_INFORMATION info{};
        if (!VirtualQueryEx(t->process, reinterpret_cast<LPCVOID>(address), &info, sizeof(info))) { os_error(t, "VirtualQueryEx watched range"); return false; }
        if (info.State != MEM_COMMIT || (info.Protect & (PAGE_GUARD | PAGE_NOACCESS)) || !(info.Protect & 0xff)) {
            plain_error(t, MS_INVALID, "The watched range must be committed and cannot already use PAGE_GUARD or PAGE_NOACCESS."); return false;
        }
        t->pages.push_back({address, reinterpret_cast<uintptr_t>(info.AllocationBase), info.Protect});
    }
    return true;
}
} // namespace

std::mutex& ms_trace_registry_mutex() noexcept { return registry_gate; }
bool ms_trace_overlaps_locked(uint32_t pid, uint64_t start, uint64_t end) noexcept {
    for (const auto* trace : registry) if (trace->pid == pid)
        for (const auto& page : trace->pages) if (start < page.address + trace->page_size && end > page.address) return true;
    return false;
}
uint64_t ms_trace_read_end_locked(uint32_t pid, uint64_t start, uint64_t end, uint64_t& skip_end) noexcept {
    skip_end = start;
    uint64_t result = end;
    for (const auto* trace : registry) if (trace->pid == pid) for (const auto& page : trace->pages) {
        const uint64_t page_end = page.address + trace->page_size;
        if (start >= page.address && start < page_end) { skip_end = std::min(end, page_end); return start; }
        if (page.address > start) result = std::min(result, page.address);
    }
    return result;
}

extern "C" {
MS_TRACE_API void* ms_trace_start(uint32_t pid, uint64_t address, uint32_t size, uint32_t mode) {
    Trace* t = nullptr;
    try {
        startup_error[0] = '\0';
        if (!pid || pid == GetCurrentProcessId() || !address || size == 0 || size > 4096 ||
            address > UINT64_MAX - size || mode > MS_TRACE_WRITE) {
            std::snprintf(startup_error, sizeof(startup_error), "Choose a non-self process and a valid explicit 1..4096-byte address range and trace mode."); return nullptr;
        }
        if (has_trace(pid)) {
            std::snprintf(startup_error, sizeof(startup_error), "This target already has an active Memory Studio access trace. Stop it before starting another.");
            return nullptr;
        }
        t = new Trace;
        t->pid = pid; t->address = address; t->size = size; t->mode = mode;
        if (!prepare(t)) { std::snprintf(startup_error, sizeof(startup_error), "%s", t->error); delete t; return nullptr; }
        if (!reserve_trace(t)) {
            std::snprintf(startup_error, sizeof(startup_error), "This target already has an active Memory Studio access trace. Stop it before starting another.");
            delete t; return nullptr;
        }
        t->worker = std::thread(worker_main, t);
        bool ok = false;
        {
            std::unique_lock<std::mutex> lock(t->gate);
            bool complete = t->startup.wait_for(lock, std::chrono::seconds(16), [&] { return t->startup_done; });
            ok = complete && t->startup_ok;
        }
        if (ok) return t;
        ms_trace_stop(t);
        // Recovery handle: ownership must not disappear if startup failed after
        // attaching and its cleanup could not detach. The caller checks state
        // and retries Stop while retaining this non-running session.
        if (t->attached.load()) return t;
        std::snprintf(startup_error, sizeof(startup_error), "%s", t->error[0] ? t->error : "Timed out attaching the debugger.");
        delete t; return nullptr;
    } catch (...) {
        if (t) {
            plain_error(t, MS_OS, "Unexpected native error while starting instruction access tracing.");
            ms_trace_stop(t);
            if (t->attached.load()) return t;
            if (t->worker.joinable()) t->worker.join();
            delete t;
        }
        std::snprintf(startup_error, sizeof(startup_error), "Unexpected native error while starting instruction access tracing."); return nullptr;
    }
}
MS_TRACE_API uint32_t ms_trace_poll(void* session, ms_trace_event* events, uint32_t capacity) {
    try {
        auto* t = static_cast<Trace*>(session);
        if (!t || (!events && capacity)) return 0;
        std::lock_guard<std::mutex> lock(t->gate);
        uint32_t count = static_cast<uint32_t>(std::min<size_t>(capacity, t->count));
        for (uint32_t index = 0; index < count; ++index) events[index] = t->queue[(t->head + index) % QueueCapacity];
        t->head = (t->head + count) % QueueCapacity; t->count -= count;
        return count;
    } catch (...) { return 0; }
}
MS_TRACE_API void ms_trace_get_state(void* session, ms_trace_state* state) {
    try {
        if (!state) return;
        *state = {};
        auto* t = static_cast<Trace*>(session);
        if (!t) return;
        state->running = t->running.load(); state->attached = t->attached.load();
        state->bitness = t->bitness; state->mode = t->mode;
        state->total_events = t->total.load(); state->dropped_events = t->dropped.load();
        state->watch_address = t->address; state->watch_size = t->size; state->status = t->status.load();
    } catch (...) { if (state) *state = {}; }
}
MS_TRACE_API int32_t ms_trace_stop(void* session) {
    try {
        auto* t = static_cast<Trace*>(session);
        if (!t) return MS_INVALID;
        t->stop.store(true);
        if (t->worker.joinable()) {
            {
                std::unique_lock<std::mutex> lock(t->gate);
                const uint64_t request = ++t->stop_requested;
                t->stop_completed_cv.wait(lock, [&] { return t->worker_finished.load() || t->stop_completed >= request; });
            }
            if (!t->attached.load() || t->worker_finished.load()) t->worker.join();
        }
        return static_cast<int32_t>(t->status.load());
    } catch (...) { return MS_OS; }
}
MS_TRACE_API void ms_trace_close(void* session) {
    try {
        if (!session) return;
        auto* t = static_cast<Trace*>(session);
        ms_trace_stop(t);
        if (t->attached.load() || (t->worker.joinable() && !t->worker_finished.load())) return;
        if (t->worker.joinable()) t->worker.join();
        delete t;
    }
    catch (...) { }
}
MS_TRACE_API uint32_t ms_trace_error(void* session, char* buffer, uint32_t capacity) {
    try {
        auto* t = static_cast<Trace*>(session);
        std::unique_lock<std::mutex> lock;
        if (t) lock = std::unique_lock<std::mutex>(t->gate);
        const char* message = t ? t->error : startup_error;
        uint32_t length = static_cast<uint32_t>(std::strlen(message));
        if (buffer && capacity) { uint32_t count = std::min(length, capacity - 1); std::memcpy(buffer, message, count); buffer[count] = '\0'; }
        return length;
    } catch (...) { if (buffer && capacity) buffer[0] = '\0'; return 0; }
}
}
