namespace UnionNarrowing.Tests;

/// <summary>
/// Switches over a tuple of unions, where earlier code rules cases out element by element.
/// </summary>
public class TupleTests
{
    [Fact]
    public Task Combination_guards_that_do_not_hold() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet a, Pet b, bool flag)
                {
                    if (a is Bird && b is Bird && flag) return 0;
                    return (a, b) /*warns*/switch { (Cat or Dog, _) => 1, (Bird, Cat or Dog) => 2 };
                }

                int N(Pet a, Pet b, Func<Pet> next)
                {
                    if (a is Bird && b is Bird) return 0;
                    /*because*/b = next();
                    return (a, b) /*warns*/switch { (Cat or Dog, _) => 1, (Bird, Cat or Dog) => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Combination_neither_handled_nor_ruled_out() =>
        Verify.Suppressions("""
            class C
            {
                // (Dog, Dog) and (Dog, Bird) are left.
                int M(Pet a, Pet b)
                {
                    if (a is Bird) return 0;
                    return (a, b) /*warns*/switch { (Cat, _) => 1, (Dog, Cat) => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Combinations_ruled_out_by_more_kinds_of_guard() =>
        Verify.Suppressions("""
            class C
            {
                int EarlierSwitchStatement(Pet a, Pet b)
                {
                    switch (a, b) { case (Bird, Bird): return 0; }
                    return (a, b) /*suppressed*/switch { (Cat, _) => 1, (Dog, _) => 2, (Bird, Cat) => 3, (Bird, Dog) => 4 };
                }

                int InASection(Pet a, Pet b)
                {
                    switch (a, b)
                    {
                        case (Bird, Bird): return 0;
                        case (Bird, _): return (a, b) /*suppressed*/switch { (Bird, Cat) => 1, (Bird, Dog) => 2 };
                        default: return 3;
                    }
                }

                int Conditional(Pet a, Pet b) =>
                    a is Bird && b is Bird ? 0 : (a, b) /*suppressed*/switch { (Cat, _) => 1, (Dog, _) => 2, (Bird, Cat) => 3, (Bird, Dog) => 4 };

                int RightOfOr(Pet a, Pet b) =>
                    a is Bird && b is Bird || (a, b) /*suppressed*/switch { (Cat, _) => true, (Dog, _) => false, (Bird, Cat) => true, (Bird, Dog) => false } ? 1 : 0;

                int IfWithAnElse(Pet a, Pet b, bool log)
                {
                    if (a is Bird && b is Bird) { return 0; }
                    else if (log) { Console.WriteLine("other"); }
                    return (a, b) /*suppressed*/switch { (Cat, _) => 1, (Dog, _) => 2, (Bird, Cat) => 3, (Bird, Dog) => 4 };
                }

                // A section that breaks out could have matched.
                int SectionThatBreaks(Pet a, Pet b, bool log)
                {
                    switch (a, b) { case (Bird, Bird): if (log) break; return 0; }
                    return (a, b) /*warns*/switch { (Cat, _) => 1, (Dog, _) => 2, (Bird, Cat) => 3, (Bird, Dog) => 4 };
                }
            }
            """
        );

    [Fact]
    public Task Element_that_can_be_null() =>
        Verify.Suppressions("""
            union MaybeCat(Cat?, Dog);

            class C
            {
                int M(MaybeCat a, Pet b)
                {
                    if (b is Bird) return 0;
                    return (a, b) /*warns*/switch { (Cat, Cat or Dog) => 1, (Dog, Cat or Dog) => 2 };
                }

                int N(MaybeCat a, Pet b)
                {
                    if (b is Bird || a is null) return 0;
                    return (a, b) /*suppressed*/switch { (Cat, Cat or Dog) => 1, (Dog, Cat or Dog) => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Element_the_analysis_can_not_follow() =>
        Verify.Suppressions("""
            class C
            {
                Pet _other;

                void Reset() => _other = new Bird("Polly");

                int M(Pet a)
                {
                    if (a is Bird || _other is Bird) return 0;
                    Reset();
                    return (a, _other) /*warns*/switch { (Cat or Dog, Cat or Dog) => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Elements_that_are_not_unions() =>
        Verify.Suppressions("""
            enum Size { Small, Large }

            class C
            {
                int Bool(Pet pet, bool flag)
                {
                    if (pet is Bird) return 0;
                    return (pet, flag) /*suppressed*/switch { (Cat, true) => 1, (Cat, false) => 2, (Dog, _) => 3 };
                }

                int Int(Pet pet, int n)
                {
                    if (pet is Bird) return 0;
                    return (pet, n) /*suppressed*/switch { (Cat, _) => 1, (Dog, > 0) => 2, (Dog, int) => 3 };
                }

                int String(Pet pet, string? s)
                {
                    if (pet is Bird) return 0;
                    return (pet, s) /*suppressed*/switch { (Cat, null) => 1, (Cat, not null) => 2, (Dog, _) => 3 };
                }

                int Three(Pet pet, Pet other, bool flag)
                {
                    if (pet is Bird || other is Bird) return 0;
                    return (pet, other, flag) /*suppressed*/switch { (Cat, _, _) => 1, (Dog, _, _) => 2 };
                }

                // (Dog, 0) and the other ints aren't handled.
                int Partly(Pet pet, int n)
                {
                    if (pet is Bird) return 0;
                    return (pet, n) /*warns*/switch { (Cat, _) => 1, (Dog, > 0) => 2 };
                }

                // s can be null.
                int MaybeNull(Pet pet, string? s)
                {
                    if (pet is Bird) return 0;
                    return (pet, s) /*warns*/switch { (Cat, string) => 1, (Dog, _) => 3 };
                }

                // Without a union element, the analysis isn't for this switch.
                int NoUnion(bool a, bool b)
                {
                    if (a) return 0;
                    return (a, b) /*warns*/switch { (false, true) => 1, (false, false) => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Enum_elements_and_values_no_member_has() =>
        Verify.Suppressions("""
            enum Size { Small, Large }

            class C
            {
                int M(Pet pet, Size size)
                {
                    if (pet is Bird) return 0;
                    return (pet, size) /*suppressed*/switch { (Cat, _) => 1, (Dog, Size.Small) => 2, (Dog, Size.Large or _) => 3 };
                }

                // (Dog, (Size)2) isn't handled: the compiler would say so with CS8524, once CS8509 is gone.
                int N(Pet pet, Size size)
                {
                    if (pet is Bird) return 0;
                    return (pet, size) /*warns*/switch { (Cat, _) => 1, (Dog, Size.Small) => 2, (Dog, Size.Large) => 3 };
                }
            }
            """
        );

    [Fact]
    public Task Enum_elements_where_values_no_member_has_are_ignored() =>
        Verify.Suppressions("""
            enum Size { Small, Large }

            class C
            {
                int N(Pet pet, Size size)
                {
                    if (pet is Bird) return 0;
                    return (pet, size) /*suppressed*/switch { (Cat, _) => 1, (Dog, Size.Small) => 2, (Dog, Size.Large) => 3 };
                }
            }
            """, disabledWarnings: ["CS8524"]
        );

    [Fact]
    public Task Guard_on_one_element() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet a, Pet b)
                {
                    if (a is Bird) return 0;
                    return (a, b) /*suppressed*/switch { (Cat, _) => 1, (Dog, _) => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Guards_on_both_elements() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet a, Pet b)
                {
                    if (a is Bird || b is Bird) return 0;
                    return (a, b) /*suppressed*/switch
                    {
                        (Cat, Cat) => 1,
                        (Cat, Dog) or (Dog, Cat) => 2,
                        (Dog, Dog) => 3,
                    };
                }
            }
            """
        );

    [Fact]
    public Task Guards_on_elements_that_are_not_unions() =>
        Verify.Suppressions("""
            enum Size { Small, Large }

            class C
            {
                int Bool(Pet pet, bool flag)
                {
                    if (flag) return 0;
                    return (pet, flag) /*suppressed*/switch { (_, false) => 1 };
                }

                int NotBool(Pet pet, bool flag)
                {
                    if (!flag) return 0;
                    return (pet, flag) /*suppressed*/switch { (Cat, true) => 1, (Dog or Bird, _) => 2 };
                }

                int Enum(Pet pet, Size size)
                {
                    if (size == Size.Large) return 0;
                    if (pet is Bird) return 1;
                    return (pet, size) /*suppressed*/switch { (Cat, Size.Small) => 2, (Dog, Size.Small) => 3, (_, not (Size.Small or Size.Large)) => 4 };
                }

                int Null(Pet pet, string? s)
                {
                    if (s is null || pet is Bird) return 0;
                    return (pet, s) /*suppressed*/switch { (Cat, string) => 1, (Dog, _) => 2 };
                }

                int EqualsTrue(Pet pet, bool flag)
                {
                    if (flag == true) return 0;
                    return (pet, flag) /*suppressed*/switch { (_, false) => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Guards_that_rule_out_a_combination() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet a, Pet b)
                {
                    if (a is Bird && b is Bird) return 0;
                    return (a, b) /*suppressed*/switch { (Cat or Dog, _) => 1, (Bird, Cat or Dog) => 2 };
                }

                int N(Pet a, Pet b)
                {
                    if ((a, b) is (Bird, Bird) or (Cat, Cat)) return 0;
                    return (a, b) /*suppressed*/switch { (Cat, Dog or Bird) => 1, (Dog, _) => 2, (Bird, Cat or Dog) => 3 };
                }

                int O(Pet a, Pet b)
                {
                    if (!(a is Bird && b is Bird))
                        return (a, b) /*warns*/switch { (Cat or Dog, _) => 1 };
                    return 0;
                }

                int P(Pet a, Pet b)
                {
                    if (!(a is not Bird || b is not Bird))
                        return 0;
                    return (a, b) /*suppressed*/switch { (Cat or Dog, _) => 1, (Bird, Cat or Dog) => 2 };
                }

                int NotPattern(Pet a, Pet b)
                {
                    if ((a, b) is not (Bird, Bird))
                        return (a, b) /*suppressed*/switch { (Cat or Dog, _) => 1, (Bird, Cat or Dog) => 2 };
                    return 0;
                }

                int AndPattern(Pet a, Pet b)
                {
                    if ((a, b) is (Bird, _) and (_, Bird)) return 0;
                    return (a, b) /*suppressed*/switch { (Cat or Dog, _) => 1, (Bird, Cat or Dog) => 2 };
                }

                // Only some Birds as the second element.
                int AndPartly(Pet a, Pet b)
                {
                    if ((a, b) is (Bird, _) and (_, Bird { Name: "Polly" })) return 0;
                    return (a, b) /*warns*/switch { (Cat or Dog, _) => 1, (Bird, Cat or Dog) => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Tuple_pattern_guard() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet a, Pet b)
                {
                    if ((a, b) is (Bird, _)) return 0;
                    return (a, b) /*suppressed*/switch { (Cat or Dog, _) => 1 };
                }

                int N(Pet a, Pet b)
                {
                    if ((a, b) is not (Cat or Dog, var _)) return 0;
                    return a /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // Only when b is also a Cat, so a Bird can still get past.
                int O(Pet a, Pet b)
                {
                    if ((a, b) is (Bird, Cat)) return 0;
                    return a /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Tuple_typed_variable() =>
        Verify.Suppressions("""
            class C
            {
                (Pet, Pet) Get() => (new Cat("Tom"), new Dog("Rex"));

                int M(Pet a, Pet b)
                {
                    var t = (a, b);
                    if (t.Item1 is Bird) return 0;
                    return t /*suppressed*/switch { (Cat, _) => 1, (Dog, _) => 2 };
                }

                int N()
                {
                    var t = Get();
                    if (t.Item1 is Bird || t.Item2 is Bird) return 0;
                    return t /*suppressed*/switch { (Cat or Dog, Cat) => 1, (Cat or Dog, Dog) => 2 };
                }

                int O((Pet A, bool Flag) t)
                {
                    if (t.A is Bird) return 0;
                    return t /*suppressed*/switch { (Cat, true) => 1, (Cat, false) => 2, (Dog, _) => 3 };
                }

                int P(Pet a, Pet b)
                {
                    var t = (a, b);
                    if (t.Item1 is Bird) return 0;
                    /*because*/t = (new Bird("Polly"), b);
                    return t /*warns*/switch { (Cat, _) => 1, (Dog, _) => 2 };
                }
            }
            """
        );

    /// <summary>
    /// The compiler reports CS8509 on some tuple switches that handle every combination, where wildcards sit in different places in different arms.
    /// </summary>
    [Fact]
    public Task Combinations_the_compiler_misses() =>
        Verify.Suppressions("""
            class C
            {
                int Complete(Pet a, Pet b) => (a, b) /*suppressed*/switch { (Cat, Cat) => 1, (_, Dog) => 2, (_, Bird) => 3, (Dog or Bird, _) => 4 };

                int Missing(Pet a, Pet b) => (a, b) /*warns*/switch { (Cat, Cat) => 1, (_, Dog) => 2, (Dog or Bird, _) => 4 };
            }
            """
        );
}
