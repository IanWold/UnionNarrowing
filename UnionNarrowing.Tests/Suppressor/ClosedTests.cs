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
    public Task Discriminant_property() =>
        Verify.Suppressions("""
            enum ShapeKind { Circle, Square, Triangle }

            closed record Shape
            {
                public abstract ShapeKind Kind { get; }
                public abstract string Label { get; }
            }

            sealed record Circle(double Radius) : Shape
            {
                public override ShapeKind Kind => ShapeKind.Circle;
                public override string Label => "circle";
            }

            sealed record Square(double Side) : Shape
            {
                public override ShapeKind Kind { get { return ShapeKind.Square; } }
                public override string Label => Side > 1 ? "big" : "small";
            }

            sealed record Triangle(double Base) : Shape
            {
                public override ShapeKind Kind => ShapeKind.Triangle;
                public override string Label => "triangle";
            }

            class C
            {
                int Equality(Shape s)
                {
                    if (s.Kind == ShapeKind.Circle) return 0;
                    return s /*suppressed*/switch { Square => 1, Triangle => 2 };
                }

                int Pattern(Shape s)
                {
                    if (s.Kind is ShapeKind.Circle or ShapeKind.Square) return 0;
                    return s /*suppressed*/switch { Triangle => 1 };
                }

                int PropertyPattern(Shape s)
                {
                    if (s is { Kind: ShapeKind.Triangle }) return 0;
                    return s /*suppressed*/switch { Circle => 1, Square => 2 };
                }

                int SwitchStatement(Shape s)
                {
                    switch (s.Kind)
                    {
                        case ShapeKind.Circle:
                            return 0;
                        case ShapeKind.Square:
                            return 1;
                    }

                    return s /*suppressed*/switch { Triangle => 2 };
                }

                int NotEveryCaseHasAConstant(Shape s)
                {
                    if (s.Label == "big") return 0;
                    return s /*warns*/switch { Circle => 1, Triangle => 2 };
                }

                int OtherValue(Shape s)
                {
                    if (s.Kind != ShapeKind.Circle) return 0;
                    return s /*suppressed*/switch { Circle => 1 };
                }

                int WrongCase(Shape s)
                {
                    if (s.Kind == ShapeKind.Circle) return 0;
                    return s /*warns*/switch { Square => 1 };
                }
            }
            """
        );


    [Fact]
    public Task Members_that_are_not_discriminants() =>
        Verify.Suppressions("""
            closed record Shape
            {
                public abstract double Weight { get; }
                public abstract object Tag { get; }
                public abstract double Ratio { get; }
                public abstract int Sides { get; }
            }

            sealed record Circle : Shape
            {
                public override double Weight => 1;
                public override object Tag => 1;
                public override double Ratio => double.NaN;
                public override int Sides => 0;
            }

            sealed record Square : Shape
            {
                public override double Weight => 2;
                public override object Tag => 2;
                public override double Ratio => 1.0;
                public override int Sides => 4;
            }

            class C
            {
                int ConvertedConstant(Shape s)
                {
                    if (s.Weight == 1.0) return s /*warns*/switch { Square => 0 };
                    return 1;
                }

                int Boxed(Shape s)
                {
                    if (s is { Tag: 1L }) return 0;
                    return s /*warns*/switch { Square => 2 };
                }

                int NotANumber(Shape s)
                {
                    if (s.Ratio == double.NaN) return 0;
                    return s /*warns*/switch { Square => 1 };
                }

                int ComparedAsADouble(Shape s)
                {
                    if (s.Sides == 4.0) return s /*warns*/switch { Circle => 1 };
                    return 0;
                }
            }
            """
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
