#pragma once

// An absent primary window decodes to pct=-1 and may keep the legacy 300-minute
// default. Its duration alone must not create a phantom 5-hour quota/reset label.
constexpr bool hasShortQuotaWindow(float primaryPct, int primaryWindowMin) {
  return primaryPct >= 0 && primaryWindowMin > 0 && primaryWindowMin < 1440;
}
