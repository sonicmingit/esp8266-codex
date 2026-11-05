package cn.sonic.aiclockwatch;

import android.content.Context;
import android.graphics.*;
import android.os.Bundle;
import android.os.SystemClock;
import android.view.*;
import android.view.accessibility.AccessibilityNodeInfo;

public final class QuotaView extends View implements WatchFace.Surface {
    private final WatchFace face;
    private final PetStore pets;
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Bitmap[] claude = new Bitmap[6], codex = new Bitmap[6];
    private final Runnable refresh, settings;
    private final java.util.function.IntConsumer cycle;
    private final GestureDetector detector;
    private Canvas canvas;
    private boolean active;
    private float scale = 1, ox, oy;
    private final RectF rect = new RectF();
    private final Runnable tick = new Runnable() {
        public void run() {
            if (!active) return;
            invalidate(); postDelayed(this, face.snapshot != null && !face.failed
                && SystemClock.elapsedRealtime() - face.snapshot.receivedMs <= 30000
                && (face.snapshot.claude.working() || face.snapshot.codex.working()
                || pets.animation("codex") != null || pets.animation("claude") != null) ? 160 : 600);
        }
    };

    public QuotaView(Context context, WatchFace model, PetStore store, Runnable onRefresh, Runnable onSettings,
                     java.util.function.IntConsumer onCycle) {
        super(context); face = model; pets = store; refresh = onRefresh; settings = onSettings; cycle = onCycle;
        setFocusable(true); setClickable(true); setLongClickable(true);
        setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_YES);
        face.fontScale = getResources().getConfiguration().fontScale;
        for (int i = 0; i < 6; i++) {
            claude[i] = load("claude_" + i); codex[i] = load("codex_" + i);
        }
        detector = new GestureDetector(context, new GestureDetector.SimpleOnGestureListener() {
            public boolean onDown(android.view.MotionEvent e) { return true; }
            public boolean onSingleTapUp(android.view.MotionEvent e) { performClick(); return true; }
            public void onLongPress(android.view.MotionEvent e) { performLongClick(); }
            public boolean onFling(android.view.MotionEvent a, android.view.MotionEvent b, float vx, float vy) {
                if (a != null && Math.abs(b.getX() - a.getX()) > 64 * scale && Math.abs(vx) > Math.abs(vy)) {
                    cycle.accept(b.getX() < a.getX() ? 1 : -1); return true;
                } return false;
            }
        });
    }
    private Bitmap load(String name) {
        int id = getResources().getIdentifier(name, "drawable", getContext().getPackageName());
        if (id == 0) throw new IllegalStateException("Missing firmware sprite " + name);
        return BitmapFactory.decodeResource(getResources(), id);
    }
    public void setActive(boolean value) { active = value; removeCallbacks(tick); if (active) post(tick); }
    @Override protected void onAttachedToWindow() { super.onAttachedToWindow(); setActive(active); }
    @Override protected void onDetachedFromWindow() { removeCallbacks(tick); super.onDetachedFromWindow(); }
    @Override public boolean onTouchEvent(android.view.MotionEvent event) { return detector.onTouchEvent(event); }
    @Override public boolean performClick() { super.performClick(); refresh.run(); return true; }
    @Override public boolean performLongClick() { super.performLongClick(); settings.run(); return true; }
    @Override public void onInitializeAccessibilityNodeInfo(AccessibilityNodeInfo info) {
        super.onInitializeAccessibilityNodeInfo(info);
        info.setContentDescription(face.description(SystemClock.elapsedRealtime()));
        info.addAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_SCROLL_FORWARD);
        info.addAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_SCROLL_BACKWARD);
    }
    @Override public boolean performAccessibilityAction(int action, Bundle args) {
        if (action == AccessibilityNodeInfo.ACTION_SCROLL_FORWARD || action == AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD) {
            cycle.accept(action == AccessibilityNodeInfo.ACTION_SCROLL_FORWARD ? 1 : -1); return true;
        } return super.performAccessibilityAction(action, args);
    }
    @Override protected void onDraw(Canvas c) {
        super.onDraw(c); c.drawColor(Color.BLACK); canvas = c;
        scale = Math.min(getWidth(), getHeight()) / 480f;
        ox = (getWidth() - 480 * scale) / 2; oy = (getHeight() - 480 * scale) / 2;
        c.save(); c.translate(ox, oy); c.scale(scale, scale);
        face.draw(this, SystemClock.elapsedRealtime()); c.restore();
    }
    public void circle(float x, float y, float radius, int color, float stroke) {
        paint.setColor(color); paint.setStyle(Paint.Style.STROKE); paint.setStrokeWidth(stroke);
        canvas.drawCircle(x, y, radius, paint);
    }
    public void arc(float x, float y, float radius, float start, float sweep, int color, float stroke) {
        paint.setColor(color); paint.setStyle(Paint.Style.STROKE); paint.setStrokeWidth(stroke); paint.setStrokeCap(Paint.Cap.ROUND);
        rect.set(x - radius, y - radius, x + radius, y + radius); canvas.drawArc(rect, start, sweep, false, paint);
        paint.setStrokeCap(Paint.Cap.BUTT);
    }
    public void text(String text, float x, float baseline, float size, int color, boolean mono) {
        paint.setStyle(Paint.Style.FILL); paint.setColor(color); paint.setTextAlign(Paint.Align.CENTER);
        paint.setTypeface(mono ? Typeface.MONOSPACE : Typeface.create("sans-serif", Typeface.NORMAL));
        paint.setTextSize(WatchFace.fitText(x, baseline, size, candidate -> {
            paint.setTextSize(candidate); return paint.measureText(text);
        }));
        canvas.drawText(text, x, baseline, paint);
    }
    public void sprite(String provider, String state, long now, float x, float y, float width, float height) {
        paint.setStyle(Paint.Style.FILL); paint.setFilterBitmap(false);
        PetStore.Animation custom = pets.animation(provider);
        Bitmap bitmap = custom == null ? (provider.equals("claude") ? claude : codex)["working".equals(state) ? (int)(now / 160 % 6) : 0]
                : custom.frame(state, now);
        float ratio = Math.min(width / bitmap.getWidth(), height / bitmap.getHeight());
        float w = bitmap.getWidth() * ratio, h = bitmap.getHeight() * ratio;
        rect.set(x + (width - w) / 2, y + (height - h) / 2, x + (width + w) / 2, y + (height + h) / 2);
        canvas.drawBitmap(bitmap, null, rect, paint);
    }
}
