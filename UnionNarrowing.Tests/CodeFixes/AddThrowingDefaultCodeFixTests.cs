using UnionNarrowing.CodeFixes;

namespace UnionNarrowing.Tests;

/// <summary>
/// Add a throwing default, for CS0161 and CS0165 after a switch statement that handles every case.
/// </summary>
public class AddThrowingDefaultCodeFixTests
{
    [Fact]
    public Task Add_a_throwing_default_for_a_return() =>
        Verify.CodeFix(new AddThrowingDefaultCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    switch (pet)
                    {
                        case Cat: return 1;
                        case Dog: return 2;
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
                        case Cat: return 1;
                        case Dog: return 2;
                        default:
                            throw new System.Diagnostics.UnreachableException();
                    }
                }
            }
            """
        );

    [Fact]
    public Task Add_a_throwing_default_for_an_assignment() =>
        Verify.CodeFix(new AddThrowingDefaultCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    int legs;
                    switch (pet)
                    {
                        case Cat: legs = 4; break;
                        case Dog: legs = 4; break;
                        case Bird: legs = 2; break;
                    }
                    return legs;
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    int legs;
                    switch (pet)
                    {
                        case Cat: legs = 4; break;
                        case Dog: legs = 4; break;
                        case Bird: legs = 2; break;
                        default:
                            throw new System.Diagnostics.UnreachableException();
                    }
                    return legs;
                }
            }
            """
        );

    [Fact]
    public Task Add_a_throwing_default_in_a_getter() =>
        Verify.CodeFix(new AddThrowingDefaultCodeFix(), """
            class C
            {
                readonly Pet _pet = new Cat("Tom");

                int Legs
                {
                    get
                    {
                        switch (_pet)
                        {
                            case Cat: return 4;
                            case Dog: return 4;
                            case Bird: return 2;
                        }
                    }
                }
            }
            """, """
            class C
            {
                readonly Pet _pet = new Cat("Tom");

                int Legs
                {
                    get
                    {
                        switch (_pet)
                        {
                            case Cat: return 4;
                            case Dog: return 4;
                            case Bird: return 2;
                            default:
                                throw new System.Diagnostics.UnreachableException();
                        }
                    }
                }
            }
            """
        );

    /// <summary>
    /// In a lambda, "not all code paths return a value" is CS1643.
    /// </summary>
    [Fact]
    public Task Add_a_throwing_default_in_a_lambda() =>
        Verify.CodeFix(new AddThrowingDefaultCodeFix(), """
            class C
            {
                Func<Pet, int> Legs() => pet =>
                {
                    switch (pet)
                    {
                        case Cat: return 4;
                        case Dog: return 4;
                        case Bird: return 2;
                    }
                };
            }
            """, """
            class C
            {
                Func<Pet, int> Legs() => pet =>
                {
                    switch (pet)
                    {
                        case Cat: return 4;
                        case Dog: return 4;
                        case Bird: return 2;
                        default:
                            throw new System.Diagnostics.UnreachableException();
                    }
                };
            }
            """
        );

    [Fact]
    public Task Add_a_throwing_default_in_a_local_function() =>
        Verify.CodeFix(new AddThrowingDefaultCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    return Legs(pet);

                    static int Legs(Pet pet)
                    {
                        switch (pet)
                        {
                            case Cat: return 4;
                            case Dog: return 4;
                            case Bird: return 2;
                        }
                    }
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    return Legs(pet);

                    static int Legs(Pet pet)
                    {
                        switch (pet)
                        {
                            case Cat: return 4;
                            case Dog: return 4;
                            case Bird: return 2;
                            default:
                                throw new System.Diagnostics.UnreachableException();
                        }
                    }
                }
            }
            """
        );

    [Fact]
    public Task Fix_all() =>
        Verify.FixAll(new AddThrowingDefaultCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat: return 1;
                        case Dog or Bird: return 2;
                    }
                }

                int N(Pet pet)
                {
                    if (pet is Cat) return 1;
                    else if (pet is Dog) return 2;
                    else if (pet is Bird) return 3;
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
                        case Dog or Bird: return 2;
                        default:
                            throw new System.Diagnostics.UnreachableException();
                    }
                }

                int N(Pet pet)
                {
                    if (pet is Cat) return 1;
                    else if (pet is Dog) return 2;
                    else if (pet is Bird) return 3;
                    throw new System.Diagnostics.UnreachableException();
                }
            }
            """
        );

    [Fact]
    public Task No_throwing_default_for_a_switch_that_misses_a_case() =>
        Verify.CodeFix(new AddThrowingDefaultCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat: return 1;
                        case Dog: return 2;
                    }
                }
            }
            """, null
        );

    [Fact]
    public Task Throw_after_ifs_that_test_every_case() =>
        Verify.CodeFix(new AddThrowingDefaultCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Cat) return 1;
                    else if (pet is Dog) return 2;
                    else if (pet is Bird) return 3;
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Cat) return 1;
                    else if (pet is Dog) return 2;
                    else if (pet is Bird) return 3;
                    throw new System.Diagnostics.UnreachableException();
                }
            }
            """
        );
}
