using UnionNarrowing.Suppressors.Analyzers;
using UnionNarrowing.CodeFixes;

namespace UnionNarrowing.Tests;

/// <summary>
/// Remove the arm or case that can't match (UN0004), or the throwing arm that only catches cases added later (UN0016).
/// </summary>
public class RemoveDeadArmCodeFixTests
{
    [Fact]
    public Task Dead_case_section_with_a_directive_inside_is_left_alone() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        case Bird:
            #if DEBUG
                            Console.WriteLine("bird");
            #endif
                            return 3;
                        case Cat:
                            return 1;
                        default:
                            return 2;
                    }
                }
            }
            """, expected: null, new DeadArmAnalyzer()
        );

    [Fact]
    public Task Dead_case_section_that_declares_a_local_function_is_left_alone() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        case Bird:
                            int Three() => 3;
                            return Three();
                        case Cat:
                            return 1;
                        default:
                            return 2;
                    }
                }
            }
            """, expected: null, new DeadArmAnalyzer()
        );

    [Fact]
    public Task Dead_case_section_that_declares_a_local_is_left_alone() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        case Bird:
                            var count = 3;
                            return count;
                        case Cat:
                            count = 1;
                            return count;
                        default:
                            return 2;
                    }
                }
            }
            """, expected: null, new DeadArmAnalyzer()
        );

    [Fact]
    public Task Fix_all_arms_of_one_switch() =>
        Verify.FixAll(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird || pet is Dog) return 0;
                    return pet switch { Cat => 1, Dog => 2, Bird => 3 };
                }

                int N(Pet pet)
                {
                    if (pet is Bird || pet is Dog) return 0;
                    return pet switch
                    {
                        Dog => 2,
                        Cat => 1,
                        Bird => 3,
                    };
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird || pet is Dog) return 0;
                    return pet switch { Cat => 1 };
                }

                int N(Pet pet)
                {
                    if (pet is Bird || pet is Dog) return 0;
                    return pet switch
                    {
                        Cat => 1,
                    };
                }
            }
            """, new DeadArmAnalyzer()
        );

    [Fact]
    public Task Fix_all_in_nested_switches() =>
        Verify.FixAll(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet, Pet other)
                {
                    if (pet is Bird || other is Bird) return 0;
                    return pet switch { Cat => other switch { Cat => 1, Bird => 9, Dog => 2 }, Dog => 2, Bird => 3 };
                }
            }
            """, """
            class C
            {
                int M(Pet pet, Pet other)
                {
                    if (pet is Bird || other is Bird) return 0;
                    return pet switch { Cat => other switch { Cat => 1, Dog => 2 }, Dog => 2 };
                }
            }
            """, new DeadArmAnalyzer()
        );

    /// <summary>
    /// A section other sections could use a local from keeps one label, as fixing its labels one at a time would leave it.
    /// That label is still reported;
    /// the fix just isn't offered for it.
    /// </summary>
    [Fact]
    public Task Fix_all_keeps_a_label_on_a_section_that_declares_a_local() =>
        Verify.FixAll(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird || pet is Dog) return 0;
                    switch (pet)
                    {
                        case Dog:
                        case Bird:
                            var count = 2;
                            return count;
                        case Cat:
                            count = 1;
                            return count;
                        default:
                            return 3;
                    }
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird || pet is Dog) return 0;
                    switch (pet)
                    {
                        case Bird:
                            var count = 2;
                            return count;
                        case Cat:
                            count = 1;
                            return count;
                        default:
                            return 3;
                    }
                }
            }
            """, new DeadArmAnalyzer(), someLeftAlone: true
        );

    [Fact]
    public Task Fix_all_labels_of_one_section() =>
        Verify.FixAll(new RemoveDeadArmCodeFix(), """
            class C
            {
                int First(Pet pet)
                {
                    if (pet is Bird || pet is Dog) return 0;
                    switch (pet)
                    {
                        case Dog:
                        case Bird:
                            return 2;
                        case Cat:
                            return 1;
                        default:
                            return 3;
                    }
                }

                int Later(Pet pet)
                {
                    if (pet is Bird || pet is Dog) return 0;
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                        case Dog:
                        case Bird:
                            return 2;
                        default:
                            return 3;
                    }
                }
            }
            """, """
            class C
            {
                int First(Pet pet)
                {
                    if (pet is Bird || pet is Dog) return 0;
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                        default:
                            return 3;
                    }
                }

                int Later(Pet pet)
                {
                    if (pet is Bird || pet is Dog) return 0;
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                        default:
                            return 3;
                    }
                }
            }
            """, new DeadArmAnalyzer()
        );

    [Fact]
    public Task Remove_a_throwing_arm_that_only_catches_later_cases() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, Dog => 2, Bird => 3, _ => throw new InvalidOperationException() };
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, Dog => 2, Bird => 3 };
            }
            """, new ThrowingDefaultAnalyzer(), enabled: ["UN0016"]
        );

    [Fact]
    public Task Remove_dead_arm() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat => 1, Bird => 3, Dog => 2 };
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat => 1, Dog => 2 };
                }
            }
            """, new DeadArmAnalyzer()
        );

    [Fact]
    public Task Remove_dead_arm_after_an_endif() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch
                    {
            #if LEGACY
                        Cat => 0,
            #else
                        Cat => 1,
            #endif
                        Bird => 3,
                        Dog => 2,
                    };
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch
                    {
            #if LEGACY
                        Cat => 0,
            #else
                        Cat => 1,
            #endif
                        Dog => 2,
                    };
                }
            }
            """, new DeadArmAnalyzer()
        );

    /// <summary>
    /// What's inactive between the directives before the arm stays too: it's another configuration's code.
    /// </summary>
    [Fact]
    public Task Remove_dead_arm_after_an_else() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch
                    {
                        Cat => 1,
            #if LEGACY
                        Bird => 0,
            #else
                        Bird => 3,
            #endif
                        Dog => 2,
                    };
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch
                    {
                        Cat => 1,
            #if LEGACY
                        Bird => 0,
            #else
            #endif
                        Dog => 2,
                    };
                }
            }
            """, new DeadArmAnalyzer()
        );

    [Fact]
    public Task Remove_dead_arm_on_its_own_line() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch
                    {
                        Cat => 1,
                        Dog => 2,
                        Bird => 3,
                    };
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch
                    {
                        Cat => 1,
                        Dog => 2,
                    };
                }
            }
            """, new DeadArmAnalyzer()
        );

    [Fact]
    public Task Remove_dead_case_label() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        case Cat:
                        case Bird:
                            return 1;
                        default:
                            return 2;
                    }
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                        default:
                            return 2;
                    }
                }
            }
            """, new DeadArmAnalyzer()
        );

    [Fact]
    public Task Remove_dead_case_label_after_an_endif() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        case Cat:
            #if LEGACY
                        case Dog:
            #endif
                        case Bird:
                            return 1;
                        default:
                            return 2;
                    }
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        case Cat:
            #if LEGACY
                        case Dog:
            #endif
                            return 1;
                        default:
                            return 2;
                    }
                }
            }
            """, new DeadArmAnalyzer()
        );

    [Fact]
    public Task Remove_dead_case_section() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                        case Bird:
                            Console.WriteLine("bird");
                            return 3;
                        default:
                            return 2;
                    }
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                        default:
                            return 2;
                    }
                }
            }
            """, new DeadArmAnalyzer()
        );

    [Fact]
    public Task Remove_dead_case_section_after_an_endregion() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        #region Mammals
                        case Cat:
                            return 1;
                        case Dog:
                            return 2;
                        #endregion
                        case Bird:
                            return 3;
                    }

                    return 4;
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        #region Mammals
                        case Cat:
                            return 1;
                        case Dog:
                            return 2;
                        #endregion
                    }

                    return 4;
                }
            }
            """, new DeadArmAnalyzer()
        );

    [Fact]
    public Task Remove_dead_last_arm() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat => 1, Dog => 2, Bird => 3 };
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat => 1, Dog => 2 };
                }
            }
            """, new DeadArmAnalyzer()
        );

    /// <summary>
    /// Without a trailing comma, the arm that's now last loses its comma, but not the comment after it.
    /// </summary>
    [Fact]
    public Task Remove_dead_last_arm_after_a_comment() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch
                    {
                        Cat => 1,
                        Dog => 2, // dogs
                        Bird => 3 // never
                    };
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch
                    {
                        Cat => 1,
                        Dog => 2 // dogs
                    };
                }
            }
            """, new DeadArmAnalyzer()
        );

    /// <summary>
    /// The arm that's now last keeps its comma, which the arms in the other configuration need.
    /// </summary>
    [Fact]
    public Task Remove_dead_last_arm_after_an_if() =>
        Verify.CodeFix(new RemoveDeadArmCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch
                    {
                        Cat => 1,
                        Dog => 2,
            #if LEGACY
                        Bird => 0
            #else
                        Bird => 3
            #endif
                    };
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch
                    {
                        Cat => 1,
                        Dog => 2,
            #if LEGACY
                        Bird => 0
            #else
            #endif
                    };
                }
            }
            """, new DeadArmAnalyzer()
        );
}
