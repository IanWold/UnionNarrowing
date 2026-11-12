using UnionNarrowing.Suppressors.Analyzers;
using UnionNarrowing.CodeFixes;

namespace UnionNarrowing.Tests;

/// <summary>
/// Override ToString in a union declared in the solution, for UN0015 and UN0019.
/// </summary>
public class OverrideToStringCodeFixTests
{
    [Fact]
    public Task Fix_all() =>
        Verify.FixAll(new OverrideToStringCodeFix(), """
            class C
            {
                string M(Shape shape, Shape other) => $"{shape} and {other}";

                string N(Fill fill) => "fill: " + fill;
            }

            public record Owner(Shape Shape);
            public record Circle;
            public record Square;
            public union Shape(Circle, Square);
            public union Fill(Circle, Square);
            """, """
            class C
            {
                string M(Shape shape, Shape other) => $"{shape} and {other}";

                string N(Fill fill) => "fill: " + fill;
            }

            public record Owner(Shape Shape);
            public record Circle;
            public record Square;
            public union Shape(Circle, Square)
            {
                public override string ToString() => Value?.ToString() ?? "";
            }
            public union Fill(Circle, Square)
            {
                public override string ToString() => Value?.ToString() ?? "";
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Override_ToString_in_a_generic_union() =>
        Verify.CodeFix(new OverrideToStringCodeFix(), """
            class C
            {
                string M(Option<int> option) => option.ToString();
            }

            public record Some<T>(T Item);
            public record None;
            public union Option<T>(Some<T>, None);
            """, """
            class C
            {
                string M(Option<int> option) => option.ToString();
            }

            public record Some<T>(T Item);
            public record None;
            public union Option<T>(Some<T>, None)
            {
                public override string ToString() => Value?.ToString() ?? "";
            }
            """, new BoxedUnionAnalyzer(), title: "Override ToString in 'Option'"
        );

    [Fact]
    public Task Override_ToString_in_a_union_a_record_prints() =>
        Verify.CodeFix(new OverrideToStringCodeFix(), """
            public record Owner(Shape Shape);
            public record Circle;
            public record Square;
            public union Shape(Circle, Square);
            """, """
            public record Owner(Shape Shape);
            public record Circle;
            public record Square;
            public union Shape(Circle, Square)
            {
                public override string ToString() => Value?.ToString() ?? "";
            }
            """, new BoxedUnionAnalyzer(), title: "Override ToString in 'Shape'"
        );

    [Fact]
    public Task Override_ToString_in_a_union_with_a_body() =>
        Verify.CodeFix(new OverrideToStringCodeFix(), """
            class C
            {
                string M(List<Shape> shapes) => string.Join(", ", shapes);
            }

            public record Circle;
            public record Square;
            public union Shape(Circle, Square)
            {
                public bool IsRound => Value is Circle;
            }
            """, """
            class C
            {
                string M(List<Shape> shapes) => string.Join(", ", shapes);
            }

            public record Circle;
            public record Square;
            public union Shape(Circle, Square)
            {
                public bool IsRound => Value is Circle;
                public override string ToString() => Value?.ToString() ?? "";
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Override_ToString_not_offered_for_a_union_from_another_assembly() =>
        Verify.CodeFix(new OverrideToStringCodeFix(), """
            class C
            {
                string M(Shape shape) => $"{shape}";
            }
            """, expected: null, new BoxedUnionAnalyzer(), references: [Verify.CompileToMetadata("""
                public record Circle;
                public record Square;
                public union Shape(Circle, Square);
                """)]
        );

    [Fact]
    public Task Override_ToString_not_offered_where_the_union_hides_ToString() =>
        Verify.CodeFix(new OverrideToStringCodeFix(), """
            class C
            {
                string M(Shape shape) => $"{shape}";
            }

            public record Circle;
            public record Square;
            public union Shape(Circle, Square)
            {
                public new string ToString() => "shape";
            }
            """, expected: null, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Override_ToString_where_the_union_is_passed_as_itself_to_be_formatted() =>
        Verify.CodeFix(new OverrideToStringCodeFix(), """
            using Microsoft.Extensions.Logging;

            partial class C
            {
                [LoggerMessage(Level = LogLevel.Information, Message = "Order {Id} is {Status}")]
                static partial void LogStatusUpdated(ILogger logger, int id, Status status);

                void M(ILogger logger, int id, Status status) => LogStatusUpdated(logger, id, status);
            }

            public record Pending;
            public record Shipped;
            public union Status(Pending, Shipped);
            """, """
            using Microsoft.Extensions.Logging;

            partial class C
            {
                [LoggerMessage(Level = LogLevel.Information, Message = "Order {Id} is {Status}")]
                static partial void LogStatusUpdated(ILogger logger, int id, Status status);

                void M(ILogger logger, int id, Status status) => LogStatusUpdated(logger, id, status);
            }

            public record Pending;
            public record Shipped;
            public union Status(Pending, Shipped)
            {
                public override string ToString() => Value?.ToString() ?? "";
            }
            """, new BoxedUnionAnalyzer()
        );
}
