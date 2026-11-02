// Compile-only regression checks; use the firmware's Xtensa C++ compiler with
// -std=c++11 -fsyntax-only -I firmware/include tools/tests/quota_windows.cpp.
#include "quota_windows.h"

// null primary_pct / null primary_window_min in a weekly-only bridge payload
// become -1 / 300 in the ESP8266 parser. This was the phantom 5h-reset case.
static_assert(!hasShortQuotaWindow(-1, 300), "No primary quota means no 5h label");
static_assert(!hasShortQuotaWindow(-1, 10080), "Unknown weekly quota is not 5h");
static_assert(!hasShortQuotaWindow(24, 10080), "Legacy primary weekly window is not 5h");
static_assert(!hasShortQuotaWindow(24, 0), "Invalid duration is not a short window");
static_assert(!hasShortQuotaWindow(24, -1), "Missing duration is not a short window");
static_assert(!hasShortQuotaWindow(24, 1440), "Day-long boundary is not a short window");
static_assert(hasShortQuotaWindow(0, 300), "Unused 5h quota must remain visible");
static_assert(hasShortQuotaWindow(24, 300), "Active 5h quota must remain visible");
static_assert(hasShortQuotaWindow(100, 300), "Exhausted 5h quota must retain its reset");
