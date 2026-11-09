namespace UnionNarrowing.Sample;

public record Circle(double Radius);
public record Rectangle(double Width, double Height);
public record Polygon(IReadOnlyList<(double X, double Y)> Points);

public union Shape(Circle, Rectangle, Polygon);

public record Unlabelled;
public union Label(string?, Unlabelled);

public static class Geometry
{
    private static double Shoelace(IReadOnlyList<(double X, double Y)> points)
    {
        var sum = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var (x1, y1) = points[i];
            var (x2, y2) = points[(i + 1) % points.Count];
            sum += x1 * y2 - x2 * y1;
        }

        return Math.Abs(sum) / 2;
    }

    public static double Area(Shape shape)
    {
        if (shape is Polygon polygon)
        {
            return Shoelace(polygon.Points);
        }

        return shape switch
        {
            Circle c => Math.PI * c.Radius * c.Radius,
            Rectangle r => r.Width * r.Height,
        };
    }

    public static string Describe(IEnumerable<Shape> shapes)
    {
        var parts = new List<string>();
        foreach (var shape in shapes)
        {
            if (shape is Polygon { Points.Count: < 3 } or Circle { Radius: <= 0 })
            {
                continue;
            }

            if (shape is Polygon || shape is Circle)
            {
                throw new NotSupportedException("Only rectangles are described.");
            }

            parts.Add(shape switch { Rectangle r => $"{r.Width}x{r.Height}" });
        }

        return string.Join(", ", parts);
    }

    public static int LabelLength(Label label)
    {
        // `string?` lets the union hold null, so null has to be ruled out too.
        if (label is Unlabelled or null)
        {
            return 0;
        }

        return label switch { string text => text.Length };
    }
}
