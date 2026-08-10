using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RaceNetScraper.Shared.Scraping;

/// <summary>
/// Derives a human-readable silk colour description from a racing silk SVG image.
///
/// Some sources expose no textual silk description anywhere (not in their GraphQL/Apollo cache,
/// not in page HTML, not as SVG metadata) — only a "silkImageUrl" pointing at a flat two-tone
/// SVG made of unlabeled &lt;path&gt; shapes. This class approximates a description by:
///   1. Reading each path's fill colour and its bounding box (as a fraction of the SVG's own
///      viewBox, so it works regardless of the SVG's actual pixel dimensions).
///   2. Classifying shapes into cap / body / sleeves / armbands purely by position and
///      relative size (the largest non-cap shape is the body; small shapes are armbands;
///      the rest are sleeves).
///   3. Matching each region's fill hex to the closest named racing-silk colour.
///
/// This is a best-effort approximation, not an official silk description: it can identify
/// which named colour sits in which garment region, but it cannot detect patterns (stars,
/// spots, hoops, checks, quarters) since those require pixel-level shape analysis this does
/// not attempt.
/// </summary>
public static class SilkSvgDescriber
{
    private static readonly HttpClient Http = new();

    private static readonly (string Name, byte R, byte G, byte B)[] Palette =
    {
        ("White", 255, 255, 255),
        ("Black", 0, 0, 0),
        ("Grey", 150, 150, 150),
        ("Silver", 200, 200, 200),
        ("Red", 220, 20, 20),
        ("Maroon", 128, 0, 0),
        ("Pink", 255, 150, 200),
        ("Cerise", 220, 20, 140),
        ("Orange", 255, 140, 0),
        ("Gold", 210, 170, 0),
        ("Yellow", 255, 220, 0),
        ("Brown", 130, 80, 40),
        ("Emerald Green", 0, 140, 70),
        ("Lime Green", 130, 200, 60),
        ("Dark Green", 0, 80, 40),
        ("Royal Blue", 0, 60, 200),
        ("Navy Blue", 0, 0, 90),
        ("Light Blue", 135, 206, 235),
        ("Turquoise", 0, 190, 190),
        ("Purple", 130, 0, 160),
    };

    /// <summary>Fetches and describes a silk SVG. Returns null on any failure — this is
    /// supplementary enrichment, never critical to a scrape succeeding.</summary>
    public static async Task<string?> DescribeAsync(string silkImageUrl)
    {
        try
        {
            var svg = await Http.GetStringAsync(silkImageUrl);
            var root = XDocument.Parse(svg).Root;
            if (root is null) return null;

            var ns = root.Name.Namespace;
            var viewBoxParts = (root.Attribute("viewBox")?.Value ?? "0 0 100 100")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var width = double.Parse(viewBoxParts[2], CultureInfo.InvariantCulture);
            var height = double.Parse(viewBoxParts[3], CultureInfo.InvariantCulture);

            var shapes = new List<Shape>();
            foreach (var path in root.Descendants(ns + "path"))
            {
                var fill = path.Attribute("fill")?.Value;
                var d = path.Attribute("d")?.Value;
                if (string.IsNullOrEmpty(fill) || fill == "none" || string.IsNullOrEmpty(d)) continue;

                var box = BoundingBoxOf(d);
                if (box is null) continue;

                shapes.Add(new Shape(fill, box.Value.MinX / width, box.Value.MaxX / width,
                    box.Value.MinY / height, box.Value.MaxY / height));
            }

            return shapes.Count > 0 ? Describe(shapes) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Describe(List<Shape> shapes)
    {
        var capShapes = shapes.Where(s => s.MidY < 0.25).ToList();
        var remaining = shapes.Except(capShapes).ToList();

        if (remaining.Count == 0)
            return ColourNameOf(DominantFill(shapes));

        var body = remaining.OrderByDescending(s => s.Area).First();
        var others = remaining.Where(s => s != body).ToList();

        // An armband is a small contrasting patch layered on top of a larger shape of a
        // different colour (typically a sleeve) — not just "the smaller half" of the
        // remaining shapes, which lumps genuinely same-sized sleeve/cuff pairs apart.
        var armbandShapes = new List<Shape>();
        var sleeveShapes = new List<Shape>();
        foreach (var s in others)
        {
            var isAccent = others.Any(o => o != s && o.Fill != s.Fill && o.Area > s.Area * 1.8 && Overlaps(o, s));
            (isAccent ? armbandShapes : sleeveShapes).Add(s);
        }

        var bodyColour = ColourNameOf(body.Fill);
        var parts = new List<string> { bodyColour };

        AppendIfDistinct(parts, sleeveShapes, bodyColour, "sleeves");
        AppendIfDistinct(parts, armbandShapes, bodyColour, "armbands");
        AppendIfDistinct(parts, capShapes, bodyColour, "cap");

        return string.Join(", ", parts);
    }

    private static bool Overlaps(Shape a, Shape b) =>
        a.MinX < b.MaxX && a.MaxX > b.MinX && a.MinY < b.MaxY && a.MaxY > b.MinY;

    private static void AppendIfDistinct(List<string> parts, List<Shape> group, string bodyColour, string label)
    {
        if (group.Count == 0) return;
        var colour = ColourNameOf(DominantFill(group));
        if (colour != bodyColour)
            parts.Add($"{colour} {label}");
    }

    private static string DominantFill(List<Shape> group) =>
        group.GroupBy(s => s.Fill).OrderByDescending(g => g.Count()).First().Key;

    private static string ColourNameOf(string hex)
    {
        if (!TryParseHex(hex, out var r, out var g, out var b)) return hex;

        var best = Palette[0];
        var bestDist = double.MaxValue;
        foreach (var candidate in Palette)
        {
            var dist = Math.Pow(r - candidate.R, 2) + Math.Pow(g - candidate.G, 2) + Math.Pow(b - candidate.B, 2);
            if (dist < bestDist) { bestDist = dist; best = candidate; }
        }
        return best.Name;
    }

    private static bool TryParseHex(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (!hex.StartsWith('#') || hex.Length < 7) return false;
        return byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    private static (double MinX, double MaxX, double MinY, double MaxY)? BoundingBoxOf(string d)
    {
        var numbers = Regex.Matches(d, @"-?\d+(?:\.\d+)?")
            .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
            .ToList();
        if (numbers.Count < 2) return null;

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (var i = 0; i + 1 < numbers.Count; i += 2)
        {
            var (x, y) = (numbers[i], numbers[i + 1]);
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return (minX, maxX, minY, maxY);
    }

    private sealed class Shape
    {
        public string Fill { get; }
        public double MinX { get; }
        public double MaxX { get; }
        public double MinY { get; }
        public double MaxY { get; }
        public double MidY { get; }
        public double Area { get; }

        public Shape(string fill, double minX, double maxX, double minY, double maxY)
        {
            Fill = fill;
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
            MidY = (minY + maxY) / 2;
            Area = Math.Max(maxX - minX, 0.001) * Math.Max(maxY - minY, 0.001);
        }
    }
}
