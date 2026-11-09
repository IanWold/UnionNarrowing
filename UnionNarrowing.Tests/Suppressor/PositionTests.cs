namespace UnionNarrowing.Tests;

/// <summary>
/// Narrowing from what encloses the switch:
/// branches, short-circuiting operators, arms and sections of an outer switch, and catch filters.
/// </summary>
public class PositionTests
{
    [Fact]
    public Task An_arms_own_when_clause() =>
        Verify.Suppressions("""
            class C
            {
                int Arm(Pet pet) => pet switch
                {
                    _ when pet is not Bird => pet /*suppressed*/switch { Cat => 1, Dog => 2 },
                    _ => 0,
                };

                // Whatever the outer switch is over.
                int OverSomethingElse(int n, Pet pet) => n switch
                {
                    > 0 when pet is not Bird => pet /*suppressed*/switch { Cat => 1, Dog => 2 },
                    _ when pet is Bird => 0,
                    _ => pet /*suppressed*/switch { Cat => 1, Dog => 2 },
                };

                int Section(Pet pet)
                {
                    switch (pet)
                    {
                        case var x when pet is not Bird:
                            return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                        default:
                            return 0;
                    }
                }

                // A clause that changes the value says nothing about what it holds after.
                int ClauseReplacesIt(Pet pet, Func<Pet> next) => pet switch
                {
                    _ when pet is not Bird && (pet = next()) is not null => pet /*warns*/switch { Cat => 1, Dog => 2 },
                    _ => 0,
                };
            }
            """
        );

    [Fact]
    public Task Arms_and_sections_over_what_the_value_is_a_member_of() =>
        Verify.Suppressions("""
            record Owner(Pet Pet, int Age);

            class C
            {
                int Arm(Owner owner) => owner switch
                {
                    { Pet: Bird } => 0,
                    _ => owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 },
                };

                int ThisArm(Owner owner) => owner switch
                {
                    { Pet: Cat or Dog } => owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 },
                    _ => 0,
                };

                int Section(Owner owner)
                {
                    switch (owner)
                    {
                        case (Bird, _):
                            return 0;
                        default:
                            return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                }

                int After(Owner owner)
                {
                    switch (owner)
                    {
                        case { Pet: Bird }:
                            return 0;
                    }
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Throwing(Owner owner)
                {
                    var age = owner switch { { Pet: Bird } => throw new InvalidOperationException(), _ => owner.Age };
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Condition(Owner owner)
                {
                    if (owner switch { { Pet: Bird } => true, _ => false }) return 0;
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // A when clause on an earlier arm might not have held.
                int Guarded(Owner owner) => owner switch
                {
                    { Pet: Bird } when owner.Age > 1 => 0,
                    _ => owner.Pet /*warns*/switch { Cat => 1, Dog => 2 },
                };
            }
            """
        );

    [Fact]
    public Task Conditional_expression() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet) => pet is Bird ? 0 : pet /*suppressed*/switch { Cat => 1, Dog => 2 };

                int N(Pet pet) => pet is Cat or Dog ? pet /*suppressed*/switch { Cat => 1, Dog => 2 } : 0;

                int O(Pet pet, bool flag) => flag ? 0 : pet /*warns*/switch { Cat => 1, Dog => 2 };
            }
            """
        );

    [Fact]
    public Task Earlier_arm_with_a_when_clause() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet) => pet switch
                {
                    Cat => 1,
                    _ when pet is Dog => 2,
                    _ => pet /*suppressed*/switch { Bird => 3 },
                };

                // A guard that's only sometimes true for a case rules nothing out.
                int N(Pet pet, bool flag) => pet switch
                {
                    Cat => 1,
                    Dog when flag => 2,
                    _ => pet /*warns*/switch { Bird => 3 },
                };

                int O(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                        case var _ when pet is Dog:
                            return 2;
                        default:
                            return pet /*suppressed*/switch { Bird => 3 };
                    }
                }
            }
            """
        );

    [Fact]
    public Task If_branch_that_writes_before_the_switch() =>
        Verify.Suppressions("""
            class C
            {
                static bool Reset(out Pet pet)
                {
                    pet = new Bird("Polly");
                    return true;
                }

                int M(Pet pet)
                {
                    if (pet is not Bird)
                    {
                        /*because*/pet = new Bird("Polly");
                        return pet /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                    return 0;
                }

                int N(Pet pet)
                {
                    if (pet is not Bird && Reset(out pet)) return pet /*warns*/switch { Cat => 1, Dog => 2 };
                    return 0;
                }
            }
            """
        );

    [Fact]
    public Task If_branches() =>
        Verify.Suppressions("""
            class C
            {
                int Then(Pet pet)
                {
                    if (pet is Cat or Dog)
                    {
                        Console.WriteLine("not a bird");
                        return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                    return 0;
                }

                int EmbeddedThen(Pet pet)
                {
                    if (pet is not Bird) return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return 0;
                }

                int Else(Pet pet)
                {
                    if (pet is Bird) Console.WriteLine("bird");
                    else return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return 0;
                }
            }
            """
        );

    [Fact]
    public Task In_a_catch_whose_filter_held() =>
        Verify.Suppressions("""
            class C
            {
                int Filter(Pet pet, Action act)
                {
                    try { act(); return 0; }
                    catch (Exception) when (pet is not Bird)
                    {
                        return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                }

                // Filters run before the finally blocks inside the try, which could change it.
                int FinallyInTheTry(Pet pet, Action act, Func<Pet> next)
                {
                    try
                    {
                        try { act(); }
                        finally { pet = next(); }
                        return 0;
                    }
                    catch (Exception) when (pet is not Bird)
                    {
                        return pet /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                }
            }
            """
        );

    [Fact]
    public Task Outer_switch_over_a_tuple_literal() =>
        Verify.Suppressions("""
            record Owner(Pet Pet);

            class C
            {
                int Element(Pet a, Pet b) => (a, b) switch
                {
                    (Bird, _) => 0,
                    _ => a /*suppressed*/switch { Cat => 1, Dog => 2 },
                };

                int Together(Pet a, Pet b) => (a, b) switch
                {
                    (Bird, Bird) => 0,
                    _ => (a, b) /*suppressed*/switch { (Cat or Dog, _) => 1, (Bird, Cat or Dog) => 2 },
                };

                int ThisArm(Pet a, Pet b) => (a, b) switch
                {
                    (Cat or Dog, _) => a /*suppressed*/switch { Cat => 1, Dog => 2 },
                    _ => 0,
                };

                int Member(Owner owner, Pet b) => (owner, b) switch
                {
                    ({ Pet: Bird }, _) => 0,
                    _ => owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 },
                };

                int Section(Pet a, Pet b)
                {
                    switch (a, b)
                    {
                        case (Bird, _): return 0;
                        default: return a /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                }

                // Only a Bird with a Cat is ruled out.
                int Partly(Pet a, Pet b) => (a, b) switch
                {
                    (Bird, Cat) => 0,
                    _ => a /*warns*/switch { Cat => 1, Dog => 2 },
                };
            }
            """
        );

    [Fact]
    public Task Short_circuiting_operators() =>
        Verify.Suppressions("""
            class C
            {
                bool M(Pet pet) => pet is not Bird && pet /*suppressed*/switch { Cat => true, Dog => false };

                bool N(Pet pet) => pet is Bird || pet /*suppressed*/switch { Cat => true, Dog => false };

                bool O(Pet pet) => pet is Bird && pet /*warns*/switch { Cat => true, Dog => false };
            }
            """
        );

    [Fact]
    public Task Switch_expression_arm_whose_when_clause_writes() =>
        Verify.Suppressions("""
            class C
            {
                static bool Reset(out Pet pet)
                {
                    pet = new Bird("Polly");
                    return false;
                }

                int M(Pet pet) => pet switch
                {
                    Bird => 0,
                    Cat when Reset(out pet) => 1,
                    _ => pet /*warns*/switch { Cat => 1, Dog => 2 },
                };
            }
            """
        );

    [Fact]
    public Task Switch_expression_arms() =>
        Verify.Suppressions("""
            class C
            {
                int Discard(Pet pet) => pet switch
                {
                    Bird => 0,
                    _ => pet /*suppressed*/switch { Cat => 1, Dog => 2 },
                };

                int ThisArm(Pet pet) => pet switch
                {
                    Cat or Dog => pet /*suppressed*/switch { Cat => 1, Dog => 2 },
                    Bird => 0,
                };

                int OtherValue(Pet pet, Pet other) => other switch
                {
                    Bird => 0,
                    _ => pet /*warns*/switch { Cat => 1, Dog => 2 },
                };
            }
            """
        );

    [Fact]
    public Task Switch_statement_sections() =>
        Verify.Suppressions("""
            class C
            {
                int Default(Pet pet)
                {
                    switch (pet)
                    {
                        case Bird:
                            return 0;
                        default:
                            return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                }

                int ThisSection(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat or Dog:
                            Console.WriteLine("not a bird");
                            return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                        default:
                            return 0;
                    }
                }

                int EarlierSection(Pet pet)
                {
                    switch (pet)
                    {
                        case Bird:
                            return 0;
                        case var other:
                            return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                }

                int GuardInSection(int n, Pet pet)
                {
                    switch (n)
                    {
                        case 1:
                            if (pet is Bird) return 0;
                            return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                        default:
                            return -1;
                    }
                }
            }
            """
        );

    [Fact]
    public Task Switch_statement_whose_when_clause_writes() =>
        Verify.Suppressions("""
            class C
            {
                static bool Reset(out Pet pet)
                {
                    pet = new Bird("Polly");
                    return false;
                }

                int M(Pet pet)
                {
                    switch (pet)
                    {
                        case Bird:
                            return 0;
                        case Cat when Reset(out pet):
                            return 1;
                        default:
                            return pet /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                }
            }
            """
        );

    [Fact]
    public Task Switch_statement_with_goto_default() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    switch (pet)
                    {
                        case Bird:
                            Console.WriteLine("bird");
                            goto default;
                        default:
                            return pet /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                }
            }
            """
        );

    [Fact]
    public Task When_clauses_on_what_the_pattern_binds() =>
        Verify.Suppressions("""
            class C
            {
                int Section(Pet pet)
                {
                    switch (pet)
                    {
                        case var p when p is Bird:
                            return 0;
                        default:
                            return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                }

                int Arm(Pet pet) => pet switch
                {
                    var p when p.Value is Bird => 0,
                    _ => pet /*suppressed*/switch { Cat => 1, Dog => 2 },
                };

                int Closed(Shape shape) => shape switch
                {
                    Shape s when s is Circle => 0,
                    _ => shape /*suppressed*/switch { Square => 1, Triangle => 2 },
                };

                // Only some Birds.
                int PartOfTheClause(Pet pet, bool flag)
                {
                    switch (pet)
                    {
                        case var p when p is Bird && flag:
                            return 0;
                        default:
                            return pet /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                }
            }
            """ + Snippets.Shapes
        );
}
