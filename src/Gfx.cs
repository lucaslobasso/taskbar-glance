// Drawing without image files or icon fonts: anti-aliased shapes from signed distances, blended into a 32-bit
// bitmap. The weather icons are a handful of discs, capsules and one polygon, so they stay sharp at any scaling.
namespace TaskbarGlance;

static unsafe class Gfx
{
    // target of the drawing calls that follow (single UI thread)
    static uint* px;
    static int W, H;
    static bool opaque;
    static uint bg;

    public delegate float Sdf(float x, float y);

    /// <summary>Where to draw. A transparent target holds premultiplied alpha (the strip); an opaque one has a known
    /// background colour (the popup), which is what "erase" paints there.</summary>
    public static void Target(uint* pixels, int w, int h, uint? opaqueBackground = null)
    {
        px = pixels; W = w; H = h; opaque = opaqueBackground != null; bg = opaqueBackground ?? 0;
    }

    /// <summary>Source-over blend of 0xRRGGBB at the given coverage.</summary>
    public static void Over(int x, int y, uint rgb, float cov)
    {
        if ((uint)x >= (uint)W || (uint)y >= (uint)H || cov <= 0) return;
        uint a = (uint)(Math.Min(cov, 1f) * 255 + 0.5f);
        if (a == 0) return;
        uint d = px[y * W + x], inv = 255 - a;
        uint oa = Math.Min(255u, a + ((d >> 24) * inv + 127) / 255);
        uint or = (((rgb >> 16) & 0xFF) * a + ((d >> 16) & 0xFF) * inv + 127) / 255;
        uint og = (((rgb >> 8) & 0xFF) * a + ((d >> 8) & 0xFF) * inv + 127) / 255;
        uint ob = ((rgb & 0xFF) * a + (d & 0xFF) * inv + 127) / 255;
        px[y * W + x] = (oa << 24) | (or << 16) | (og << 8) | ob;
    }

    /// <summary>Removes what is there: back to transparent on the strip, back to the background on the popup.</summary>
    static void Erase(int x, int y, float cov)
    {
        if (opaque) { Over(x, y, bg, cov); return; }
        if ((uint)x >= (uint)W || (uint)y >= (uint)H || cov <= 0) return;
        uint k = 255 - (uint)(Math.Min(cov, 1f) * 255 + 0.5f), d = px[y * W + x];
        uint oa = Math.Max(1u, ((d >> 24) * k + 127) / 255); // alpha 1 keeps the strip clickable
        px[y * W + x] = (oa << 24) | (((((d >> 16) & 0xFF) * k + 127) / 255) << 16) | (((((d >> 8) & 0xFF) * k + 127) / 255) << 8) | (((d & 0xFF) * k + 127) / 255);
    }

    /// <summary>Fills the shape whose signed distance (negative inside, in pixels) is sd, within the given box.</summary>
    public static void Fill(float x0, float y0, float x1, float y1, Sdf sd, uint rgb, bool erase = false)
    {
        for (int y = (int)MathF.Floor(y0) - 1; y <= (int)MathF.Ceiling(y1) + 1; y++)
            for (int x = (int)MathF.Floor(x0) - 1; x <= (int)MathF.Ceiling(x1) + 1; x++)
            {
                float cov = Math.Clamp(0.5f - sd(x + 0.5f, y + 0.5f), 0f, 1f);
                if (cov <= 0) continue;
                if (erase) Erase(x, y, cov); else Over(x, y, rgb, cov);
            }
    }

    public static float Disc(float x, float y, float cx, float cy, float r)
    {
        float dx = x - cx, dy = y - cy;
        return MathF.Sqrt(dx * dx + dy * dy) - r;
    }

    /// <summary>Distance to the segment a-b, minus r: a line with round ends.</summary>
    public static float Capsule(float x, float y, float ax, float ay, float bx, float by, float r)
    {
        float vx = bx - ax, vy = by - ay, wx = x - ax, wy = y - ay;
        float len2 = vx * vx + vy * vy;
        float t = len2 <= 0 ? 0 : Math.Clamp((wx * vx + wy * vy) / len2, 0f, 1f);
        float dx = wx - t * vx, dy = wy - t * vy;
        return MathF.Sqrt(dx * dx + dy * dy) - r;
    }

    /// <summary>Signed distance to a closed polygon given as x0,y0,x1,y1,...</summary>
    public static float Polygon(float x, float y, float[] p)
    {
        int n = p.Length / 2;
        float best = float.MaxValue;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float ax = p[2 * j], ay = p[2 * j + 1], bx = p[2 * i], by = p[2 * i + 1];
            best = Math.Min(best, Capsule(x, y, ax, ay, bx, by, 0));
            if ((ay > y) != (by > y) && x < (bx - ax) * (y - ay) / (by - ay) + ax) inside = !inside;
        }
        return inside ? -best : best;
    }
}

static class Icons
{
    static float cx, cy, d;
    static bool light;

    // icon space: the icon's centre is 0,0 and its box runs from -0.5 to 0.5; y points down
    static float X(float u) => cx + u * d;
    static float Y(float u) => cy + u * d;
    static void Fill(Gfx.Sdf sd, uint rgb, bool erase = false) => Gfx.Fill(cx - d * 0.6f, cy - d * 0.6f, cx + d * 0.6f, cy + d * 0.6f, sd, rgb, erase);

    static uint SunCol => light ? 0xE8A100u : 0xFFC533u;
    static uint MoonCol => light ? 0x5E6B8Cu : 0xDADDEAu;
    static uint CloudCol => light ? 0x8E96A3u : 0xCDD2DAu;
    static uint DarkCloudCol => light ? 0x6F7885u : 0xA3ABB8u;
    static uint RainCol => light ? 0x2F86E0u : 0x5BB0FFu;
    static uint SnowCol => light ? 0x6FA8DEu : 0xFFFFFFu;
    public static uint RainColor(bool lightTheme) => lightTheme ? 0x2F86E0u : 0x5BB0FFu;

    /// <summary>Draws one weather icon centred on (centerX, centerY) into the current Gfx target.</summary>
    public static void Draw(Sky sky, bool night, float centerX, float centerY, float size, bool lightTheme)
    {
        cx = centerX; cy = centerY; d = size; light = lightTheme;
        switch (sky)
        {
            case Sky.Clear:
                if (night) Moon(0, 0, 1f); else Sun(0, 0, 1f);
                break;
            case Sky.PartlyCloudy:
                if (night) Moon(0.15f, -0.15f, 0.68f); else Sun(0.16f, -0.15f, 0.66f);
                Cloud(-0.05f, 0.10f, 0.80f, CloudCol, gap: true);
                break;
            case Sky.Cloudy:
                Cloud(0, 0, 1f, CloudCol);
                break;
            case Sky.Fog:
                Cloud(0, -0.15f, 0.80f, CloudCol);
                Line(-0.32f, 0.22f, 0.32f, 0.22f, 0.04f, CloudCol);
                Line(-0.20f, 0.38f, 0.20f, 0.38f, 0.04f, CloudCol);
                break;
            case Sky.Rain:
                Cloud(0, -0.12f, 0.90f, DarkCloudCol);
                for (int i = 0; i < 3; i++) Line(-0.16f + 0.19f * i, 0.24f, -0.23f + 0.19f * i, 0.42f, 0.04f, RainCol);
                break;
            case Sky.Snow:
                Cloud(0, -0.12f, 0.90f, DarkCloudCol);
                for (int i = 0; i < 3; i++) Dot(-0.19f + 0.19f * i, 0.27f, 0.05f, SnowCol);
                for (int i = 0; i < 2; i++) Dot(-0.095f + 0.19f * i, 0.42f, 0.05f, SnowCol);
                break;
            case Sky.Thunder:
                Cloud(0, -0.14f, 0.90f, DarkCloudCol);
                Bolt();
                break;
        }
    }

    /// <summary>The "something is coming later today" mark: a filled disc with an exclamation mark.</summary>
    public static void Badge(float centerX, float centerY, float size, bool thunder, bool lightTheme)
    {
        cx = centerX; cy = centerY; d = size; light = lightTheme;
        Dot(0, 0, 0.5f, thunder ? 0xF0563Fu : 0xF5A524u);
        uint ink = thunder ? 0xFFFFFFu : 0x1A1A1Au;
        Line(0, -0.24f, 0, 0.05f, 0.085f, ink);
        Dot(0, 0.27f, 0.095f, ink);
    }

    static void Dot(float ux, float uy, float ur, uint rgb)
    {
        float x0 = X(ux), y0 = Y(uy), r = ur * d;
        Fill((x, y) => Gfx.Disc(x, y, x0, y0, r), rgb);
    }

    static void Line(float ax, float ay, float bx, float by, float ur, uint rgb)
    {
        float x0 = X(ax), y0 = Y(ay), x1 = X(bx), y1 = Y(by), r = Math.Max(ur * d, 0.6f);
        Fill((x, y) => Gfx.Capsule(x, y, x0, y0, x1, y1, r), rgb);
    }

    static void Sun(float ox, float oy, float k)
    {
        Dot(ox, oy, 0.21f * k, SunCol);
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.PI / 4, c = MathF.Cos(a), s = MathF.Sin(a);
            Line(ox + c * 0.33f * k, oy + s * 0.33f * k, ox + c * 0.45f * k, oy + s * 0.45f * k, 0.038f * k, SunCol);
        }
    }

    static void Moon(float ox, float oy, float k)
    {
        float x0 = X(ox - 0.03f * k), y0 = Y(oy + 0.02f * k), r = 0.37f * k * d;
        float x1 = X(ox + 0.15f * k), y1 = Y(oy - 0.12f * k), r1 = 0.31f * k * d;
        Fill((x, y) => Math.Max(Gfx.Disc(x, y, x0, y0, r), -Gfx.Disc(x, y, x1, y1, r1)), MoonCol);
    }

    /// <summary>A flat-bottomed cloud. With gap, a thin ring around it is cleared first so it stands off the sun.</summary>
    static void Cloud(float ox, float oy, float k, uint rgb, bool gap = false)
    {
        float ax = X(ox - 0.20f * k), bx = X(ox + 0.21f * k), by = Y(oy + 0.14f * k), br = 0.13f * k * d;
        float c1x = X(ox - 0.07f * k), c1y = Y(oy - 0.03f * k), c1r = 0.21f * k * d;
        float c2x = X(ox + 0.16f * k), c2y = Y(oy + 0.04f * k), c2r = 0.15f * k * d;
        float Sd(float x, float y) => Math.Min(Gfx.Capsule(x, y, ax, by, bx, by, br), Math.Min(Gfx.Disc(x, y, c1x, c1y, c1r), Gfx.Disc(x, y, c2x, c2y, c2r)));
        if (gap)
        {
            float g = Math.Max(1f, 0.06f * d);
            Fill((x, y) => Sd(x, y) - g, 0, erase: true);
        }
        Fill(Sd, rgb);
    }

    static void Bolt()
    {
        float[] u = [0.07f, 0.06f, -0.13f, 0.30f, -0.01f, 0.30f, -0.07f, 0.50f, 0.17f, 0.21f, 0.04f, 0.21f, 0.14f, 0.06f];
        var p = new float[u.Length];
        for (int i = 0; i < u.Length; i += 2) { p[i] = X(u[i]); p[i + 1] = Y(u[i + 1]); }
        float g = Math.Max(1f, 0.05f * d);
        Fill((x, y) => Gfx.Polygon(x, y, p) - g, 0, erase: true); // a dark edge where it crosses the cloud
        Fill((x, y) => Gfx.Polygon(x, y, p), SunCol);
    }
}
