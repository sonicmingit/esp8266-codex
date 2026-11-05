package cn.sonic.aiclockwatch;

import java.awt.*;
import java.awt.geom.*;
import java.awt.image.BufferedImage;
import java.nio.file.*;
import javax.imageio.ImageIO;

/** Runs the production WatchFace drawing plan; Java2D preview is not a device screenshot. */
public final class RenderPreview implements WatchFace.Surface {
    final Graphics2D g;
    final Path root;
    static final long NOW = 12000;
    int textCount;
    RenderPreview(Graphics2D canvas, Path app) { g = canvas; root = app; }
    public void circle(float x, float y, float r, int color, float stroke) {
        g.setColor(new Color(color, true)); g.setStroke(new BasicStroke(stroke));
        g.draw(new Ellipse2D.Float(x - r, y - r, 2 * r, 2 * r));
    }
    public void arc(float x, float y, float r, float start, float sweep, int color, float stroke) {
        g.setColor(new Color(color, true)); g.setStroke(new BasicStroke(stroke, BasicStroke.CAP_ROUND, BasicStroke.JOIN_ROUND));
        g.draw(new Arc2D.Float(x - r, y - r, 2 * r, 2 * r, -start, -sweep, Arc2D.OPEN));
    }
    public void text(String text, float x, float baseline, float size, int color, boolean mono) {
        g.setColor(new Color(color, true));
        Font base = new Font(mono ? "Monospaced" : "Microsoft YaHei", Font.PLAIN, 20);
        float fitted = WatchFace.fitText(x, baseline, size,
                candidate -> (float)base.deriveFont(candidate).getStringBounds(text, g.getFontRenderContext()).getWidth());
        Font font = base.deriveFont(fitted); g.setFont(font);
        float width = WatchFace.availableWidth(x, baseline, fitted);
        Rectangle2D bounds = font.getStringBounds(text, g.getFontRenderContext());
        float actualWidth = (float)bounds.getWidth();
        if (actualWidth > width + 1) throw new AssertionError("Text escaped circular safe area: " + text);
        g.drawString(text, x - actualWidth / 2, baseline); textCount++;
    }
    public void sprite(String provider, String state, long now, float x, float y, float width, float height) {
        try {
            int frame = "working".equals(state) ? (int)(now / 160 % 6) : 0;
            BufferedImage image = ImageIO.read(root.resolve("res/drawable-nodpi/" + provider + "_" + frame + ".png").toFile());
            g.setRenderingHint(RenderingHints.KEY_INTERPOLATION, RenderingHints.VALUE_INTERPOLATION_NEAREST_NEIGHBOR);
            float ratio = Math.min(width / image.getWidth(), height / image.getHeight());
            int w = Math.round(image.getWidth() * ratio), h = Math.round(image.getHeight() * ratio);
            g.drawImage(image, (int)(x + (width - w) / 2), (int)(y + (height - h) / 2), w, h, null);
        } catch (Exception e) { throw new RuntimeException(e); }
    }
    public static void main(String[] args) throws Exception {
        Path root = Paths.get(args[0]); Path dest = root.resolve("qa/generated"); Files.createDirectories(dest);
        String normal = "{\"ts\":1798732800,\"utc_offset_min\":480,\"claude\":{\"status\":\"working\",\"five_hour_pct\":28,\"five_hour_reset_min\":139,\"seven_day_pct\":61,\"seven_day_reset_min\":3816},\"codex\":{\"status\":\"idle\",\"weekly_pct\":24,\"weekly_reset_min\":8729}}";
        int index = 0;
        BufferedImage contact = new BufferedImage(1440, 1440, BufferedImage.TYPE_INT_RGB);
        Graphics2D sheet = contact.createGraphics(); sheet.setColor(new Color(0xff171b18)); sheet.fillRect(0, 0, 1440, 1440);
        String[] scenarios = {"codex-weekly", "claude", "dual", "exhausted", "stale", "unknown", "approval", "datetime-large", "setup"};
        for (String scenario : scenarios) {
            WatchFace face = new WatchFace(); face.snapshot = QuotaSnapshot.parse(normal, NOW); face.mode = "codex";
            face.endpoint = "http://192.168.0.118:8765/status"; face.wifiIp = "192.168.1.203";
            if (scenario.equals("claude")) face.mode = "claude";
            if (scenario.equals("dual")) face.mode = "dual";
            if (scenario.equals("exhausted")) face.snapshot = QuotaSnapshot.parse("{\"codex\":{\"weekly_pct\":100,\"weekly_reset_min\":165}}", NOW);
            if (scenario.equals("stale")) face.failed = true;
            if (scenario.equals("unknown")) face.snapshot = QuotaSnapshot.parse("{\"codex\":{}}", NOW);
            if (scenario.equals("approval")) face.snapshot = QuotaSnapshot.parse("{\"codex\":{\"needs_input\":true,\"weekly_pct\":42}}", NOW);
            if (scenario.equals("datetime-large")) { face.mode = "claude"; face.dateTime = true; face.fontScale = 1.35f; }
            if (scenario.equals("setup")) face.snapshot = null;
            BufferedImage preview = new BufferedImage(480, 480, BufferedImage.TYPE_INT_ARGB);
            Graphics2D g = preview.createGraphics();
            g.setClip(new Ellipse2D.Float(0, 0, 480, 480)); g.setColor(Color.BLACK); g.fillRect(0, 0, 480, 480);
            g.setRenderingHint(RenderingHints.KEY_ANTIALIASING, RenderingHints.VALUE_ANTIALIAS_ON);
            g.setRenderingHint(RenderingHints.KEY_TEXT_ANTIALIASING, RenderingHints.VALUE_TEXT_ANTIALIAS_ON);
            RenderPreview renderer = new RenderPreview(g, root); face.draw(renderer, NOW);
            if (renderer.textCount < 3) throw new AssertionError("Incomplete face " + scenario);
            g.dispose(); ImageIO.write(preview, "PNG", dest.resolve(scenario + ".png").toFile());
            sheet.drawImage(preview, index % 3 * 480, index / 3 * 480, null); index++;
        }
        sheet.dispose(); ImageIO.write(contact, "PNG", dest.resolve("contact-sheet.png").toFile());
        System.out.println("PASS 9 circular layout previews (Java2D, not device screenshots): " + dest);
    }
}
