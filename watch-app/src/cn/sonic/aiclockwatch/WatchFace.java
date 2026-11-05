package cn.sonic.aiclockwatch;

/** Platform-independent drawing plan, shared by Android Canvas and offline layout previews. */
public final class WatchFace {
    public interface Surface {
        void circle(float x, float y, float radius, int color, float stroke);
        void arc(float x, float y, float radius, float start, float sweep, int color, float stroke);
        void text(String text, float x, float baseline, float size, int color, boolean mono);
        void sprite(String provider, String state, long now, float x, float y, float width, float height);
    }
    public static final int WHITE = 0xfffffbf7, MUTED = 0xffb5aea5, TRACK = 0xff292829;
    public static final int GREEN = 0xff78dc91, RED = 0xffff6060, ORANGE = 0xffde7552, BLUE = 0xff8b94ff;
    public static final String[] MODES = {"auto", "codex", "claude", "dual"};
    public interface TextMeasure { float width(float size); }
    public static float availableWidth(float x, float baseline, float size) {
        float edgeY = Math.max(Math.abs(baseline - 240), Math.abs(baseline - size - 240));
        float halfChord = (float)Math.sqrt(Math.max(0, 215 * 215 - edgeY * edgeY));
        return Math.max(0, Math.min(x == 240 ? 430 : 174, 2 * (halfChord - Math.abs(x - 240) - 6)));
    }
    public static float fitText(float x, float baseline, float desired, TextMeasure measure) {
        float lo = 1, hi = desired;
        for (int i = 0; i < 12; i++) {
            float mid = (lo + hi) / 2;
            if (measure.width(mid) <= availableWidth(x, baseline, mid) - 2) lo = mid; else hi = mid;
        }
        return lo;
    }
    public QuotaSnapshot snapshot;
    public String mode = "auto", error = "", endpoint = "", wifiIp = "";
    public boolean remaining, dateTime, failed, loading;
    public float fontScale = 1;
    private boolean activeCodex = true;
    private long switched;

    public void draw(Surface s, long now) {
        s.text(BridgeProfiles.display(endpoint), 240, 74, 21, endpoint.isEmpty() ? MUTED : GREEN, true);
        if (snapshot == null) {
            s.circle(240, 240, 226, TRACK, 8);
            title(s, "AI 额度", 159, 37, WHITE);
            title(s, loading ? "正在连接…" : !error.isEmpty() ? error : "连接你的桥接服务", 223, 22, MUTED);
            title(s, "Windows / Linux", 267, 20, MUTED);
            footer(s);
            return;
        }
        boolean stale = failed || now - snapshot.receivedMs > 30000;
        if ("dual".equals(mode)) {
            boolean alert = stale || snapshot.claude.needsInput || snapshot.codex.needsInput;
            border(s, -1, alert, now);
            title(s, "额度总览 · " + (remaining ? "剩余" : "已用"), 115, 25, WHITE);
            dual(s, snapshot.codex, 156, now, stale);
            dual(s, snapshot.claude, 287, now, stale);
        } else {
            QuotaSnapshot.Provider p = selected(now);
            border(s, p.ringUsed(), stale || p.needsInput, now);
            int color = p.name.equals("Claude") ? ORANGE : BLUE;
            s.text(p.name, 137, 142, Math.min(31, 27 * fontScale), color, false);
            String activity = stale ? "断线 · 缓存" : p.needsInput ? "等待确认" : state(p);
            s.text(activity, 137, 365, Math.min(22, 18 * fontScale), stale || p.needsInput ? RED : MUTED, false);
            s.sprite(p.name.toLowerCase(java.util.Locale.ROOT), stale ? "idle" : p.needsInput ? "waiting" : p.working() ? "working" : "idle",
                    now, 46, 163, 182, 182);
            QuotaSnapshot.Window exhausted = p.exhausted();
            if (exhausted != null) {
                s.text(exhausted == p.weekly ? "周额度已用完" : "5h 额度已用完", 329, 192, 21, ORANGE, false);
                s.text(exhausted.percent(remaining), 329, 248, 44, WHITE, false);
                s.text(dateTime ? exhausted.reset(snapshot, true, now) : exhausted.countdown(now), 329, 294, 27, ORANGE, true);
                s.text("等待重置", 329, 325, 18, MUTED, false);
            } else if (p.shortWindow == null) rightQuota(s, p.weekly, "周", 214, now, color, true);
            else {
                rightQuota(s, p.shortWindow, "5h", 156, now, color, false);
                rightQuota(s, p.weekly, "周", 280, now, color, false);
            }
        }
        footer(s);
    }
    private void footer(Surface s) {
        s.text(wifiIp.isEmpty() ? "Wi-Fi 未连接" : "Wi-Fi " + wifiIp, 240, 389, Math.min(21, 17 * fontScale), MUTED, true);
        s.text(modeName(mode) + " · 点按刷新 · 长按设置", 240, 422, Math.min(21, 17 * fontScale), GREEN, false);
    }
    private void rightQuota(Surface s, QuotaSnapshot.Window w, String label, float y, long now, int color, boolean big) {
        s.text(label + " · " + (remaining ? "剩余" : "已用"), 329, y, Math.min(24, 19 * fontScale), MUTED, false);
        s.text(w.percent(remaining), 329, y + (big ? 62 : 48), Math.min(big ? 62 : 47, (big ? 56 : 40) * fontScale), WHITE, false);
        s.text(w.reset(snapshot, dateTime, now), 329, y + (big ? 96 : 76), Math.min(20, 16 * fontScale), color, false);
    }
    private void dual(Surface s, QuotaSnapshot.Provider p, float y, long now, boolean stale) {
        int color = p.name.equals("Claude") ? ORANGE : BLUE;
        s.text(p.name + " · " + (stale ? "缓存" : p.needsInput ? "待确认" : state(p)), 240, y, Math.min(28, 24 * fontScale), color, false);
        if (p.shortWindow != null) {
            dualWindow(s, p.shortWindow, "5h", 147, y + 38, now);
            dualWindow(s, p.weekly, "周", 333, y + 38, now);
        } else dualWindow(s, p.weekly, "周", 240, y + 38, now);
    }
    private void dualWindow(Surface s, QuotaSnapshot.Window w, String name, float x, float y, long now) {
        s.text(name + " " + w.percent(remaining), x, y, Math.min(34, 28 * fontScale), WHITE, false);
        s.text(w.reset(snapshot, dateTime, now), x, y + 27, Math.min(19, 16 * fontScale), MUTED, false);
    }
    private void border(Surface s, double pct, boolean alert, long now) {
        s.circle(240, 240, 226, TRACK, 8);
        if (alert) s.circle(240, 240, 226, now / 600 % 2 == 0 ? RED : 0xff501d1d, 8);
        else if (pct >= 0) s.arc(240, 240, 226, -90, (float)(pct * 3.6), GREEN, 8);
    }
    public QuotaSnapshot.Provider selected(long now) {
        QuotaSnapshot.Provider c = snapshot.claude, x = snapshot.codex;
        boolean desired = activeCodex;
        if ("claude".equals(mode)) desired = false;
        else if ("codex".equals(mode)) desired = true;
        else if (c.needsInput != x.needsInput) desired = x.needsInput;
        else if (c.working() != x.working()) desired = x.working();
        else if (now - switched >= (c.working() && x.working() ? 2000 : 6000)) {
            desired = !activeCodex; switched = now;
        }
        if (desired != activeCodex) { activeCodex = desired; switched = now; }
        return activeCodex ? x : c;
    }
    private static String state(QuotaSnapshot.Provider p) {
        return p.working() ? "工作中" : "idle".equals(p.state) ? "空闲" : "offline".equals(p.state) ? "来源离线" : "状态未知";
    }
    public static String modeName(String mode) {
        return "codex".equals(mode) ? "Codex" : "claude".equals(mode) ? "Claude" : "dual".equals(mode) ? "双栏" : "自动";
    }
    private void title(Surface s, String text, float y, float size, int color) { label(s, text, 240, y, size, color); }
    private void label(Surface s, String text, float x, float y, float size, int color) {
        // Surface additionally fits every line to its circular chord and quota column.
        s.text(text, x, y, size * Math.min(1.35f, Math.max(1, fontScale)), color, false);
    }
    public String description(long now) {
        String network = "桥接地址" + BridgeProfiles.display(endpoint) + "。手表 Wi-Fi 地址" + (wifiIp.isEmpty() ? "未连接" : wifiIp) + "。";
        if (snapshot == null) return network + "AI 额度。" + (loading ? "正在连接。" : error) + "点按刷新，长按设置。";
        return network + describe(snapshot.codex, now) + "。" + describe(snapshot.claude, now)
                + (failed || now - snapshot.receivedMs > 30000 ? "。连接中断，显示缓存数据" : "")
                + "。点按刷新，长按设置，左右滑动切换显示模式。";
    }
    private String describe(QuotaSnapshot.Provider p, long now) {
        return p.name + "，" + (p.needsInput ? "等待电脑端确认" : state(p))
                + (p.shortWindow == null ? "" : "，5 小时" + (remaining ? "剩余" : "已用") + p.shortWindow.percent(remaining))
                + "，周额度" + (remaining ? "剩余" : "已用") + p.weekly.percent(remaining)
                + "，" + p.weekly.reset(snapshot, dateTime, now);
    }
}
