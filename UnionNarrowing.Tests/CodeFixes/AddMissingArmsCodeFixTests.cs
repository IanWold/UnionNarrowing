using UnionNarrowing.CodeFixes;

namespace UnionNarrowing.Tests;

/// <summary>
/// Add missing arms, for CS8509, CS8846, CS8655 and CS8847.
/// </summary>
public class AddMissingArmsCodeFixTests
{
    private const string Nested = Snippets.Toys + """

        public record Rock;
        public union Thing(Pet, Rock);
        public record Owner(Pet Pet);
        """;

    /// <summary>
    /// The snippet with Windows line breaks, indented with tabs.
    /// </summary>
    private static string WithCrLfAndTabs(string text) =>
        text.ReplaceLineEndings("\r\n").Replace("    ", "\t");

    [Fact]
    public Task Add_missing_arms_after_a_comment_ending_the_last_arm() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch
                {
                    Cat => 1, // meow
                };
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch
                {
                    Cat => 1, // meow
                    Dog => throw new NotImplementedException(),
                    Bird => throw new NotImplementedException(),
                };
            }
            """
        );

    [Fact]
    public Task Add_missing_arms_after_a_comment_ending_the_last_arm_without_a_trailing_comma() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch
                {
                    Cat => 1 // meow
                };
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch
                {
                    Cat => 1, // meow
                    Dog => throw new NotImplementedException(),
                    Bird => throw new NotImplementedException()
                };
            }
            """
        );

    [Fact]
    public Task Add_missing_arms_alongside_when_arms() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet, bool flag) => pet switch { Cat => 1, _ when flag => 2 };
            }
            """, """
            class C
            {
                int M(Pet pet, bool flag) => pet switch { Cat => 1, _ when flag => 2, Dog => throw new NotImplementedException(), Bird => throw new NotImplementedException() };
            }
            """
        );

    [Fact]
    public Task Add_missing_arms_before_a_brace_on_the_last_arm_s_line() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch {
                    Cat => 1,
                    Dog => 2 };
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch {
                    Cat => 1,
                    Dog => 2,
                    Bird => throw new NotImplementedException() };
            }
            """
        );

    [Fact]
    public Task Add_missing_arms_below_a_comment_above_the_first_arm() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch
                {

                    // Cats first
                    Cat => 1,
                };
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch
                {

                    // Cats first
                    Cat => 1,
                    Dog => throw new NotImplementedException(),
                    Bird => throw new NotImplementedException(),
                };
            }
            """
        );

    [Fact]
    public Task Add_missing_arms_for_a_closed_hierarchy() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            public closed record Shape;
            public record Circle : Shape;
            public closed record Polygon : Shape;
            public record Square : Polygon;
            public record Triangle : Polygon;

            class C
            {
                int M(Shape shape) => shape switch { Square => 1 };
            }
            """, """
            public closed record Shape;
            public record Circle : Shape;
            public closed record Polygon : Shape;
            public record Square : Polygon;
            public record Triangle : Polygon;

            class C
            {
                int M(Shape shape) => shape switch { Square => 1, Circle => throw new NotImplementedException(), Triangle => throw new NotImplementedException() };
            }
            """
        );

    [Fact]
    public Task Add_missing_arms_for_a_member_of_the_object() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    return owner switch { { Pet: Cat } => 1 };
                }
            }
            """ + Nested, """
            class C
            {
                int M(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    return owner switch { { Pet: Cat } => 1, { Pet: Dog } => throw new NotImplementedException() };
                }
            }
            """ + Nested
        );

    [Fact]
    public Task Add_missing_arms_for_a_tuple() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet a, Pet b)
                {
                    if (a is Bird) return 0;
                    return (a, b) switch { (Cat, _) => 1 };
                }
            }
            """, """
            class C
            {
                int M(Pet a, Pet b)
                {
                    if (a is Bird) return 0;
                    return (a, b) switch { (Cat, _) => 1, (Dog, _) => throw new NotImplementedException() };
                }
            }
            """
        );

    [Fact]
    public Task Add_missing_arms_for_a_tuple_in_one_arm_per_element_that_differs() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Scalar left, Scalar right)
                {
                    if (left is bool) return 0;
                    return (left, right) switch { (long, long) => 1, (string, string) => 2 };
                }
            }

            public union Scalar(long, bool, string);
            """, """
            class C
            {
                int M(Scalar left, Scalar right)
                {
                    if (left is bool) return 0;
                    return (left, right) switch { (long, long) => 1, (string, string) => 2, (long, bool or string) => throw new NotImplementedException(), (string, long or bool) => throw new NotImplementedException() };
                }
            }

            public union Scalar(long, bool, string);
            """
        );

    [Fact]
    public Task Add_missing_arms_for_a_tuple_with_a_bool() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet, bool flag)
                {
                    if (pet is Bird) return 0;
                    return (pet, flag) switch { (Cat, true) => 1 };
                }
            }
            """, """
            class C
            {
                int M(Pet pet, bool flag)
                {
                    if (pet is Bird) return 0;
                    return (pet, flag) switch { (Cat, true) => 1, (Cat, false) => throw new NotImplementedException(), (Dog, _) => throw new NotImplementedException() };
                }
            }
            """
        );

    [Fact]
    public Task Add_missing_arms_for_case_types_a_pattern_names_differently() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Mixed mixed) => mixed switch { int => 1 };
            }

            public union Mixed(int, (int, string), char?, int?[]);
            """, """
            class C
            {
                int M(Mixed mixed) => mixed switch { int => 1, ValueTuple<int, string> => throw new NotImplementedException(), char => throw new NotImplementedException(), int?[] => throw new NotImplementedException(), null => throw new NotImplementedException() };
            }

            public union Mixed(int, (int, string), char?, int?[]);
            """
        );

    [Fact]
    public Task Add_missing_arms_for_part_of_a_bool_case_type() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Setting s) => s switch { true => 1 };
            }
            public union Setting(bool, string);
            """, """
            class C
            {
                int M(Setting s) => s switch { true => 1, false => throw new NotImplementedException(), string => throw new NotImplementedException() };
            }
            public union Setting(bool, string);
            """
        );

    [Fact]
    public Task Add_missing_arms_for_part_of_a_closed_case_type() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Result r) => r switch { Ok => 1, NotFound => 2 };
            }
            """ + Snippets.Result, """
            class C
            {
                int M(Result r) => r switch { Ok => 1, NotFound => 2, Timeout => throw new NotImplementedException() };
            }
            """ + Snippets.Result
        );

    [Fact]
    public Task Add_missing_arms_for_the_cases_when_too_much_is_missing_inside_them() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Group group) => group switch { Trio { A: Cat, B: Cat, C: Cat } => 1, Rock => 2 };
            }

            public record Trio(Pet A, Pet B, Pet C);
            public record Rock;
            public union Group(Trio, Rock);
            """, """
            class C
            {
                int M(Group group) => group switch { Trio { A: Cat, B: Cat, C: Cat } => 1, Rock => 2, Trio => throw new NotImplementedException() };
            }

            public record Trio(Pet A, Pet B, Pet C);
            public record Rock;
            public union Group(Trio, Rock);
            """
        );

    [Fact]
    public Task Add_missing_arms_for_the_union_a_case_holds() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Thing t) => t switch { Pet and (Cat or Dog) => 1, Rock => 2 };
            }
            """ + Nested, """
            class C
            {
                int M(Thing t) => t switch { Pet and (Cat or Dog) => 1, Rock => 2, Pet and Bird => throw new NotImplementedException() };
            }
            """ + Nested
        );

    [Fact]
    public Task Add_missing_arms_for_the_values_of_an_enum_no_member_has() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet, Color color) => (pet, color) switch { (Cat, Color.Red) => 1, (Cat, Color.Green) => 2, (Dog, _) => 3 };
            }

            public enum Color { Red, Green }
            """, """
            class C
            {
                int M(Pet pet, Color color) => (pet, color) switch { (Cat, Color.Red) => 1, (Cat, Color.Green) => 2, (Dog, _) => 3, (Cat, not (Color.Red or Color.Green)) => throw new NotImplementedException(), (Bird, _) => throw new NotImplementedException() };
            }

            public enum Color { Red, Green }
            """
        );

    [Fact]
    public Task Add_missing_arms_includes_null_where_it_matters() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            union MaybeCat(Cat?, Dog);

            class C
            {
                int M(MaybeCat cat) => cat switch { Cat => 1 };
            }
            """, """
            union MaybeCat(Cat?, Dog);

            class C
            {
                int M(MaybeCat cat) => cat switch { Cat => 1, Dog => throw new NotImplementedException(), null => throw new NotImplementedException() };
            }
            """
        );

    [Fact]
    public Task Add_missing_arms_inline() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1 };
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, Dog => throw new NotImplementedException(), Bird => throw new NotImplementedException() };
            }
            """
        );

    [Fact]
    public Task Add_missing_arms_inside_a_case() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Young y) => y switch { Kitten { Toy: Mouse } => 1, Dog => 2 };
            }
            """ + Nested, """
            class C
            {
                int M(Young y) => y switch { Kitten { Toy: Mouse } => 1, Dog => 2, Kitten { Toy: Ball } => throw new NotImplementedException() };
            }
            """ + Nested
        );

    [Fact]
    public Task Add_missing_arms_inside_a_case_qualifies_a_case_type_a_member_of_its_name_hides() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                public Dog Dog { get; } = new("Rex");

                int M(Young y) => y switch { Kitten { Toy: Mouse } => 1 };
            }
            """ + Nested, """
            class C
            {
                public Dog Dog { get; } = new("Rex");

                int M(Young y) => y switch { Kitten { Toy: Mouse } => 1, Kitten { Toy: Ball } => throw new NotImplementedException(), global::Dog => throw new NotImplementedException() };
            }
            """ + Nested
        );

    [Fact]
    public Task Add_missing_arms_names_a_case_type_whose_parts_are_all_missing() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Result r) => r switch { Ok => 1 };
            }
            """ + Snippets.Result, """
            class C
            {
                int M(Result r) => r switch { Ok => 1, Error => throw new NotImplementedException() };
            }
            """ + Snippets.Result
        );

    [Fact]
    public Task Add_missing_arms_not_offered_for_other_types() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(object value) => value switch { int => 1 };
            }
            """, expected: null
        );

    [Fact]
    public Task Add_missing_arms_not_offered_with_arms_in_an_inactive_if() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch
                {
                    Cat => 1,
            #if DEBUG
                    Dog => 2,
            #endif
                };
            }
            """, expected: null
        );

    [Fact]
    public Task Add_missing_arms_one_per_line() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch
                {
                    Cat => 1,
                };

                int N(Pet pet) => pet switch { Cat => 1, Dog => 2, Bird => 3 };
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch
                {
                    Cat => 1,
                    Dog => throw new NotImplementedException(),
                    Bird => throw new NotImplementedException(),
                };

                int N(Pet pet) => pet switch { Cat => 1, Dog => 2, Bird => 3 };
            }
            """
        );

    [Fact]
    public Task Add_missing_arms_qualifies_a_case_type_a_member_of_its_name_hides() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                public Failure Failure { get; } = new("x");

                int M(Outcome outcome) => outcome switch { Success => 1 };
            }

            public record Success;
            public record Failure(string Reason);
            public union Outcome(Success, Failure);
            """, """
            class C
            {
                public Failure Failure { get; } = new("x");

                int M(Outcome outcome) => outcome switch { Success => 1, global::Failure => throw new NotImplementedException() };
            }

            public record Success;
            public record Failure(string Reason);
            public union Outcome(Success, Failure);
            """
        );

    [Fact]
    public Task Add_missing_arms_skips_what_earlier_code_ruled_out() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat c when c.Name == "Tom" => 1 };
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat c when c.Name == "Tom" => 1, Cat => throw new NotImplementedException(), Dog => throw new NotImplementedException() };
                }
            }
            """
        );

    [Fact]
    public Task Add_missing_arms_with_a_using_for_the_exception() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1 };
            }
            """, """
            using System;

            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, Dog => throw new NotImplementedException(), Bird => throw new NotImplementedException() };
            }
            """, usings: false
        );

    [Fact]
    public Task Add_missing_arms_with_the_file_s_line_breaks_and_indentation() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), WithCrLfAndTabs("""
            class C
            {
                int M(Pet pet) => pet switch
                {
                    Cat => 1,
                };
            }
            """), WithCrLfAndTabs("""
            class C
            {
                int M(Pet pet) => pet switch
                {
                    Cat => 1,
                    Dog => throw new NotImplementedException(),
                    Bird => throw new NotImplementedException(),
                };
            }
            """)
        );

    [Fact]
    public Task Add_missing_arms_without_a_trailing_comma() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch
                {
                    Cat => 1,
                    Dog => 2
                };
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch
                {
                    Cat => 1,
                    Dog => 2,
                    Bird => throw new NotImplementedException()
                };
            }
            """
        );

    [Fact]
    public Task Add_the_null_arm() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            public closed record Shape;
            public record Circle : Shape;
            public record Square : Shape;

            class C
            {
                int M(Shape? shape) => shape switch { Circle => 1, Square => 2 };
            }
            """, """
            public closed record Shape;
            public record Circle : Shape;
            public record Square : Shape;

            class C
            {
                int M(Shape? shape) => shape switch { Circle => 1, Square => 2, null => throw new NotImplementedException() };
            }
            """
        );

    [Fact]
    public Task Add_the_null_arm_alongside_when_arms() =>
        Verify.CodeFix(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Shape? shape, bool flag) => shape switch { Circle => 1, Square => 2, Triangle => 3, _ when flag => 4 };
            }
            """ + Snippets.Shapes, """
            class C
            {
                int M(Shape? shape, bool flag) => shape switch { Circle => 1, Square => 2, Triangle => 3, _ when flag => 4, null => throw new NotImplementedException() };
            }
            """ + Snippets.Shapes
        );

    [Fact]
    public Task Fix_all_in_separate_and_nested_switches() =>
        Verify.FixAll(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1 };

                int N(Pet pet, Pet other) => pet switch { Cat => other switch { Cat => 1 }, Dog => 2 };
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, Dog => throw new NotImplementedException(), Bird => throw new NotImplementedException() };

                int N(Pet pet, Pet other) => pet switch { Cat => other switch { Cat => 1, Dog => throw new NotImplementedException(), Bird => throw new NotImplementedException() }, Dog => 2, Bird => throw new NotImplementedException() };
            }
            """
        );

    [Fact]
    public Task Fix_all_with_one_using_for_the_exception() =>
        Verify.FixAll(new AddMissingArmsCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1 };

                int N(Pet pet) => pet switch { Dog => 1 };
            }
            """, """
            using System;

            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, Dog => throw new NotImplementedException(), Bird => throw new NotImplementedException() };

                int N(Pet pet) => pet switch { Dog => 1, Cat => throw new NotImplementedException(), Bird => throw new NotImplementedException() };
            }
            """, usings: false
        );
}
