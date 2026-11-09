using UnionNarrowing.Suppressors.Analyzers;
using UnionNarrowing.CodeFixes;

namespace UnionNarrowing.Tests;

/// <summary>
/// Name the cases a throwing default catches (UN0002).
/// </summary>
public class ListCaughtCasesCodeFixTests
{
    [Fact]
    public Task Constant_labels_count_as_handling_their_case() =>
        Verify.CodeFix(new ListCaughtCasesCodeFix(), """
            class C
            {
                int M(Setting s)
                {
                    switch (s)
                    {
                        case true:
                            return 1;
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }
            public union Setting(bool, string);
            """, """
            class C
            {
                int M(Setting s)
                {
                    switch (s)
                    {
                        case true:
                            return 1;
                        case false:
                        case string:
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }
            public union Setting(bool, string);
            """, new ThrowingDefaultAnalyzer()
        );

    [Fact]
    public Task Fix_all() =>
        Verify.FixAll(new ListCaughtCasesCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, _ => throw new InvalidOperationException() };

                int N(Pet pet) => pet switch { Dog => 1, _ => throw new InvalidOperationException() };
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, Dog or Bird => throw new InvalidOperationException() };

                int N(Pet pet) => pet switch { Dog => 1, Cat or Bird => throw new InvalidOperationException() };
            }
            """, new ThrowingDefaultAnalyzer()
        );

    [Fact]
    public Task Name_the_cases_a_throw_after_a_switch_catches() =>
        Verify.CodeFix(new ListCaughtCasesCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat: return 1;
                    }
                    throw new InvalidOperationException();
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat: return 1;
                        case Dog:
                        case Bird:
                            throw new InvalidOperationException();
                    }
                    throw new InvalidOperationException();
                }
            }
            """, new ThrowingDefaultAnalyzer()
        );

    [Fact]
    public Task Name_the_cases_a_throw_after_ifs_catches() =>
        Verify.CodeFix(new ListCaughtCasesCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Cat) return 1;
                    if (pet is Dog) return 2;
                    throw new InvalidOperationException();
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Cat) return 1;
                    if (pet is Dog) return 2;
                    if (pet is Bird)
                        throw new InvalidOperationException();
                    throw new InvalidOperationException();
                }
            }
            """, new ThrowingDefaultAnalyzer()
        );

    [Fact]
    public Task Name_the_cases_a_throwing_arm_catches() =>
        Verify.CodeFix(new ListCaughtCasesCodeFix(), """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, _ => throw new InvalidOperationException() };
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, Dog or Bird => throw new InvalidOperationException() };
            }
            """, new ThrowingDefaultAnalyzer()
        );

    [Fact]
    public Task Name_the_cases_a_throwing_arm_catches_and_keep_its_variable() =>
        Verify.CodeFix(new ListCaughtCasesCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat => 1, var other => throw new InvalidOperationException(other.ToString()) };
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet switch { Cat => 1, Dog and var other => throw new InvalidOperationException(other.ToString()) };
                }
            }
            """, new ThrowingDefaultAnalyzer()
        );

    [Fact]
    public Task Name_the_cases_a_throwing_default_section_catches() =>
        Verify.CodeFix(new ListCaughtCasesCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat:
                            return 1;
                        case Dog:
                        case Bird:
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }
            """, new ThrowingDefaultAnalyzer()
        );

    [Fact]
    public Task Name_the_cases_a_throwing_else_block_catches() =>
        Verify.CodeFix(new ListCaughtCasesCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Cat)
                    {
                        return 1;
                    }
                    else if (pet is Dog)
                    {
                        return 2;
                    }
                    else
                    {
                        throw new InvalidOperationException();
                    }
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Cat)
                    {
                        return 1;
                    }
                    else if (pet is Dog)
                    {
                        return 2;
                    }
                    else if (pet is Bird)
                    {
                        throw new InvalidOperationException();
                    }
                    else
                    {
                        throw new InvalidOperationException();
                    }
                }
            }
            """, new ThrowingDefaultAnalyzer()
        );

    [Fact]
    public Task Name_the_cases_a_throwing_else_catches() =>
        Verify.CodeFix(new ListCaughtCasesCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Cat) return 1;
                    else if (pet is Dog d && d.Name.Length > 0) return 2;
                    else throw new InvalidOperationException();
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Cat) return 1;
                    else if (pet is Dog d && d.Name.Length > 0) return 2;
                    else if (pet is Dog or Bird) throw new InvalidOperationException();
                    else throw new InvalidOperationException();
                }
            }
            """, new ThrowingDefaultAnalyzer()
        );

    [Fact]
    public Task Name_the_combinations_a_throwing_arm_catches() =>
        Verify.CodeFix(new ListCaughtCasesCodeFix(), """
            class C
            {
                int M(Pet a, Pet b) => (a, b) switch { (Cat, _) => 1, _ => throw new InvalidOperationException() };
            }
            """, """
            class C
            {
                int M(Pet a, Pet b) => (a, b) switch { (Cat, _) => 1, (Dog, _) or (Bird, _) => throw new InvalidOperationException() };
            }
            """, new ThrowingDefaultAnalyzer()
        );
}
