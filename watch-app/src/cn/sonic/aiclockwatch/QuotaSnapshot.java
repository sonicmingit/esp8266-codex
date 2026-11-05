package cn.sonic.aiclockwatch;

import org.json.JSONException;
import org.json.JSONObject;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.SimpleTimeZone;

/** Same wire format and window selection as firmware/src/main.cpp. No account credentials. */
public final class QuotaSnapshot {
    public final Provider claude, codex;
    public final long receivedMs, serverEpoch;
    public final Integer utcOffset;

    private QuotaSnapshot(Provider c, Provider x, long received, long epoch, Integer offset) {
        claude = c; codex = x; receivedMs = received; serverEpoch = epoch; utcOffset = offset;
    }

    public static QuotaSnapshot parse(String json, long now) throws JSONException {
        JSONObject root = new JSONObject(json);
        JSONObject c = root.optJSONObject("claude"), x = root.optJSONObject("codex");
        if (c == null && x == null) throw new JSONException("不是桥接 /status 数据");
        long ts = integer(root, "ts", 0);
        if (ts < 1577836800L || ts > 32503680000L) ts = 0;
        double offset = number(root, "utc_offset_min", Double.NaN);
        Integer zone = !Double.isNaN(offset) && offset == Math.rint(offset) && offset >= -720 && offset <= 840
                ? (int)offset : null;
        return new QuotaSnapshot(provider(c, true, now, ts), provider(x, false, now, ts), now, ts, zone);
    }

    private static Provider provider(JSONObject o, boolean claude, long now, long ts) {
        if (o == null) o = new JSONObject();
        String state = o.optString("status", "unknown");
        if (!state.equals("working") && !state.equals("idle") && !state.equals("offline")) state = "unknown";
        String primary = claude ? "five_hour" : "primary", weekly = claude ? "seven_day" : "weekly";
        double pct = percent(o, primary + "_pct");
        int duration = claude ? 300 : (int)integer(o, "primary_window_min", 300);
        Window shortWindow = pct >= 0 && duration > 0 && duration < 1440
                ? window(o, primary, pct, now, ts) : null;
        Window week = window(o, weekly, percent(o, weekly + "_pct"), now, ts);
        // Legacy weekly-only Codex reports a 7-day primary and no secondary.
        if (!claude && week.used < 0 && duration >= 1440 && pct >= 0)
            week = window(o, primary, pct, now, ts);
        return new Provider(claude ? "Claude" : "Codex", state,
                Boolean.TRUE.equals(o.opt("needs_input")), shortWindow, week);
    }

    private static Window window(JSONObject o, String prefix, double pct, long now, long ts) {
        long mins = integer(o, prefix + "_reset_min", -1);
        long epoch = integer(o, prefix + "_reset_at", 0);
        if (epoch < 1577836800L || epoch > 32503680000L) epoch = 0;
        if (mins > 525600 || mins < 0) mins = -1;
        long seconds = epoch > 0 && ts > 0 ? Math.max(0, epoch - ts) : mins < 0 ? -1 : mins * 60 + 30;
        long deadline = seconds < 0 ? -1 : now + seconds * 1000;
        if (epoch == 0 && ts > 0 && mins >= 0) epoch = ts + mins * 60;
        return new Window(pct, deadline, epoch);
    }

    private static double number(JSONObject o, String key, double fallback) {
        Object value = o.opt(key);
        if (!(value instanceof Number)) return fallback;
        double n = ((Number)value).doubleValue();
        return Double.isNaN(n) || Double.isInfinite(n) ? fallback : n;
    }
    private static long integer(JSONObject o, String key, long fallback) {
        double n = number(o, key, fallback);
        return n >= Long.MIN_VALUE && n < Long.MAX_VALUE ? (long)n : fallback;
    }
    private static double percent(JSONObject o, String key) {
        double n = number(o, key, -1);
        return n < 0 ? -1 : Math.min(100, n);
    }

    public static final class Provider {
        public final String name, state;
        public final boolean needsInput;
        public final Window shortWindow, weekly;
        Provider(String n, String s, boolean input, Window shortW, Window week) {
            name = n; state = s; needsInput = input; shortWindow = shortW; weekly = week;
        }
        public boolean working() { return state.equals("working"); }
        public Window exhausted() {
            if (weekly.used >= 99.9 && weekly.deadlineMs >= 0) return weekly;
            if (shortWindow != null && shortWindow.used >= 99.9 && shortWindow.deadlineMs >= 0) return shortWindow;
            return null;
        }
        public double ringUsed() { return shortWindow == null ? weekly.used : shortWindow.used; }
    }

    public static final class Window {
        public final double used;
        public final long deadlineMs, resetEpoch;
        Window(double pct, long deadline, long epoch) { used = pct; deadlineMs = deadline; resetEpoch = epoch; }
        public long seconds(long now) { return deadlineMs < 0 ? -1 : Math.max(0, (deadlineMs - now + 999) / 1000); }
        public String percent(boolean remaining) {
            return used < 0 ? "—" : (int)(remaining ? Math.ceil(100 - used) : Math.floor(used)) + "%";
        }
        public String reset(QuotaSnapshot snapshot, boolean dateTime, long now) {
            if (dateTime) {
                if (resetEpoch == 0 || snapshot.serverEpoch == 0 || snapshot.utcOffset == null) return "重置时间未知";
                SimpleDateFormat f = new SimpleDateFormat("MM/dd HH:mm", Locale.ROOT);
                f.setTimeZone(new SimpleTimeZone(snapshot.utcOffset * 60000, "bridge"));
                return f.format(new Date(resetEpoch * 1000));
            }
            long s = seconds(now);
            if (s < 0) return "重置时间未知";
            if (s == 0) return "等待额度更新";
            long minutes = (s + 59) / 60;
            if (minutes < 60) return minutes + "分后重置";
            if (minutes < 1440) return minutes / 60 + "时" + minutes % 60 + "分后重置";
            return minutes / 1440 + "天" + minutes % 1440 / 60 + "时后重置";
        }
        public String countdown(long now) {
            long s = seconds(now);
            if (s < 0) return "—";
            if (s == 0) return "等待更新";
            return s >= 360000 ? String.format(Locale.ROOT, "%d:%02d", s / 3600, s % 3600 / 60)
                    : String.format(Locale.ROOT, "%d:%02d:%02d", s / 3600, s % 3600 / 60, s % 60);
        }
    }
}
