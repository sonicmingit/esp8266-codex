package cn.sonic.aiclockwatch;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.net.*;
import android.os.*;
import android.text.InputType;
import android.view.*;
import android.widget.*;
import java.util.*;
import java.util.concurrent.*;

public final class MainActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService executor = Executors.newSingleThreadExecutor();
    private final ExecutorService petExecutor = Executors.newSingleThreadExecutor();
    private final BridgeClient client = new BridgeClient();
    private final WatchFace face = new WatchFace();
    private SharedPreferences prefs;
    private QuotaView quotaView;
    private PetStore petStore;
    private PetPicker petPicker;
    private List<BridgeProfiles.Profile> profiles = Collections.emptyList();
    private WifiLink wifi;
    private boolean automatic = true, watchingNetwork;
    private String preferred = "";
    private final ConnectivityManager.NetworkCallback networkCallback = new ConnectivityManager.NetworkCallback() {
        @Override public void onAvailable(Network network) { handler.post(() -> networkChanged()); }
        @Override public void onLost(Network network) { handler.post(() -> networkChanged()); }
        @Override public void onLinkPropertiesChanged(Network network, LinkProperties props) { handler.post(() -> networkChanged()); }
    };
    private boolean started, configuring, busy;
    private int generation;
    private Future<?> request;
    private final Runnable poll = () -> fetch();

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN, WindowManager.LayoutParams.FLAG_FULLSCREEN);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        prefs = getSharedPreferences("bridge", MODE_PRIVATE);
        petStore = new PetStore(this);
        petExecutor.submit(() -> { petStore.restore(); handler.post(() -> { if (quotaView != null) quotaView.invalidate(); }); });
        applyIntent(getIntent()); readSettings();
        if (profiles.isEmpty()) showSettings(); else showFace();
    }
    @Override public void onNewIntent(Intent intent) {
        super.onNewIntent(intent); setIntent(intent); cancel();
        String oldEndpoint = face.endpoint;
        applyIntent(intent); readSettings();
        if (!oldEndpoint.equals(face.endpoint)) { face.snapshot = null; face.failed = false; face.error = ""; }
        showFace();
    }
    private void applyIntent(Intent intent) {
        if (intent.hasExtra("bridges")) {
            try { prefs.edit().putString("profiles", BridgeProfiles.format(BridgeProfiles.parse(intent.getStringExtra("bridges").replace(',', '\n')))).apply(); }
            catch (IllegalArgumentException e) { Toast.makeText(this, e.getMessage(), Toast.LENGTH_LONG).show(); }
        }
        if (intent.hasExtra("bridge")) {
            try {
                String endpoint = BridgeClient.normalize(intent.getStringExtra("bridge"));
                String old = prefs.getString("profiles", prefs.getString("endpoint", ""));
                prefs.edit().putString("endpoint", endpoint).putString("profiles", BridgeProfiles.format(BridgeProfiles.parse(old + "\n" + endpoint))).apply();
            }
            catch (IllegalArgumentException e) { Toast.makeText(this, e.getMessage(), Toast.LENGTH_LONG).show(); }
        }
        String mode = intent.getStringExtra("mode");
        if (Arrays.asList(WatchFace.MODES).contains(mode)) prefs.edit().putString("mode", mode).apply();
    }
    private void readSettings() {
        profiles = BridgeProfiles.parse(prefs.getString("profiles", prefs.getString("endpoint", "")));
        automatic = prefs.getBoolean("automatic", true);
        face.endpoint = prefs.getString("endpoint", ""); face.mode = prefs.getString("mode", "auto");
        wifi = WifiLink.current(this); chooseEndpoint();
        face.remaining = prefs.getBoolean("remaining", false); face.dateTime = prefs.getBoolean("datetime", false);
        if (prefs.getBoolean("awake", false)) getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        else getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
    }
    @Override protected void onStart() {
        super.onStart(); started = true;
        try {
            ((ConnectivityManager)getSystemService(CONNECTIVITY_SERVICE)).registerNetworkCallback(
                    new NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build(), networkCallback);
            watchingNetwork = true;
        } catch (Exception ignored) { }
        if (petPicker != null) petPicker.resume();
        networkChanged();
        if (!configuring) { if (quotaView != null) quotaView.setActive(true); fetch(); }
    }
    @Override protected void onStop() {
        started = false; if (quotaView != null) quotaView.setActive(false); cancel();
        if (petPicker != null) petPicker.pause();
        if (watchingNetwork) { ((ConnectivityManager)getSystemService(CONNECTIVITY_SERVICE)).unregisterNetworkCallback(networkCallback); watchingNetwork = false; }
        super.onStop();
    }
    @Override protected void onDestroy() { cancel(); petStore.http.cancel(); executor.shutdownNow(); petExecutor.shutdownNow(); super.onDestroy(); }
    @Override public void onWindowFocusChanged(boolean focused) {
        super.onWindowFocusChanged(focused);
        if (focused) getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                | View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
    }
    private void cancel() {
        generation++; handler.removeCallbacks(poll); client.cancel();
        if (request != null) request.cancel(true);
        request = null; busy = false; face.loading = false;
    }
    private void chooseEndpoint() {
        face.wifiIp = wifi.ip;
        if (automatic) {
            List<BridgeProfiles.Profile> list = BridgeProfiles.candidates(profiles, wifi.ip, wifi.prefix, preferred);
            face.endpoint = face.snapshot != null && !preferred.isEmpty() ? preferred : list.isEmpty() ? "" : list.get(0).endpoint;
        } else if (profiles.stream().noneMatch(p -> p.endpoint.equals(face.endpoint))) {
            face.endpoint = profiles.isEmpty() ? "" : profiles.get(0).endpoint;
        }
        if (face.endpoint.isEmpty() && !profiles.isEmpty()) face.error = "当前 Wi-Fi 没有匹配地址";
    }
    private void networkChanged() {
        if (!started) return;
        WifiLink current = WifiLink.current(this);
        if (wifi != null && wifi.key().equals(current.key())) return;
        cancel(); wifi = current; preferred = ""; face.snapshot = null; face.failed = false;
        chooseEndpoint(); if (quotaView != null) quotaView.invalidate(); if (!configuring) fetch();
    }
    private void fetch() {
        if (!started || configuring || busy) return;
        WifiLink current = WifiLink.current(this);
        if (wifi == null || !wifi.key().equals(current.key())) { networkChanged(); return; }
        chooseEndpoint(); if (face.endpoint.isEmpty()) return;
        handler.removeCallbacks(poll); busy = true; face.loading = true;
        final int serial = generation; final WifiLink link = wifi;
        final List<BridgeProfiles.Profile> choices = automatic ? BridgeProfiles.candidates(profiles, wifi.ip, wifi.prefix, preferred)
                : BridgeProfiles.parse(face.endpoint);
        request = executor.submit(() -> {
            QuotaSnapshot result = null; String message = "", used = "";
            for (BridgeProfiles.Profile choice : choices) {
                if (Thread.currentThread().isInterrupted()) break;
                used = choice.endpoint;
                try {
                    // Bind matched LAN requests to Wi-Fi; public fallback keeps the default network (eSIM/VPN included).
                    boolean lan = BridgeProfiles.viaWifi(choice, link.ip, link.prefix);
                    String json = client.fetch(used, url -> link.network != null && lan ? link.network.openConnection(url) : url.openConnection());
                    result = QuotaSnapshot.parse(json, SystemClock.elapsedRealtime()); message = ""; break;
                } catch (Exception e) {
                    message = e instanceof org.json.JSONException ? "响应不是额度数据" : e instanceof javax.net.ssl.SSLException
                            ? "HTTPS 证书验证失败" : e instanceof java.net.SocketTimeoutException ? "连接超时" : "无法连接桥接服务";
                }
            }
            final QuotaSnapshot data = result; final String error = message; final String endpoint = used;
            handler.post(() -> {
                if (serial != generation || !started || configuring) return;
                busy = false; face.loading = false; face.failed = data == null; face.error = error;
                if (data != null) {
                    if (!face.endpoint.equals(endpoint)) face.snapshot = null;
                    face.snapshot = data; face.endpoint = endpoint; preferred = endpoint;
                }
                if (quotaView != null) quotaView.invalidate();
                if (!error.isEmpty()) Toast.makeText(this, error + "，长按检查地址", Toast.LENGTH_SHORT).show();
                handler.postDelayed(poll, 15000);
            });
        });
        if (quotaView != null) quotaView.invalidate();
    }
    private void showFace() {
        if (petPicker != null) { petPicker.close(); petPicker = null; }
        configuring = false;
        quotaView = new QuotaView(this, face, petStore, () -> { if (face.endpoint.isEmpty()) showSettings(); else fetch(); },
                this::showSettings, delta -> {
                    int index = Arrays.asList(WatchFace.MODES).indexOf(face.mode);
                    face.mode = WatchFace.MODES[(index + delta + WatchFace.MODES.length) % WatchFace.MODES.length];
                    prefs.edit().putString("mode", face.mode).apply();
                    Toast.makeText(this, WatchFace.modeName(face.mode), Toast.LENGTH_SHORT).show(); quotaView.invalidate();
                });
        setContentView(quotaView); quotaView.setActive(started); if (started) fetch();
    }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private TextView label(String value, int size) {
        TextView t = new TextView(this); t.setText(value); t.setTextSize(size); t.setTextColor(Color.rgb(228, 231, 225));
        t.setPadding(0, dp(8), 0, dp(6)); return t;
    }
    private CheckBox check(String value, boolean checked) {
        CheckBox c = new CheckBox(this); c.setText(value); c.setTextSize(14); c.setChecked(checked); c.setMinHeight(dp(48)); return c;
    }
    private void showSettings() {
        cancel(); configuring = true;
        ScrollView scroll = new ScrollView(this); scroll.setBackgroundColor(Color.BLACK); scroll.setClipToPadding(false);
        LinearLayout form = new LinearLayout(this); form.setOrientation(LinearLayout.VERTICAL);
        // Native controls inside the circular safe area; scroll past top/bottom padding.
        form.setPadding(dp(34), dp(42), dp(34), dp(46)); scroll.addView(form);
        form.addView(label("连接桥接服务", 20));
        form.addView(label("Wi-Fi：" + (wifi.ip.isEmpty() ? "未连接 IPv4" : wifi.ip + "/" + wifi.prefix), 12));
        form.addView(label("每行一个地址，可保存多个网络的服务", 12));
        EditText address = new EditText(this); address.setTextSize(13); address.setMinLines(3); address.setMaxLines(6);
        address.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        address.setHint("192.168.1.20:8765\n192.168.10.20:8765"); address.setText(BridgeProfiles.format(profiles));
        address.setContentDescription("多个桥接地址，每行一个"); form.addView(address);
        CheckBox auto = check("按 Wi-Fi 网段自动选择", automatic); form.addView(auto);
        form.addView(label("也可指定：192.168.0.0/23 = 主机:端口。公网域名在局域网失败后回退。", 12));
        form.addView(label("关闭自动选择时使用", 12));
        Spinner servers = new Spinner(this);
        String[] names = profiles.stream().map(p -> BridgeProfiles.display(p.endpoint)).toArray(String[]::new);
        servers.setAdapter(new ArrayAdapter<String>(this, android.R.layout.simple_spinner_dropdown_item, names.length == 0 ? new String[]{"请先保存地址"} : names));
        for (int i = 0; i < profiles.size(); i++) if (profiles.get(i).endpoint.equals(face.endpoint)) servers.setSelection(i);
        servers.setContentDescription("手动选择桥接地址"); form.addView(servers, new LinearLayout.LayoutParams(-1, dp(48)));
        servers.setEnabled(!auto.isChecked());
        auto.setOnCheckedChangeListener((button, checked) -> servers.setEnabled(!checked));
        address.addTextChangedListener(new android.text.TextWatcher() {
            public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            public void afterTextChanged(android.text.Editable s) { }
            public void onTextChanged(CharSequence s, int start, int before, int count) {
                try {
                    List<BridgeProfiles.Profile> list = BridgeProfiles.parse(s.toString());
                    int selected = Math.max(0, servers.getSelectedItemPosition());
                    String[] labels = list.stream().map(p -> BridgeProfiles.display(p.endpoint)).toArray(String[]::new);
                    servers.setAdapter(new ArrayAdapter<String>(MainActivity.this, android.R.layout.simple_spinner_dropdown_item,
                            labels.length == 0 ? new String[]{"请先填写地址"} : labels));
                    servers.setSelection(Math.min(selected, Math.max(0, labels.length - 1)));
                } catch (IllegalArgumentException ignored) { /* Validate the completed field on save. */ }
            }
        });
        form.addView(label("显示模式", 14));
        Spinner modes = new Spinner(this);
        modes.setAdapter(new ArrayAdapter<String>(this, android.R.layout.simple_spinner_dropdown_item,
                new String[]{"自动切换", "固定 Codex", "固定 Claude", "双栏额度"}));
        modes.setSelection(Math.max(0, Arrays.asList(WatchFace.MODES).indexOf(face.mode)));
        modes.setContentDescription("显示模式"); form.addView(modes, new LinearLayout.LayoutParams(-1, dp(48)));
        CheckBox remaining = check("显示剩余额度", face.remaining); form.addView(remaining);
        CheckBox date = check("显示重置日期时间", face.dateTime); form.addView(date);
        CheckBox awake = check("页面显示时保持亮屏", prefs.getBoolean("awake", false)); form.addView(awake);
        TextView status = label("", 12); status.setTextColor(WatchFace.RED); form.addView(status);
        Button save = new Button(this); save.setText("保存并连接"); form.addView(save);
        save.setOnClickListener(v -> {
            try {
                List<BridgeProfiles.Profile> list = BridgeProfiles.parse(address.getText().toString());
                if (list.isEmpty()) throw new IllegalArgumentException("请至少填写一个地址");
                String endpoint = list.get(Math.min(Math.max(0, servers.getSelectedItemPosition()), list.size() - 1)).endpoint;
                boolean changed = !BridgeProfiles.format(list).equals(BridgeProfiles.format(profiles)) || auto.isChecked() != automatic || !endpoint.equals(face.endpoint);
                prefs.edit().putString("profiles", BridgeProfiles.format(list)).putBoolean("automatic", auto.isChecked())
                        .putString("endpoint", endpoint).putString("mode", WatchFace.MODES[modes.getSelectedItemPosition()])
                        .putBoolean("remaining", remaining.isChecked()).putBoolean("datetime", date.isChecked())
                        .putBoolean("awake", awake.isChecked()).apply();
                if (changed) { face.snapshot = null; face.failed = false; face.error = ""; }
                preferred = ""; readSettings(); showFace();
            } catch (IllegalArgumentException e) { status.setText(e.getMessage()); }
        });
        form.addView(label("联网更换宠物", 18));
        for (String slot : new String[]{"codex", "claude"}) {
            Button pets = new Button(this); pets.setText((slot.equals("codex") ? "Codex" : "Claude") + "：" + petStore.name(slot));
            pets.setTextSize(13); pets.setOnClickListener(v -> {
                cancel(); configuring = true;
                petPicker = new PetPicker(this, petStore, slot, petExecutor, handler, this::showFace);
            }); form.addView(pets);
        }
        Button back = new Button(this); back.setText("返回"); back.setOnClickListener(v -> showFace()); form.addView(back);
        form.addView(label("前台每 15 秒刷新。左右滑动切换模式，长按进入设置。\n\n局域网使用 HTTP；公网服务可填 HTTPS 地址。宠物由手表直接下载并保存在本地。\n\nAI 额度 0.2.0", 12));
        setContentView(scroll); scroll.setFocusableInTouchMode(true); scroll.requestFocus();
    }
    @Override public void onBackPressed() {
        if (petPicker != null && petPicker.back()) return;
        if (petPicker != null) { petPicker.close(); petPicker = null; showSettings(); }
        else if (configuring) showFace(); else super.onBackPressed();
    }
}
