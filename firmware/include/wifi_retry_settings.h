#pragma once

#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "config.h"

// Accept only a whole number in range, without overflow or partial parsing.
inline bool parseWifiRetrySeconds(const char *text, uint32_t &seconds) {
  if (!text || !*text) return false;
  uint32_t value = 0;
  for (const char *p = text; *p; ++p) {
    if (*p < '0' || *p > '9') return false;
    uint32_t digit = static_cast<uint32_t>(*p - '0');
    if (value > (WIFI_RETRY_MAX_SECONDS - digit) / 10U) return false;
    value = value * 10U + digit;
  }
  if (value < WIFI_RETRY_MIN_SECONDS || value > WIFI_RETRY_MAX_SECONDS) return false;
  seconds = value;
  return true;
}

template <typename FileSystem>
bool readWifiRetrySeconds(FileSystem &fs, uint32_t &seconds) {
  auto file = fs.open(WIFI_RETRY_CONFIG_FILE, "r");
  if (!file) return false;
  char value[12];
  size_t length = 0;
  int c;
  while ((c = file.read()) >= 0 && c != '\n') {
    if (length == sizeof(value) - 1) {
      file.close();
      return false;
    }
    value[length++] = static_cast<char>(c);
  }
  file.close();
  if (length && value[length - 1] == '\r') --length;
  value[length] = '\0';
  return parseWifiRetrySeconds(value, seconds);
}

template <typename FileSystem>
bool writeWifiRetrySeconds(FileSystem &fs, uint32_t seconds) {
  if (seconds < WIFI_RETRY_MIN_SECONDS || seconds > WIFI_RETRY_MAX_SECONDS) return false;
  auto file = fs.open(WIFI_RETRY_CONFIG_TMP_FILE, "w");
  if (!file) return false;
  char value[12];
  snprintf(value, sizeof(value), "%lu", static_cast<unsigned long>(seconds));
  bool written = file.print(value) == strlen(value);
  file.close();
  if (!written || !fs.rename(WIFI_RETRY_CONFIG_TMP_FILE, WIFI_RETRY_CONFIG_FILE)) {
    fs.remove(WIFI_RETRY_CONFIG_TMP_FILE);
    return false;
  }
  return true;
}
