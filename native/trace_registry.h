#pragma once
#include <cstdint>
#include <mutex>

// Internal only. A memory operation holds this lock from range validation through
// Read/WriteProcessMemory. Trace registration happens before PAGE_GUARD is armed,
// preventing a modeless scanner/editor from consuming it, including TF rearm gaps.
std::mutex& ms_trace_registry_mutex() noexcept;
bool ms_trace_overlaps_locked(uint32_t pid, uint64_t start, uint64_t end) noexcept;
// Clips a read block before the next watched page. If start is watched, returns
// start and sets skip_end to the end of that watched page (capped by end).
uint64_t ms_trace_read_end_locked(uint32_t pid, uint64_t start, uint64_t end, uint64_t& skip_end) noexcept;
