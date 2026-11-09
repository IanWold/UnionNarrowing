namespace UnionNarrowing.Tests;

/// <summary>
/// Closed class hierarchies get the same treatment as unions.
/// </summary>
public class ClosedTests
{
    private const string Shapes = """

        public closed record Shape;
        public record Circle(double Radius) : Shape;
        public closed record Polygon : Shape;
        public record Square(double Side) : Polygon;
        public record Triangle(double Base, double Height) : Polygon;
        """;

    [Fact]
    public Task Generic_closed_hierarchy() =>
        Verify.Suppressions("""
            public closed record Result<T>;
            public record Ok<T>(T Value) : Result<T>;
            public record Error<T>(string Message) : Result<T>;

            class C
            {
                int M(Result<int> result)
                {
                    if (result is Error<int>) return -1;
                    return result /*suppressed*/switch { Ok<int> ok => ok.Value };
                }

                int N<T>(Result<T> result)
                {
                    if (result is Error<T>) return -1;
                    return result /*suppressed*/switch { Ok<T> => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_then_switch() =>
        Verify.Suppressions("""
            class C
            {
                double M(Shape shape)
                {
                    if (shape is Circle c) return Math.PI * c.Radius * c.Radius;
                    return shape /*suppressed*/switch { Polygon => 0 };
                }
            }
            """ + Shapes
        );

    [Fact]
    public Task Nested_closed_types_are_expanded() =>
        Verify.Suppressions("""
            class C
            {
                double M(Shape shape)
                {
                    if (shape is Square) return 1;
                    return shape /*suppressed*/switch { Circle => 2, Triangle => 3 };
                }

                double N(Shape shape)
                {
                    if (shape is Polygon) return 1;
                    return shape /*suppressed*/switch { Circle => 2 };
                }
            }
            """ + Shapes
        );

    [Fact]
    public Task Nullable_reference_needs_null_handled() =>
        Verify.Suppressions("""
            class C
            {
                double M(Shape? shape)
                {
                    if (shape is Circle) return 1;
                    return shape /*warns*/switch { Polygon => 2 };
                }

                double N(Shape? shape)
                {
                    if (shape is Circle or null) return 1;
                    return shape /*suppressed*/switch { Polygon => 2 };
                }
            }
            """ + Shapes
        );

    [Fact]
    public Task Switch_over_this() =>
        Verify.Suppressions("""
            public closed record Expr
            {
                public int Evaluate()
                {
                    if (this is Literal literal) return literal.Value;
                    return this /*suppressed*/switch { Sum sum => sum.Left.Evaluate() + sum.Right.Evaluate() };
                }
            }
            public record Literal(int Value) : Expr;
            public record Sum(Expr Left, Expr Right) : Expr;
            """
        );
}
