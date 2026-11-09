namespace UnionNarrowing.Tests;

/// <summary>
/// CS8655 and CS8847:
/// the switch handles every case type but not null, which earlier code rules out in a way the compiler's null-state tracking doesn't follow.
/// </summary>
public class NullTests
{
    private const string Shapes = """

        public closed record Shape;
        public record Circle(double Radius) : Shape;
        public record Square(double Side) : Shape;
        union MaybeCat(Cat?, Dog);
        """;

    [Fact]
    public Task Coalescing_with_a_throw_rules_out_no_value_but_not_a_null_case() =>
        Verify.Suppressions("""
            class C
            {
                int M(MaybeCat? maybe) => (maybe ?? throw new InvalidOperationException()) /*warns*/switch { Cat => 1, Dog => 2 };
            }
            public union MaybeCat(Cat?, Dog);
            """, diagnosticId: "CS8655"
        );

    [Fact]
    public Task Null_filtered_out_by_where() =>
        Verify.Suppressions("""
            class C
            {
                IEnumerable<int> M(IEnumerable<Shape?> shapes) =>
                    shapes.Where(s => s is not null).Select(s => s /*suppressed*/switch { Circle => 1, Square => 2 });

                IEnumerable<int> N(IEnumerable<MaybeCat> cats) =>
                    cats.Where(c => c is not null).Select(c => c /*suppressed*/switch { Cat => 1, Dog => 2 });

                IEnumerable<int> O(IEnumerable<Pet?> pets) =>
                    pets.Where(p => p is not null).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2, Bird => 3 });

                // Nothing rules null out.
                IEnumerable<int> P(IEnumerable<Shape?> shapes) =>
                    shapes.Where(s => s is not Circle).Select(s => s /*warns*/switch { Circle => 1, Square => 2 });
            }
            """ + Shapes, diagnosticId: "CS8655"
        );

    [Fact]
    public Task Null_ruled_out_by_every_caller() =>
        Verify.Suppressions("""
            class C
            {
                int M(Shape? shape) => shape is null ? 0 : Area(shape);

                int N(Shape shape) => Area(shape);

                private static int Area(Shape? shape) => shape /*suppressed*/switch { Circle => 1, Square => 2 };

                int O(Shape? shape) => Perimeter(shape);

                private static int Perimeter(Shape? shape) => shape /*warns*/switch { Circle => 1, Square => 2 };
            }
            """ + Shapes, diagnosticId: "CS8655"
        );

    [Fact]
    public Task Null_ruled_out_for_the_source_of_a_copy() =>
        Verify.Suppressions("""
            class C
            {
                int M(Shape? shape)
                {
                    var copy = shape;
                    if (shape is null) return 0;
                    return copy /*suppressed*/switch { Circle => 1, Square => 2 };
                }

                int N(MaybeCat cat)
                {
                    var copy = cat;
                    if (cat is null) return 0;
                    return copy /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """ + Shapes, diagnosticId: "CS8655"
        );

    [Fact]
    public Task Null_ruled_out_with_a_when_arm() =>
        Verify.Suppressions("""
            class C
            {
                IEnumerable<int> M(IEnumerable<Shape?> shapes, bool flag) =>
                    shapes.Where(s => s is not null).Select(s => s /*suppressed*/switch { Circle => 1, Square => 2, _ when flag => 3 });

                IEnumerable<int> N(IEnumerable<Shape?> shapes, bool flag) =>
                    shapes.Select(s => s /*warns*/switch { Circle => 1, Square => 2, _ when flag => 3 });
            }
            """ + Shapes, diagnosticId: "CS8847"
        );
}
