#pragma once

// ---- Firmware version (shown on the first-time WiFi setup screen & /api/info) ----
#define FW_VERSION "0.4.7-sonic"

// ---- Bridge polling ----
#define BRIDGE_DEFAULT_PORT 8765
#define BRIDGE_DEFAULT_PATH "/status"
#define BRIDGE_POLL_INTERVAL_MS 5000
#define BRIDGE_HTTP_TIMEOUT_MS 3000

// ---- WiFiManager ----
#define WIFI_PORTAL_AP_NAME "AI-Clock-Setup"
#define WIFI_RETRY_DEFAULT_SECONDS 300UL
#define WIFI_RETRY_MIN_SECONDS 30UL
#define WIFI_RETRY_MAX_SECONDS 86400UL
#define WIFI_PORTAL_RETRY_WINDOW_MS 15000UL
#define WIFI_RETRY_CONFIG_FILE "/wifi_retry.txt"
#define WIFI_RETRY_CONFIG_TMP_FILE "/wifi_retry.tmp"
#define WIFI_CONFIG_FILE "/bridge_host.txt"
#define DISPLAY_MODE_FILE "/display_mode.txt"
#define RESET_TIME_FORMAT_FILE "/reset_time_format.txt"
#define USB_WIFI_OFF_FILE "/usb_wifi_off.txt"

// ---- Backlight ----
#define BRIGHTNESS_FILE "/brightness.txt"
#define BRIGHTNESS_DEFAULT 100
#define BRIGHTNESS_PWM_FREQ 2000 // Hz; high enough to avoid visible flicker when dim

// ---- Display layout (240x240 ST7789) ----
#define SCREEN_W 240
#define SCREEN_H 240
