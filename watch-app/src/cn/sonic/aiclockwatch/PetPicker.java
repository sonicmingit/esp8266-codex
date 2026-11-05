package cn.sonic.aiclockwatch;

import android.app.Activity;
import android.graphics.Color;
import android.os.Handler;
import android.text.*;
import android.widget.*;
import java.util.*;
import java.util.concurrent.*;

/** Native, searchable Petdex browser. Preview/download on the watch; apply only after confirmation. */
public final class PetPicker {
    private final Activity activity;
    private final PetStore store;
    private final ExecutorService executor;
    private final Handler handler;
    private final Runnable home;
    private final String slot;
    private final ScrollView scroll;
    private final LinearLayout form;
    private List<PetCatalog.Pet> catalog = Collections.emptyList();
    private LinearLayout results;
    private TextView status;
    private String query = "";
    private int page, generation;
    private boolean closed, paused;
    private PetCatalog.Pet selectedPet;
    private Future<?> job;
    public PetPicker(Activity a, PetStore s, String target, ExecutorService workers, Handler h, Runnable back) {
        activity = a; store = s; slot = target; executor = workers; handler = h; home = back;
        scroll = new ScrollView(a); scroll.setBackgroundColor(Color.BLACK);
        form = new LinearLayout(a); form.setOrientation(LinearLayout.VERTICAL); form.setPadding(dp(32), dp(42), dp(32), dp(46));
        scroll.addView(form); activity.setContentView(scroll); browser(); load();
    }
    private int dp(int value) { return Math.round(value * activity.getResources().getDisplayMetrics().density); }
    private TextView text(String value, int size) {
        TextView t = new TextView(activity); t.setText(value); t.setTextSize(size); t.setTextColor(WatchFace.WHITE);
        t.setPadding(0, dp(8), 0, dp(6)); return t;
    }
    private Button button(String title, Runnable click) {
        Button b = new Button(activity); b.setText(title); b.setTextSize(13); b.setMinHeight(dp(48)); b.setOnClickListener(v -> click.run()); return b;
    }
    private void browser() {
        selectedPet = null;
        form.removeAllViews();
        form.addView(text((slot.equals("codex") ? "Codex" : "Claude") + " 宠物", 20));
        form.addView(text("当前：" + store.name(slot), 12));
        EditText search = new EditText(activity); search.setSingleLine(true); search.setTextSize(14);
        search.setHint("搜索名字或英文名"); search.setContentDescription("搜索在线宠物"); search.setText(query); form.addView(search);
        status = text("正在获取在线宠物…", 12); form.addView(status);
        results = new LinearLayout(activity); results.setOrientation(LinearLayout.VERTICAL); form.addView(results);
        search.addTextChangedListener(new TextWatcher() {
            public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            public void onTextChanged(CharSequence s, int start, int before, int count) { query = s.toString(); page = 0; render(); }
            public void afterTextChanged(Editable s) { }
        });
        form.addView(button("刷新在线目录", this::load));
        form.addView(button("恢复固件默认宠物", () -> { store.reset(slot); close(); home.run(); }));
        form.addView(button("返回首页", () -> { close(); home.run(); }));
        render(); scroll.setFocusableInTouchMode(true); scroll.requestFocus(); scroll.smoothScrollTo(0, 0);
    }
    private void render() {
        if (results == null) return;
        results.removeAllViews(); List<PetCatalog.Pet> filtered = PetCatalog.search(catalog, query);
        int pages = Math.max(1, (filtered.size() + 7) / 8); page = Math.max(0, Math.min(page, pages - 1));
        status.setText((store.cachedCatalog ? "离线目录 · " : "") + filtered.size() + " 只 · " + (page + 1) + "/" + pages + " 页");
        for (int i = page * 8; i < Math.min(filtered.size(), page * 8 + 8); i++) {
            PetCatalog.Pet pet = filtered.get(i); results.addView(button(pet.name, () -> detail(pet)));
        }
        LinearLayout paging = new LinearLayout(activity);
        Button prev = button("上一页", () -> { page--; render(); }); prev.setEnabled(page > 0);
        Button next = button("下一页", () -> { page++; render(); }); next.setEnabled(page < pages - 1);
        paging.addView(prev, new LinearLayout.LayoutParams(0, -2, 1)); paging.addView(next, new LinearLayout.LayoutParams(0, -2, 1)); results.addView(paging);
    }
    private void load() {
        cancel(); status.setText("正在获取在线宠物…"); int serial = generation;
        job = executor.submit(() -> {
            try {
                List<PetCatalog.Pet> data = store.catalog();
                handler.post(() -> { if (valid(serial)) { catalog = data; render(); } });
            } catch (Exception e) { handler.post(() -> { if (valid(serial)) status.setText("目录加载失败，请检查网络后重试"); }); }
        });
    }
    private void detail(PetCatalog.Pet pet) {
        selectedPet = pet;
        cancel(); form.removeAllViews(); results = null;
        form.addView(text(pet.name, 20));
        ImageView image = new ImageView(activity); image.setScaleType(ImageView.ScaleType.FIT_CENTER);
        image.setContentDescription(pet.name + "宠物预览"); form.addView(image, new LinearLayout.LayoutParams(-1, dp(100)));
        TextView message = text("正在下载并检查动画…", 12); form.addView(message);
        final PetStore.Prepared[] prepared = {null};
        Button apply = button("用于 " + (slot.equals("codex") ? "Codex" : "Claude"), () -> {
            if (prepared[0] == null) return;
            try { store.apply(slot, prepared[0]); close(); home.run(); }
            catch (Exception e) { message.setText("保存失败，请重试"); }
        });
        apply.setEnabled(false); form.addView(apply);
        form.addView(button("返回宠物目录", () -> { cancel(); browser(); }));
        form.addView(text("待机、工作、等待确认会自动使用对应动画。下载后保存在手表，断网也能显示。", 12));
        int serial = generation;
        job = executor.submit(() -> {
            try {
                PetStore.Prepared data = store.prepare(pet);
                handler.post(() -> { if (valid(serial)) { prepared[0] = data; image.setImageBitmap(data.animation.frame("idle", 0));
                    message.setText("预览已就绪，应用后返回首页"); apply.setEnabled(true); } });
            } catch (Exception e) { handler.post(() -> { if (valid(serial)) {
                message.setText(e instanceof java.io.IOException && e.getMessage() != null ? e.getMessage() : "下载失败，请返回重试");
            } }); }
        });
        scroll.smoothScrollTo(0, 0);
    }
    private boolean valid(int serial) { return !closed && !paused && serial == generation; }
    private void cancel() { generation++; store.http.cancel(); if (job != null) job.cancel(true); }
    public void pause() { paused = true; cancel(); }
    public void resume() {
        if (closed || !paused) return;
        paused = false;
        if (selectedPet != null) detail(selectedPet);
        else { browser(); if (catalog.isEmpty()) load(); }
    }
    public boolean back() {
        if (selectedPet == null) return false;
        cancel(); browser(); return true;
    }
    public void close() { closed = true; cancel(); }
}
