// Host test: compile with a C++11 compiler and -I firmware/include.
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <map>
#include <string>

#include "wifi_recovery.h"
#include "wifi_retry_settings.h"

struct FakeWifi {
  bool staEnabled = false;
  bool credentialsErased = false;
  int begins = 0;
  int reconnects = 0;
  int disconnects = 0;

  void begin() { staEnabled = true; ++begins; }
  void reconnect() { assert(staEnabled); ++reconnects; }
  void disconnect(bool wifiOff, bool eraseCredentials) {
    assert(!wifiOff);
    credentialsErased |= eraseCredentials;
    ++disconnects;
  }
  void enableSTA(bool enabled) { staEnabled = enabled; }
};

struct FakeManager {
  bool portal = true;
  bool saved = true;
  int portalStops = 0;

  bool getConfigPortalActive() { return portal; }
  bool getWiFiIsSaved() { return saved; }
  void stopConfigPortal() { portal = false; ++portalStops; }
};

struct FakeFileSystem;
struct FakeFile {
  FakeFileSystem *fs;
  std::string path;
  size_t position = 0;
  bool valid = false;

  explicit operator bool() const { return valid; }
  int read();
  size_t print(const char *value);
  void close() {}
};

struct FakeFileSystem {
  std::map<std::string, std::string> files;
  bool failWrite = false;
  bool failRename = false;

  FakeFile open(const char *path, const char *mode) {
    FakeFile file;
    file.fs = this;
    file.path = path;
    if (mode[0] == 'w') {
      files[path] = "";
      file.valid = true;
    } else {
      file.valid = files.count(path) != 0;
    }
    return file;
  }
  bool rename(const char *from, const char *to) {
    if (failRename || !files.count(from)) return false;
    files[to] = files[from];
    files.erase(from);
    return true;
  }
  void remove(const char *path) { files.erase(path); }
};

int FakeFile::read() {
  const std::string &contents = fs->files[path];
  return position < contents.size() ? static_cast<unsigned char>(contents[position++]) : -1;
}

size_t FakeFile::print(const char *value) {
  if (fs->failWrite) return 0;
  fs->files[path] = value;
  return fs->files[path].size();
}

static WifiRecoveryEvent tick(WifiRecovery &recovery, uint32_t now, bool disabled,
                              bool connected, FakeWifi &wifi, FakeManager &manager,
                              bool portalHasClients = false) {
  return recovery.update(now, disabled, connected, portalHasClients, wifi.staEnabled, wifi, manager);
}

static void testLateRouterAndDroppedConnection() {
  FakeWifi wifi;
  FakeManager manager;
  WifiRecovery recovery(30000, 15000);
  recovery.begin(1000);

  // The setup AP continues running while a late router is unavailable.
  assert(tick(recovery, 30999, false, false, wifi, manager) == WifiRecoveryEvent::None);
  assert(tick(recovery, 31000, false, false, wifi, manager) == WifiRecoveryEvent::Retrying);
  assert(wifi.begins == 1 && wifi.staEnabled && manager.portal);
  assert(tick(recovery, 45999, false, false, wifi, manager) == WifiRecoveryEvent::None);
  assert(tick(recovery, 46000, false, false, wifi, manager) == WifiRecoveryEvent::PortalAttemptTimedOut);
  assert(!wifi.staEnabled && !wifi.credentialsErased && manager.portal);

  // A later attempt succeeds; stop the portal before binding the admin server.
  assert(tick(recovery, 60999, false, false, wifi, manager) == WifiRecoveryEvent::None);
  assert(tick(recovery, 61000, false, false, wifi, manager) == WifiRecoveryEvent::Retrying);
  assert(tick(recovery, 65000, false, true, wifi, manager) == WifiRecoveryEvent::PortalClosed);
  assert(wifi.begins == 2 && manager.portalStops == 1 && !manager.portal);
  assert(tick(recovery, 94999, false, false, wifi, manager) == WifiRecoveryEvent::None);

  // A subsequent outage recovers in STA mode without starting another portal.
  assert(tick(recovery, 95000, false, false, wifi, manager) == WifiRecoveryEvent::Retrying);
  assert(tick(recovery, 125000, false, false, wifi, manager) == WifiRecoveryEvent::Retrying);
  assert(wifi.reconnects == 2 && wifi.disconnects == 1 && !manager.portal);
}

static void testFirstSetupAndWiredMode() {
  FakeWifi wifi;
  FakeManager manager;
  manager.saved = false;
  WifiRecovery recovery(30000, 15000);
  recovery.begin(0);
  assert(tick(recovery, 30000, false, false, wifi, manager) == WifiRecoveryEvent::None);
  assert(tick(recovery, 60000, false, false, wifi, manager) == WifiRecoveryEvent::None);
  assert(wifi.begins == 0 && manager.portal);

  // The portal saved a network, but the user has explicitly disabled Wi-Fi
  // after the USB handshake. No background recovery may re-enable it.
  manager.saved = true;
  assert(tick(recovery, 90000, true, false, wifi, manager) == WifiRecoveryEvent::None);
  assert(tick(recovery, 120000, true, true, wifi, manager) == WifiRecoveryEvent::None);
  assert(wifi.begins == 0 && wifi.reconnects == 0 && manager.portalStops == 0);
}

static void testManualSetupAndClockWraparound() {
  FakeWifi wifi;
  FakeManager manager;
  WifiRecovery recovery(30000, 15000);
  const uint32_t start = UINT32_MAX - 10000;
  recovery.begin(start);
  assert(tick(recovery, start + 29999U, false, false, wifi, manager) == WifiRecoveryEvent::None);
  assert(tick(recovery, start + 30000U, false, false, wifi, manager) == WifiRecoveryEvent::Retrying);

  // WiFiManager may have already closed the portal after manual setup.
  manager.portal = false;
  assert(tick(recovery, start + 31000U, false, true, wifi, manager, true) == WifiRecoveryEvent::None);
  assert(manager.portalStops == 0 && wifi.disconnects == 0);
}

static void testPortalClientPausesRetries() {
  FakeWifi wifi;
  FakeManager manager;
  WifiRecovery recovery(WIFI_RETRY_DEFAULT_SECONDS * 1000UL, 15000);
  recovery.begin(0);
  assert(WIFI_RETRY_DEFAULT_SECONDS == 300);
  assert(tick(recovery, 299999, false, false, wifi, manager) == WifiRecoveryEvent::None);

  // A phone can stay on the setup AP well beyond the retry deadline.
  assert(tick(recovery, 300000, false, false, wifi, manager, true) == WifiRecoveryEvent::None);
  assert(tick(recovery, 900000, false, false, wifi, manager, true) == WifiRecoveryEvent::None);
  assert(wifi.begins == 0 && manager.portal);
  assert(tick(recovery, 1199999, false, false, wifi, manager) == WifiRecoveryEvent::None);
  assert(tick(recovery, 1200000, false, false, wifi, manager) == WifiRecoveryEvent::Retrying);

  // If a phone arrives during an attempt, even a just-connected station is
  // stopped without clearing credentials or closing the setup portal.
  assert(tick(recovery, 1201000, false, true, wifi, manager, true) == WifiRecoveryEvent::PortalRetryPaused);
  assert(!wifi.staEnabled && !wifi.credentialsErased && manager.portal && manager.portalStops == 0);
  assert(tick(recovery, 1500999, false, false, wifi, manager) == WifiRecoveryEvent::None);
  assert(tick(recovery, 1501000, false, false, wifi, manager) == WifiRecoveryEvent::Retrying);
}

static void testConfigurableIntervalAndValidation() {
  uint32_t seconds = 999;
  assert(parseWifiRetrySeconds("30", seconds) && seconds == 30);
  assert(parseWifiRetrySeconds("300", seconds) && seconds == 300);
  assert(parseWifiRetrySeconds("86400", seconds) && seconds == 86400);
  assert(!parseWifiRetrySeconds("29", seconds) && seconds == 86400);
  assert(!parseWifiRetrySeconds("86401", seconds));
  assert(!parseWifiRetrySeconds("", seconds));
  assert(!parseWifiRetrySeconds(nullptr, seconds));
  assert(!parseWifiRetrySeconds("-30", seconds));
  assert(!parseWifiRetrySeconds("30.5", seconds));
  assert(!parseWifiRetrySeconds("300s", seconds));
  assert(!parseWifiRetrySeconds("42949672960", seconds));

  FakeWifi wifi;
  FakeManager manager;
  WifiRecovery recovery(300000, 15000);
  recovery.begin(0);
  recovery.setRetryInterval(60000, 20000);
  assert(tick(recovery, 79999, false, false, wifi, manager) == WifiRecoveryEvent::None);
  assert(tick(recovery, 80000, false, false, wifi, manager) == WifiRecoveryEvent::Retrying);
}

static void testSettingsPersistenceAndFailure() {
  FakeFileSystem fs;
  uint32_t seconds = WIFI_RETRY_DEFAULT_SECONDS;
  assert(!readWifiRetrySeconds(fs, seconds) && seconds == 300);
  assert(writeWifiRetrySeconds(fs, 600));
  seconds = WIFI_RETRY_DEFAULT_SECONDS; // simulate a fresh boot
  assert(readWifiRetrySeconds(fs, seconds) && seconds == 600);
  assert(writeWifiRetrySeconds(fs, 30));
  assert(readWifiRetrySeconds(fs, seconds) && seconds == 30);

  // A failed save must not replace the previous persisted value.
  fs.failWrite = true;
  assert(!writeWifiRetrySeconds(fs, 120));
  fs.failWrite = false;
  assert(readWifiRetrySeconds(fs, seconds) && seconds == 30);
  fs.failRename = true;
  assert(!writeWifiRetrySeconds(fs, 120));
  fs.failRename = false;
  assert(readWifiRetrySeconds(fs, seconds) && seconds == 30);
  assert(!writeWifiRetrySeconds(fs, 1));
  assert(readWifiRetrySeconds(fs, seconds) && seconds == 30);

  fs.files[WIFI_RETRY_CONFIG_FILE] = "86401";
  seconds = WIFI_RETRY_DEFAULT_SECONDS;
  assert(!readWifiRetrySeconds(fs, seconds) && seconds == 300);
  fs.files[WIFI_RETRY_CONFIG_FILE] = "600\r\n";
  assert(readWifiRetrySeconds(fs, seconds) && seconds == 600);
}

int main() {
  testLateRouterAndDroppedConnection();
  testFirstSetupAndWiredMode();
  testManualSetupAndClockWraparound();
  testPortalClientPausesRetries();
  testConfigurableIntervalAndValidation();
  testSettingsPersistenceAndFailure();
  puts("Wi-Fi recovery tests passed");
}
