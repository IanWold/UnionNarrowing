namespace UnionNarrowing.Tests;

/// <summary>
/// Which switch arms count as handling a case type.
/// </summary>
public class ArmTests
{
    [Fact]
    public Task Arm_with_a_property_subpattern_does_not_count() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet /*warns*/switch { Cat { Name: "Tom" } => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Arm_with_a_when_clause_does_not_count() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet /*warns*/switch { Cat c when c.Name == "Tom" => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Arm_with_a_when_clause_in_the_switch_itself() =>
        Verify.Suppressions("""
            class C
            {
                static bool Reset(out Pet pet)
                {
                    pet = new Bird("Polly");
                    return false;
                }

                int M(Pet pet) => pet /*suppressed*/switch
                {
                    Cat => 1,
                    _ when pet is Dog => 2,
                    Bird => 3,
                };

                // An earlier `when` clause writes pet, so the later one tests a different value.
                int N(Pet pet) => pet /*warns*/switch
                {
                    Cat when Reset(out pet) => 1,
                    Cat => 1,
                    _ when pet is Dog => 2,
                    Bird => 3,
                };
            }
            """, diagnosticId: "CS8846"
        );

    [Fact]
    public Task Arm_with_a_when_clause_that_does_not_cover_what_earlier_code_ruled_out() =>
        Verify.Suppressions("""
            class C
            {
                // CS8846: the `when` arm might match a Bird, but earlier code already ruled Bird out.
                int M(Pet pet, bool flag)
                {
                    if (pet is Bird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2, _ when flag => 3 };
                }

                int N(Pet pet, bool flag) => pet /*warns*/switch { Cat => 1, Dog => 2, _ when flag => 3 };
            }
            """, diagnosticId: "CS8846"
        );

    [Fact]
    public Task Null_arm_alone_does_not_cover_a_case_type() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet /*warns*/switch { Cat => 1, null => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Or_pattern_arm() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet /*suppressed*/switch { Cat or Dog => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Subpatterns_that_match_anything() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet /*suppressed*/switch { Cat { Name: _ } => 1, Dog(var name) => name.Length };
                }

                int N(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet /*suppressed*/switch { Cat(_) and { Name: var name } => name.Length, Dog { Name: _ or null } => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Subpatterns_that_might_not_match() =>
        Verify.Suppressions("""
            class C
            {
                // `Name.Length: _` also needs Name not to be null.
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog { Name.Length: _ } => 2 };
                }

                int N(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog { Name: { } } => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Type_declaration_and_empty_property_patterns() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    var first = pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    var second = pet /*suppressed*/switch { Cat c => 1, Dog d => 2 };
                    var third = pet /*suppressed*/switch { Cat { } => 1, Dog { } => 2 };
                    return first + second + third;
                }
            }
            """
        );

    [Fact]
    public Task When_clauses_that_split_a_case_over_bools() =>
        Verify.Suppressions("""
            class C
            {
                int Split(Pet pet, bool loud) => pet /*suppressed*/switch { Cat when loud => 1, Cat when !loud => 2, Dog => 3, Bird => 4 };

                int SplitTwice(Pet pet, bool loud, bool big) => pet /*suppressed*/switch
                {
                    Cat when loud && big => 1,
                    Cat when loud && !big => 2,
                    Cat when !loud => 3,
                    Dog => 4,
                    Bird => 5,
                };

                int NotComplementary(Pet pet, bool loud, bool quiet) => pet /*warns*/switch { Cat when loud => 1, Cat when quiet => 2, Dog => 3, Bird => 4 };

                // A clause that changes the bool.
                int ChangedByAClause(Pet pet, bool loud) => pet /*warns*/switch { Cat when loud => 1, Cat when !(loud = !loud) => 2, Dog => 3, Bird => 4 };
            }
            """, diagnosticId: "CS8846"
        );
}
