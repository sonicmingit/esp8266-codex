package cn.sonic.aiclockwatch;

import java.net.URI;
import java.util.*;

/** Saved endpoints and deterministic IPv4 subnet selection. Never scans a network. */
public final class BridgeProfiles {
    public static final class Profile {
        public final String subnet, endpoint;
        Profile(String rule, String url) { subnet = rule; endpoint = url; }
        public String line() { return subnet.isEmpty() ? endpoint : subnet + " = " + endpoint; }
    }
    public static List<Profile> parse(String text) {
        List<Profile> result = new ArrayList<>();
        for (String raw : text.split("\\r?\\n")) {
            String line = raw.trim(); if (line.isEmpty()) continue;
            String rule = "", address = line;
            if (line.contains("=")) {
                String[] pair = line.split("=", -1);
                if (pair.length != 2) throw new IllegalArgumentException("网段格式：192.168.1.0/24 = 主机:端口");
                rule = pair[0].trim(); address = pair[1].trim(); validateSubnet(rule);
            }
            Profile p = new Profile(rule, BridgeClient.normalize(address));
            if (result.stream().noneMatch(old -> old.line().equals(p.line()))) result.add(p);
            if (result.size() > 12) throw new IllegalArgumentException("最多保存 12 个桥接地址");
        }
        return Collections.unmodifiableList(result);
    }
    public static String format(List<Profile> profiles) {
        StringBuilder text = new StringBuilder();
        for (Profile p : profiles) { if (text.length() > 0) text.append('\n'); text.append(p.line()); }
        return text.toString();
    }
    public static List<Profile> candidates(List<Profile> saved, String wifiIp, int prefix, String previous) {
        List<Profile> result = new ArrayList<>();
        for (Profile p : saved) if (score(p, wifiIp, prefix) >= 0) result.add(p);
        result.sort((a, b) -> {
            int delta = Integer.compare(score(b, wifiIp, prefix), score(a, wifiIp, prefix));
            if (delta != 0) return delta;
            return Boolean.compare(b.endpoint.equals(previous), a.endpoint.equals(previous));
        });
        return result;
    }
    private static int score(Profile p, String ip, int prefix) {
        long local = ipv4(ip);
        if (!p.subnet.isEmpty()) {
            String[] rule = p.subnet.split("/"); int bits = Integer.parseInt(rule[1]);
            return local >= 0 && sameSubnet(local, ipv4(rule[0]), bits) ? 200 + bits : -1;
        }
        String host = URI.create(p.endpoint).getHost(); long remote = ipv4(host);
        if (remote >= 0 && local >= 0 && prefix >= 1 && prefix <= 32 && sameSubnet(local, remote, prefix)) return 100 + prefix;
        return remote >= 0 && privateAddress(remote) ? -1 : 0;
    }
    public static boolean viaWifi(Profile p, String ip, int prefix) { return score(p, ip, prefix) >= 100; }
    private static boolean privateAddress(long ip) {
        return (ip >>> 24) == 10 || (ip >>> 20) == 0xac1 || (ip >>> 16) == 0xc0a8 || (ip >>> 16) == 0xa9fe;
    }
    private static void validateSubnet(String value) {
        String[] pieces = value.split("/", -1);
        try {
            if (pieces.length != 2 || ipv4(pieces[0]) < 0) throw new IllegalArgumentException();
            int prefix = Integer.parseInt(pieces[1]);
            if (prefix < 0 || prefix > 32) throw new IllegalArgumentException();
        } catch (Exception e) { throw new IllegalArgumentException("请输入有效 IPv4 网段，例如 192.168.0.0/23"); }
    }
    public static long ipv4(String ip) {
        if (ip == null) return -1;
        String[] parts = ip.split("\\.", -1); if (parts.length != 4) return -1;
        long value = 0;
        try { for (String p : parts) {
            if (!p.matches("[0-9]{1,3}")) return -1;
            int n = Integer.parseInt(p); if (n > 255) return -1; value = value << 8 | n;
        } } catch (NumberFormatException e) { return -1; }
        return value;
    }
    private static boolean sameSubnet(long a, long b, int prefix) {
        if (a < 0 || b < 0 || prefix < 0 || prefix > 32) return false;
        long mask = prefix == 0 ? 0 : (0xffffffffL << (32 - prefix)) & 0xffffffffL;
        return (a & mask) == (b & mask);
    }
    public static String display(String endpoint) {
        if (endpoint == null || endpoint.isEmpty()) return "未选择桥接";
        URI uri = URI.create(endpoint);
        return uri.getHost() + (uri.getPort() == -1 ? "" : ":" + uri.getPort());
    }
}
