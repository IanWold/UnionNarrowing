namespace UnionNarrowing.Tests;

/// <summary>
/// Declarations that several snippets use, to add after a snippet's own code:
/// <c>""" + Snippets.Result</c>.
/// The shared <c>Pet</c> union in <see cref="Verify"/> is always there;
/// these are opt-in, so a snippet only has what it needs.
/// </summary>
internal static class Snippets
{
    /// <summary>
    /// A union with a closed case type, <c>Error</c>, whose leaf types are cases too.
    /// </summary>
    public const string Result = """

        public record Ok;
        public closed record Error;
        public record NotFound : Error;
        public record Timeout : Error;
        public union Result(Ok, Error);
        """;

    /// <summary>
    /// A closed hierarchy of three shapes.
    /// </summary>
    public const string Shapes = """

        public closed record Shape;
        public record Circle(double Radius) : Shape;
        public record Square(double Side) : Shape;
        public record Triangle(double Base) : Shape;
        """;

    /// <summary>
    /// A union, <c>Young</c>, with a case that holds another union, <c>Toy</c>.
    /// </summary>
    public const string Toys = """

        public record Ball;
        public record Mouse;
        public union Toy(Ball, Mouse);
        public record Kitten(Toy Toy);
        public union Young(Kitten, Dog);
        """;
}
