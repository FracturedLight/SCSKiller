// scskiller_items.bin in the warm's staging folder: the process's D3D12 item states in a mapped file, so they outlive a
// process that dies without a last line (a fault outside our handlers, a fail-fast). The parent (warm.cpp) then plans the
// retry the process couldn't print.
#pragma once
#include <cstdint>
#include <cstring>

#ifdef _WIN32
// Wine on a macOS host (CrossOver): Windows' ntdll never exports it, and Proton's host is Linux.
inline bool wine_on_mac() {
    auto host = (void(__cdecl*)(const char**, const char**))GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "wine_get_host_version");
    const char* sysname = nullptr;
    const char* release = nullptr;
    if (host) host(&sysname, &release);
    return sysname && !strcmp(sysname, "Darwin");
}
#endif

constexpr wchar_t kItemsFile[] = L"scskiller_items.bin";
constexpr uint64_t kItemsMagic = 0x31534D4554494B53;  // "SKITEMS1"
struct ItemsHeader {
    uint64_t magic, pid, first, n12, total;  // pid: the child's, so no earlier child's file is taken; the states of items [0, n12) follow, one byte each
};
// 0 not taken, 1 created, 2 failed, 3 unfinished (left for the retry), 4 not created (in flight at a removal or fault),
// 5 skipped, 6 in flight
constexpr uint8_t kItemInFlight = 6;
inline bool item_finished(uint8_t s) { return s == 1 || s == 2 || s == 5; }
