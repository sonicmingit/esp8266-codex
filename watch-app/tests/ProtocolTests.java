package cn.sonic.aiclockwatch;

import com.sun.net.httpserver.HttpServer;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.*;

public final class ProtocolTests {
    static int passed;
    static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message); passed++;
    }
    static QuotaSnapshot parse(String providers) throws Exception { return QuotaSnapshot.parse("{" + providers + "}", 1000); }
    public static void main(String[] args) throws Exception {
        QuotaSnapshot s = parse("\"codex\":{\"weekly_pct\":24,\"primary_pct\":null,\"primary_window_min\":null}");
        check(s.codex.shortWindow == null && s.codex.weekly.used == 24, "Phantom 5h window in weekly-only account");
        s = parse("\"codex\":{\"primary_pct\":36,\"primary_window_min\":10080,\"primary_reset_min\":90}");
        check(s.codex.shortWindow == null && s.codex.weekly.used == 36, "Legacy weekly primary not promoted");
        check(s.codex.weekly.seconds(31000) == 5400, "Minute-only reset did not tick monotonically");
        s = parse("\"codex\":{\"primary_pct\":0,\"weekly_pct\":100,\"weekly_reset_min\":120}");
        check(s.codex.shortWindow != null && s.codex.shortWindow.used == 0, "Lost zero/legacy short quota");
        check(s.codex.exhausted() == s.codex.weekly, "Weekly exhausted countdown priority");
        check(s.codex.weekly.seconds(99999999) == 0 && s.codex.weekly.used == 100, "Reset fabricated a refreshed quota");
        s = parse("\"claude\":{\"five_hour_pct\":100,\"five_hour_reset_min\":12,\"seven_day_pct\":100,\"seven_day_reset_min\":120}");
        check(s.claude.exhausted() == s.claude.weekly, "Weekly must block after short-window reset");
        check(s.codex.shortWindow == null && s.codex.weekly.used < 0, "Missing provider inherited previous state");
        s = parse("\"codex\":{\"primary_pct\":-1,\"weekly_pct\":\"42\",\"weekly_reset_at\":0}");
        check(s.codex.shortWindow == null && s.codex.weekly.used < 0, "Invalid percent treated as genuine quota");
        check(s.codex.weekly.percent(false).equals("—") && s.codex.weekly.reset(s, true, 1000).equals("重置时间未知"), "Unknown became zero/date");
        for (int duration : new int[]{0, -1, 1440, 10080}) {
            check(parse("\"codex\":{\"primary_pct\":24,\"primary_window_min\":" + duration + "}").codex.shortWindow == null,
                    "Invalid/daylong window classified as 5h");
        }
        s = QuotaSnapshot.parse("{\"ts\":1798732800,\"utc_offset_min\":480,\"codex\":{\"primary_pct\":25,\"primary_reset_at\":1798732861}}", 1000);
        check(s.codex.shortWindow.seconds(2000) == 60, "Absolute reset drifted with watch wall clock");
        check(s.codex.shortWindow.reset(s, true, 2000).equals("01/01 00:01"), "Bridge timezone/year handling");
        check(s.codex.shortWindow.percent(true).equals("75%"), "Remaining-vs-used conversion");
        check(s.codex.shortWindow.reset(s, true, 999999).equals("01/01 00:01"), "Expired reset date moved");
        boolean rejected = false; try { QuotaSnapshot.parse("{\"error\":\"login required\"}", 0); } catch (org.json.JSONException e) { rejected = true; }
        check(rejected, "Unrelated API response accepted");

        WatchFace face = new WatchFace();
        face.snapshot = parse("\"claude\":{\"status\":\"working\"},\"codex\":{\"status\":\"idle\"}");
        check(face.selected(10000) == face.snapshot.claude, "Auto didn't prefer active provider");
        face.snapshot = parse("\"claude\":{\"status\":\"working\"},\"codex\":{\"needs_input\":true}");
        check(face.selected(11000) == face.snapshot.codex, "Approval did not beat working");
        face.mode = "claude";
        check(face.selected(12000) == face.snapshot.claude, "Fixed provider switched unexpectedly");
        face.mode = "auto";
        face.snapshot = parse("\"claude\":{\"status\":\"working\"},\"codex\":{\"status\":\"working\"}");
        check(face.selected(13000) == face.snapshot.claude, "Auto switched too soon after forced selection");
        check(face.selected(14000) == face.snapshot.codex, "Both-working rotation did not occur after 2 seconds");
        face.snapshot = parse("\"claude\":{\"status\":\"idle\"},\"codex\":{\"status\":\"idle\"}");
        check(face.selected(19000) == face.snapshot.codex, "Idle rotation occurred before 6 seconds");
        check(face.selected(20000) == face.snapshot.claude, "Idle rotation did not occur after 6 seconds");

        check(BridgeClient.normalize("192.168.1.20").equals("http://192.168.1.20:8765/status"), "Default bridge port");
        check(BridgeClient.normalize("192.168.1.20:8877/").equals("http://192.168.1.20:8877/status"), "Custom LAN port");
        check(BridgeClient.normalize("https://bridge.example/ai/").equals("https://bridge.example/ai/status"), "Reverse proxy prefix");
        check(BridgeClient.normalize("https://bridge.example/status").equals("https://bridge.example/status"), "Duplicate status suffix");
        for (String bad : new String[]{"", "ftp://bridge", "http://u:p@bridge", "http://bridge/status?token=x", "http://bridge:99999", "http://bridge/#x"}) {
            rejected = false; try { BridgeClient.normalize(bad); } catch (IllegalArgumentException e) { rejected = true; }
            check(rejected, "Invalid endpoint accepted: " + bad);
        }
        testTransport();
        testProfiles();
        testPets();
        System.out.println("PASS " + passed + " watch protocol, selection, endpoint and HTTP assertions");
    }
    static void testProfiles() {
        java.util.List<BridgeProfiles.Profile> saved = BridgeProfiles.parse(
                "192.168.0.118:18765\n192.168.10.20:8765\nhttps://bridge.example\n192.168.0.118:18765");
        check(saved.size() == 3, "Duplicate profiles not removed");
        java.util.List<BridgeProfiles.Profile> selected = BridgeProfiles.candidates(saved, "192.168.1.203", 23, "");
        check(selected.size() == 2 && selected.get(0).endpoint.contains("192.168.0.118"), "Actual /23 Wi-Fi network misclassified");
        check(selected.get(1).endpoint.contains("bridge.example"), "Public fallback missing");
        check(BridgeProfiles.candidates(saved, "192.168.1.203", 24, "").size() == 1, "Off-subnet private endpoint probed");
        check(BridgeProfiles.candidates(saved, "", 0, "").size() == 1, "No-Wi-Fi attempted local addresses");
        check(BridgeProfiles.candidates(saved, "192.168.10.8", 24, "").get(0).endpoint.contains("192.168.10.20"), "Roaming did not choose new LAN");
        saved = BridgeProfiles.parse("192.168.0.118:18765\n192.168.1.20:8765\nhttps://bridge.example");
        selected = BridgeProfiles.candidates(saved, "192.168.1.203", 23, "http://192.168.1.20:8765/status");
        check(selected.get(0).endpoint.contains("192.168.1.20"), "Same-subnet successful endpoint not preferred");
        check(BridgeProfiles.candidates(saved, "192.168.1.203", 23, "https://bridge.example/status").get(0).endpoint.contains("192.168."), "Public endpoint displaced LAN");
        saved = BridgeProfiles.parse("192.168.0.0/23 = https://mapped.example/ai\n192.168.0.118:8765\n10.0.0.0/8 = https://wrong.example");
        selected = BridgeProfiles.candidates(saved, "192.168.1.203", 23, "");
        check(selected.size() == 2 && selected.get(0).endpoint.contains("mapped.example"), "Explicit network mapping not preferred/isolated");
        check(BridgeProfiles.viaWifi(selected.get(0), "192.168.1.203", 23), "Mapped domain should use Wi-Fi");
        check(!BridgeProfiles.viaWifi(BridgeProfiles.parse("https://public.example").get(0), "192.168.1.203", 23), "Public fallback forced onto Wi-Fi");
        check(BridgeProfiles.format(BridgeProfiles.parse(BridgeProfiles.format(saved))).equals(BridgeProfiles.format(saved)), "Profile round-trip lost subnet rules");
        check(BridgeProfiles.display("http://192.168.0.118:18765/status").equals("192.168.0.118:18765"), "Homepage bridge address incorrect");
        for (String invalid : new String[]{"192.168.0.0/33=x", "192.168.0.0/-1=x", "999.1.1.1/24=x", "x=y=z", "not-a-cidr=host"}) {
            boolean rejected = false; try { BridgeProfiles.parse(invalid); } catch (IllegalArgumentException e) { rejected = true; }
            check(rejected, "Invalid network rule accepted: " + invalid);
        }
        check(BridgeProfiles.ipv4("192.168.1.203") >= 0 && BridgeProfiles.ipv4("1.2.3") == -1, "IPv4 parsing");
    }
    static void testPets() throws Exception {
        String sheet = "https://assets.petdex.dev/pets/cat/sprite.webp";
        String json = "{\"pets\":[{\"slug\":\"cat\",\"displayName\":\"猫咪\",\"spritesheetUrl\":\"" + sheet
                + "\"},{\"slug\":\"cat\",\"spritesheetUrl\":\"" + sheet + "\"},"
                + "{\"slug\":\"evil\",\"spritesheetUrl\":\"https://other.example/a.webp\"}]}";
        java.util.List<PetCatalog.Pet> pets = PetCatalog.parse(json);
        check(pets.size() == 1 && pets.get(0).name.equals("猫咪"), "Catalog did not validate/deduplicate entries");
        check(PetCatalog.search(pets, "CAT").size() == 1 && PetCatalog.search(pets, "猫").size() == 1, "Chinese/name/slug search failed");
        check(PetCatalog.search(pets, "dog").isEmpty(), "Search returned unrelated pet");
        for (String url : new String[]{"http://assets.petdex.dev/pets/a", "https://assets.petdex.dev.evil.test/pets/a",
                "https://u:p@assets.petdex.dev/pets/a", "https://assets.petdex.dev:444/pets/a", "file:///pets/a"})
            check(!PetCatalog.validAsset(url), "Untrusted pet URL accepted: " + url);
        boolean rejected = false; try { PetCatalog.parse("{\"pets\":[]}"); } catch (org.json.JSONException e) { rejected = true; }
        check(rejected, "Empty online catalog treated as loaded");
    }
    static void testTransport() throws Exception {
        HttpServer server = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        server.createContext("/status", exchange -> {
            boolean watch = "client=watch".equals(exchange.getRequestURI().getQuery());
            byte[] body = (watch ? "{\"codex\":{\"weekly_pct\":24}}" : "wrong client").getBytes(StandardCharsets.UTF_8);
            exchange.sendResponseHeaders(200, body.length); exchange.getResponseBody().write(body); exchange.close();
        });
        server.createContext("/large", exchange -> {
            byte[] body = new byte[65537]; exchange.sendResponseHeaders(200, body.length);
            exchange.getResponseBody().write(body); exchange.close();
        });
        server.createContext("/error", exchange -> { exchange.sendResponseHeaders(503, -1); exchange.close(); });
        server.start();
        try {
            String base = "http://127.0.0.1:" + server.getAddress().getPort();
            BridgeClient client = new BridgeClient();
            check(QuotaSnapshot.parse(client.fetch(base + "/status"), 0).codex.weekly.used == 24, "Viewer marker or live HTTP parsing failed");
            for (String path : new String[]{"/large", "/error"}) {
                boolean failed = false; try { client.fetch(base + path); } catch (java.io.IOException e) { failed = true; }
                check(failed, "HTTP error/oversize response accepted");
            }
        } finally { server.stop(0); }
    }
}
