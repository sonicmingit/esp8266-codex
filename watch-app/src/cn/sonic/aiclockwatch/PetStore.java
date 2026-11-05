package cn.sonic.aiclockwatch;

import android.content.Context;
import android.content.SharedPreferences;
import android.graphics.*;
import android.util.AtomicFile;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.*;
import java.util.concurrent.ConcurrentHashMap;

public final class PetStore {
    private final Context context;
    private final SharedPreferences prefs;
    private final Map<String, Animation> loaded = new ConcurrentHashMap<>();
    public final HttpBytes http = new HttpBytes();
    public boolean cachedCatalog;
    public static final class Animation {
        final Bitmap[][] frames;
        Animation(Bitmap[][] data) { frames = data; }
        public Bitmap frame(String state, long now) {
            int row = "working".equals(state) ? 1 : "waiting".equals(state) ? 2 : 0;
            int duration = row == 1 ? 820 : row == 2 ? 1010 : 1100;
            return frames[row][(int)(now / Math.max(1, duration / 6) % 6)];
        }
    }
    public static final class Prepared {
        final PetCatalog.Pet pet; final byte[] bytes; final Animation animation;
        Prepared(PetCatalog.Pet p, byte[] b, Animation a) { pet = p; bytes = b; animation = a; }
    }
    public PetStore(Context c) { context = c; prefs = c.getSharedPreferences("pets", Context.MODE_PRIVATE); }
    public String name(String slot) { return prefs.getString(slot + "_name", "固件默认宠物"); }
    public Animation animation(String slot) { return loaded.get(slot); }
    public void restore() {
        for (String slot : new String[]{"codex", "claude"}) {
            try {
                byte[] bytes;
                try (InputStream in = new AtomicFile(file(slot)).openRead()) { bytes = read(in, 12 * 1024 * 1024); }
                loaded.put(slot, decode(bytes));
            } catch (Exception ignored) { loaded.remove(slot); }
        }
    }
    private File file(String slot) {
        if (!slot.equals("codex") && !slot.equals("claude")) throw new IllegalArgumentException("宠物位置无效");
        return new File(context.getFilesDir(), "pet-" + slot + ".webp");
    }
    public List<PetCatalog.Pet> catalog() throws Exception {
        File cache = new File(context.getCacheDir(), "petdex.json");
        try {
            byte[] bytes = http.get(PetCatalog.MANIFEST, 8 * 1024 * 1024);
            List<PetCatalog.Pet> list = PetCatalog.parse(new String(bytes, StandardCharsets.UTF_8));
            Files.write(cache.toPath(), bytes); cachedCatalog = false; return list;
        } catch (Exception e) {
            if (Thread.currentThread().isInterrupted() || !cache.exists()) throw e;
            List<PetCatalog.Pet> list = PetCatalog.parse(new String(Files.readAllBytes(cache.toPath()), StandardCharsets.UTF_8));
            cachedCatalog = true; return list;
        }
    }
    public Prepared prepare(PetCatalog.Pet pet) throws Exception {
        byte[] bytes = http.get(pet.url, 12 * 1024 * 1024);
        return new Prepared(pet, bytes, decode(bytes));
    }
    public void apply(String slot, Prepared pet) throws IOException {
        AtomicFile target = new AtomicFile(file(slot)); FileOutputStream out = null;
        try { out = target.startWrite(); out.write(pet.bytes); target.finishWrite(out); }
        catch (IOException e) { target.failWrite(out); throw e; }
        loaded.put(slot, pet.animation);
        prefs.edit().putString(slot + "_name", pet.pet.name).putString(slot + "_slug", pet.pet.slug).apply();
    }
    public void reset(String slot) {
        new AtomicFile(file(slot)).delete(); loaded.remove(slot);
        prefs.edit().remove(slot + "_name").remove(slot + "_slug").apply();
    }
    private static Animation decode(byte[] bytes) throws IOException {
        BitmapRegionDecoder decoder = BitmapRegionDecoder.newInstance(bytes, 0, bytes.length, false);
        if (decoder == null) throw new IOException("宠物图片解码失败");
        try {
            if (decoder.getWidth() != 1536 || decoder.getHeight() != 1872) throw new IOException("宠物精灵图尺寸不兼容");
            Bitmap[][] frames = new Bitmap[3][6]; int[] rows = {0, 7, 6};
            BitmapFactory.Options options = new BitmapFactory.Options(); options.inPreferredConfig = Bitmap.Config.ARGB_8888;
            for (int r = 0; r < rows.length; r++) for (int i = 0; i < 6; i++) {
                if (Thread.currentThread().isInterrupted()) throw new InterruptedIOException();
                frames[r][i] = decoder.decodeRegion(new Rect(i * 192, rows[r] * 208, (i + 1) * 192, (rows[r] + 1) * 208), options);
                if (frames[r][i] == null) throw new IOException("宠物动画帧解码失败");
            }
            return new Animation(frames);
        } finally { decoder.recycle(); }
    }
    private static byte[] read(InputStream in, int limit) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream(); byte[] b = new byte[8192]; int n;
        while ((n = in.read(b)) != -1) { if (out.size() + n > limit) throw new IOException("宠物缓存过大"); out.write(b, 0, n); }
        return out.toByteArray();
    }
}
