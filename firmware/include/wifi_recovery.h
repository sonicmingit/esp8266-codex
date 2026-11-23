#pragma once

#include <stdint.h>

enum class WifiRecoveryEvent {
  None,
  Retrying,
  PortalAttemptTimedOut,
  PortalRetryPaused,
  PortalClosed
};

// WiFiManager disables STA when it opens the setup AP. Retry in bounded,
// non-blocking windows so the portal can still be used between attempts.
class WifiRecovery {
 public:
  WifiRecovery(uint32_t retryIntervalMs, uint32_t portalAttemptMs)
      : retryIntervalMs_(retryIntervalMs), portalAttemptMs_(portalAttemptMs) {}

  void begin(uint32_t now) {
    lastRetryMs_ = now;
    portalAttemptActive_ = false;
  }

  void setRetryInterval(uint32_t intervalMs, uint32_t now) {
    retryIntervalMs_ = intervalMs;
    lastRetryMs_ = now;
  }

  template <typename Wifi, typename Manager>
  WifiRecoveryEvent update(uint32_t now, bool wifiDisabled, bool connected,
                           bool portalHasClients, bool stationEnabled,
                           Wifi &wifi, Manager &manager) {
    if (wifiDisabled) return WifiRecoveryEvent::None;

    const bool portalActive = manager.getConfigPortalActive();
    if (portalActive && portalHasClients) {
      // Protect the setup page, including when a phone joins during a retry.
      // Resume the full interval after the last client leaves the hotspot.
      lastRetryMs_ = now;
      portalAttemptActive_ = false;
      if (stationEnabled) {
        wifi.disconnect(false, false);
        wifi.enableSTA(false);
        return WifiRecoveryEvent::PortalRetryPaused;
      }
      return WifiRecoveryEvent::None;
    }
    if (connected) {
      lastRetryMs_ = now;
      portalAttemptActive_ = false;
      if (portalActive) {
        // A background connection does not make WiFiManager close its portal.
        // Release port 80 before the device's admin server starts.
        manager.stopConfigPortal();
        return WifiRecoveryEvent::PortalClosed;
      }
      return WifiRecoveryEvent::None;
    }

    if (!portalActive) portalAttemptActive_ = false;
    if (portalAttemptActive_ && now - portalAttemptStartMs_ >= portalAttemptMs_) {
      // Keep the saved network and restore AP-only mode for a stable portal.
      wifi.disconnect(false, false);
      wifi.enableSTA(false);
      portalAttemptActive_ = false;
      return WifiRecoveryEvent::PortalAttemptTimedOut;
    }

    if (now - lastRetryMs_ < retryIntervalMs_) return WifiRecoveryEvent::None;
    lastRetryMs_ = now;
    if (!manager.getWiFiIsSaved()) return WifiRecoveryEvent::None;

    if (portalActive) {
      // begin() enables STA alongside the setup AP and uses the saved config.
      wifi.begin();
      portalAttemptActive_ = true;
      portalAttemptStartMs_ = now;
    } else {
      // The STA is already enabled when recovering an established connection.
      wifi.reconnect();
    }
    return WifiRecoveryEvent::Retrying;
  }

 private:
  uint32_t retryIntervalMs_;
  uint32_t portalAttemptMs_;
  uint32_t lastRetryMs_ = 0;
  uint32_t portalAttemptStartMs_ = 0;
  bool portalAttemptActive_ = false;
};
