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
}
