namespace UnionNarrowing.Tests;

/// <summary>
/// Switches over a bool or an enum, narrowed by earlier tests the same way as a union.
/// </summary>
public class EnumAndBoolTests
{
    private const string Color = """

        enum Color { Red, Green, Blue }
        """;

    [Fact]
    public Task Bool() =>
        Verify.Suppressions("""
            class C
            {
                int M(bool b)
                {
                    if (b) return 0;
                    return b /*suppressed*/switch { false => 1 };
                }

                int N(bool? b)
                {
                    if (b is null || b == false) return 0;
                    return b /*suppressed*/switch { true => 1 };
                }

                // Could be null.
                int O(bool? b)
                {
                    if (b == false) return 0;
                    return b /*warns*/switch { true => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Enum_switch_statement_left_to_the_ide() =>
        Verify.Suppressions("""
            class C
            {
                void M(Color c)
                {
                    if (c == Color.Red) return;
                    /*warns*/switch (c)
                    {
                        case Color.Green: break;
                        case Color.Blue: break;
                    }
                }
            }
            """ + Color, diagnosticId: "IDE0010", disabledWarnings: ["CS8524"]
        );

    [Fact]
    public Task Enum_where_values_no_member_has_are_ignored() =>
        Verify.Suppressions("""
            class C
            {
                int M(Color c)
                {
                    if (c == Color.Red) return 0;
                    return c /*suppressed*/switch { Color.Green => 2, Color.Blue => 3 };
                }

                int N(Color c)
                {
                    if (c is Color.Red or Color.Blue) return 0;
                    return c /*suppressed*/switch { Color.Green => 2 };
                }

                int O(Color c)
                {
                    if (c != Color.Red) return 0;
                    return c /*suppressed*/switch { Color.Red => 1 };
                }

                int P(Color c)
                {
                    if (c > Color.Green) return 0;
                    return c /*suppressed*/switch { Color.Red => 1, Color.Green => 2 };
                }

                int Q(Color c)
                {
                    if (c == Color.Red) return 0;
                    return c /*warns*/switch { Color.Green => 2 };
                }
            }
            """ + Color, disabledWarnings: ["CS8524"]
        );

    /// <summary>
    /// With CS8524 on, an enum switch has to handle values no member has, which only a discard does, and then there's no CS8509 to suppress.
    /// Suppressing it would hide CS8524.
    /// </summary>
    [Fact]
    public Task Enum_where_values_no_member_has_matter() =>
        Verify.Suppressions("""
            class C
            {
                int M(Color c)
                {
                    if (c == Color.Red) return 0;
                    return c /*warns*/switch { Color.Green => 2, Color.Blue => 3 };
                }
            }
            """ + Color
        );
}
