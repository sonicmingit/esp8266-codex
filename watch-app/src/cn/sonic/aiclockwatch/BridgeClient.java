package cn.sonic.aiclockwatch;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URI;
import java.nio.charset.StandardCharsets;

public final class BridgeClient {
    public interface ConnectionFactory { java.net.URLConnection open(java.net.URL url) throws IOException; }
    private volatile HttpURLConnection connection;
    public static String normalize(String input) {
        try {
            String value = input.trim();
            if (value.isEmpty()) throw new IllegalArgumentException();
            boolean bare = !value.contains("://");
            URI uri = new URI(bare ? "http://" + value : value);
            if (!("http".equalsIgnoreCase(uri.getScheme()) || "https".equalsIgnoreCase(uri.getScheme()))
                    || uri.getHost() == null || uri.getRawUserInfo() != null
                    || uri.getRawQuery() != null || uri.getRawFragment() != null
                    || uri.getPort() > 65535 || uri.getPort() == 0) throw new IllegalArgumentException();
            String path = uri.getPath() == null ? "" : uri.getPath();
            while (path.endsWith("/")) path = path.substring(0, path.length() - 1);
            if (!path.endsWith("/status")) path += "/status";
            int port = bare && uri.getPort() == -1 ? 8765 : uri.getPort();
            return new URI(uri.getScheme().toLowerCase(java.util.Locale.ROOT), null, uri.getHost(), port, path, null, null).toASCIIString();
        } catch (Exception e) { throw new IllegalArgumentException("请输入主机:端口或 HTTP(S) 地址"); }
    }

    public String fetch(String endpoint) throws IOException {
        return fetch(endpoint, java.net.URL::openConnection);
    }
    public String fetch(String endpoint, ConnectionFactory factory) throws IOException {
        HttpURLConnection active = (HttpURLConnection)factory.open(URI.create(endpoint + "?client=watch").toURL());
        connection = active;
        try {
            active.setConnectTimeout(5000); active.setReadTimeout(5000);
            active.setInstanceFollowRedirects(false); active.setUseCaches(false);
            active.setRequestProperty("Accept", "application/json");
            active.setRequestProperty("User-Agent", "AIClockWatch/0.2.0");
            if (active.getResponseCode() != 200) throw new IOException("HTTP " + active.getResponseCode());
            try (InputStream in = active.getInputStream(); ByteArrayOutputStream out = new ByteArrayOutputStream()) {
                byte[] buffer = new byte[2048]; int n;
                long until = System.nanoTime() + 8_000_000_000L;
                while ((n = in.read(buffer)) != -1) {
                    if (Thread.currentThread().isInterrupted()) throw new IOException("已取消");
                    if (out.size() + n > 65536 || System.nanoTime() > until) throw new IOException("桥接响应过大或超时");
                    out.write(buffer, 0, n);
                }
                return new String(out.toByteArray(), StandardCharsets.UTF_8);
            }
        } finally { active.disconnect(); if (connection == active) connection = null; }
    }
    public void cancel() { HttpURLConnection active = connection; if (active != null) active.disconnect(); }
}
