package cn.sonic.aiclockwatch;

import org.json.*;
import java.net.URI;
import java.util.*;

public final class PetCatalog {
    public static final String MANIFEST = "https://assets.petdex.dev/manifests/petdex-v1.json";
    public static final class Pet {
        public final String slug, name, url;
        Pet(String id, String label, String sheet) { slug = id; name = label; url = sheet; }
    }
    public static List<Pet> parse(String json) throws JSONException {
        JSONArray entries = new JSONObject(json).getJSONArray("pets");
        if (entries.length() > 10000) throw new JSONException("宠物目录过大");
        List<Pet> pets = new ArrayList<>(); Set<String> seen = new HashSet<>();
        for (int i = 0; i < entries.length(); i++) {
            JSONObject item = entries.optJSONObject(i); if (item == null) continue;
            String slug = item.optString("slug", ""), url = item.optString("spritesheetUrl", "");
            if (!slug.matches("[A-Za-z0-9_-]{1,100}") || !validAsset(url) || !seen.add(slug)) continue;
            String name = item.optString("displayName", slug);
            pets.add(new Pet(slug, name.length() > 80 ? name.substring(0, 80) : name, url));
        }
        if (pets.isEmpty()) throw new JSONException("宠物目录为空");
        return Collections.unmodifiableList(pets);
    }
    public static boolean validAsset(String url) {
        try {
            URI uri = URI.create(url);
            return "https".equals(uri.getScheme()) && "assets.petdex.dev".equals(uri.getHost())
                    && (uri.getPort() == -1 || uri.getPort() == 443) && uri.getRawUserInfo() == null
                    && uri.getRawFragment() == null && uri.getPath().startsWith("/pets/");
        } catch (Exception e) { return false; }
    }
    public static List<Pet> search(List<Pet> pets, String query) {
        String q = query.trim().toLowerCase(Locale.ROOT);
        List<Pet> result = new ArrayList<>();
        for (Pet p : pets) if ((p.name + " " + p.slug).toLowerCase(Locale.ROOT).contains(q)) result.add(p);
        return result;
    }
}
