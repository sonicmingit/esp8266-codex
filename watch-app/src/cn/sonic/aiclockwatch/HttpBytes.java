package cn.sonic.aiclockwatch;

import java.io.*;
import java.net.*;

/** Bounded, cancelable HTTPS downloads. No cookies, credentials, redirects or TLS bypass. */
public final class HttpBytes {
    private volatile HttpURLConnection active;
    public byte[] get(String url, int limit) throws IOException {
        if (!url.equals(PetCatalog.MANIFEST) && !PetCatalog.validAsset(url)) throw new IOException("宠物资源地址无效");
        HttpURLConnection connection = (HttpURLConnection)new URL(url).openConnection(); active = connection;
        try {
            connection.setConnectTimeout(10000); connection.setReadTimeout(10000); connection.setInstanceFollowRedirects(false);
            connection.setRequestProperty("User-Agent", "AIClockWatch/0.2.0");
            if (connection.getResponseCode() != 200) throw new IOException("宠物服务 HTTP " + connection.getResponseCode());
            if (connection.getContentLengthLong() > limit) throw new IOException("宠物资源过大");
            long until = System.nanoTime() + 30_000_000_000L;
            try (InputStream in = connection.getInputStream(); ByteArrayOutputStream out = new ByteArrayOutputStream()) {
                byte[] buffer = new byte[8192]; int n;
                while ((n = in.read(buffer)) != -1) {
                    if (Thread.currentThread().isInterrupted()) throw new InterruptedIOException();
                    if (out.size() + n > limit || System.nanoTime() > until) throw new IOException("宠物下载过大或超时");
                    out.write(buffer, 0, n);
                }
                return out.toByteArray();
            }
        } finally { connection.disconnect(); if (active == connection) active = null; }
    }
    public void cancel() { HttpURLConnection c = active; if (c != null) c.disconnect(); }
}
