namespace UnionNarrowing.Tests;

/// <summary>
/// What's known about a parameter from where its function is called:
/// local functions, private and internal methods, constructors, and lambdas that are only invoked.
/// </summary>
public class CallTests
{
    [Fact]
    public Task Constructor_parameters_get_what_every_creation_passes() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return new Owner(pet).Legs() + new Keeper(pet).Legs();
                }

                Owner Make() => new(new Cat("Tom"));
            }

            internal class Owner(Pet pet)
            {
                public int Legs() => pet /*suppressed*/switch { Cat => 4, Dog => 4 };
            }

            internal class Keeper
            {
                private readonly int _legs;
                public Keeper(Pet pet) { _legs = pet /*suppressed*/switch { Cat => 4, Dog => 4 }; }
                public int Legs() => _legs;
            }

            internal class Derived : Keeper
            {
                public Derived() : base(new Dog("Rex")) { }
            }

            // Anyone can make one of these.
            public sealed class Visible(Pet pet)
            {
                public int Legs() => pet /*warns*/switch { Cat => 4, Dog => 4 };
            }

            internal class Unknown(Pet pet)
            {
                public int Legs() => pet /*warns*/switch { Cat => 4, Dog => 4 };
                public static Unknown Of(Pet any) => new(any);
            }
            """
        );

    [Fact]
    public Task Internal_and_partial_helpers() =>
        Verify.Suppressions("""
            public static class Helpers
            {
                internal static int Count(Pet p) => p /*suppressed*/switch { Cat => 1, Dog => 2 };

                // Anything outside the assembly could call it.
                public static int Visible(Pet p) => p /*warns*/switch { Cat => 1, Dog => 2 };
            }

            internal class Internal
            {
                // Public, but only as visible as its type.
                public static int Legs(Pet p) => p /*suppressed*/switch { Cat => 4, Dog => 4 };
            }

            internal interface ICounter { int Count(Pet p); }

            internal class Counter : ICounter
            {
                // Called through the interface too.
                public int Count(Pet p) => p /*warns*/switch { Cat => 1, Dog => 2 };
            }

            partial class C
            {
                int M(Pet pet) => pet is Bird ? 0 : Helpers.Count(pet) + Helpers.Visible(pet) + Internal.Legs(pet) + new Counter().Count(pet);

                int N(Pet pet) => pet is Bird ? 0 : Split(pet);
            }

            partial class C
            {
                private static int Split(Pet p) => p /*suppressed*/switch { Cat => 1, Dog => 2 };
            }
            """
        );

    [Fact]
    public Task Internal_helpers_in_an_assembly_that_shows_its_internals() =>
        Verify.Suppressions("""
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Other")]

            static class Helpers
            {
                internal static int Count(Pet p) => p /*warns*/switch { Cat => 1, Dog => 2 };
            }

            class C
            {
                int M(Pet pet) => pet is Bird ? 0 : Helpers.Count(pet);
            }
            """
        );

    [Fact]
    public Task Lambda_in_a_local_that_is_only_invoked() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    Func<Pet, int> f = p => p /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return f(pet) + f.Invoke(pet);
                }

                int CalledWithSomethingElse(Pet pet, Pet other)
                {
                    if (pet is Bird) return 0;
                    Func<Pet, int> f = p => p /*warns*/switch { Cat => 1, Dog => 2 };
                    return f(pet) + f(other);
                }

                int PassedOn(Pet pet, Func<Func<Pet, int>, int> run)
                {
                    if (pet is Bird) return 0;
                    Func<Pet, int> f = p => p /*warns*/switch { Cat => 1, Dog => 2 };
                    return f(pet) + run(f);
                }
            }
            """
        );

    [Fact]
    public Task Lambda_passed_to_a_method_that_calls_it_with_its_own_parameter() =>
        Verify.Suppressions("""
            static class Helpers
            {
                public static R Run<T, R>(T value, Func<T, R> f) => f(value);

                public static R Twice<T, R>(T value, Func<T, R> f, Func<R, R, R> combine) => combine(f(value), f.Invoke(value));

                public static R Stores<T, R>(T value, Func<T, R> f)
                {
                    s_last = f;
                    return f(value);
                }

                public static R Other<T, R>(T value, T other, Func<T, R> f) => f(other);

                public static R Reassigns<T, R>(T value, Func<T, R> f, Func<T> next)
                {
                    value = next();
                    return f(value);
                }

                private static object? s_last;
            }

            class C
            {
                int Run(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return Helpers.Run(pet, p => p /*suppressed*/switch { Cat => 1, Dog => 2 });
                }

                int Twice(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return Helpers.Twice(pet, p => p /*suppressed*/switch { Cat => 1, Dog => 2 }, (a, b) => a + b);
                }

                int Stores(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return Helpers.Stores(pet, p => p /*warns*/switch { Cat => 1, Dog => 2 });
                }

                int Other(Pet pet, Pet other)
                {
                    if (pet is Bird) return 0;
                    return Helpers.Other(pet, other, p => p /*warns*/switch { Cat => 1, Dog => 2 });
                }

                int Reassigns(Pet pet, Func<Pet> next)
                {
                    if (pet is Bird) return 0;
                    return Helpers.Reassigns(pet, p => p /*warns*/switch { Cat => 1, Dog => 2 }, next);
                }

                // Nothing can be known about what a method from elsewhere does with it.
                int Elsewhere(Pet pet, Func<Pet, Func<Pet, int>, int> run)
                {
                    if (pet is Bird) return 0;
                    return run(pet, p => p /*warns*/switch { Cat => 1, Dog => 2 });
                }
            }
            """
        );

    [Fact]
    public Task Local_function_called_only_after_guards() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, Pet other)
                {
                    if (pet is Bird || other is Bird) return 0;
                    return Local(pet) + Local(other);

                    static int Local(Pet p) => p /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // One call isn't guarded.
                int N(Pet pet, Pet other)
                {
                    if (pet is Bird) return 0;
                    return Local(pet) + Local(other);

                    static int Local(Pet p) => p /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // Turned into a delegate, so it could be called with anything.
                Func<Pet, int> O(Pet pet)
                {
                    if (pet is Bird) return _ => 0;
                    Local(pet);
                    return Local;

                    static int Local(Pet p) => p /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Private_method_called_only_after_guards() =>
        Verify.Suppressions("""
            public class C
            {
                public int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return Describe(pet);
                }

                public int N(Pet pet) => pet is Bird ? 0 : Describe(pet);

                private static int Describe(Pet p) => p /*suppressed*/switch { Cat => 1, Dog => 2 };

                // Calls itself, so its own call would have to be checked against itself.
                private static int Recursive(Pet p, int depth) => depth > 3 ? p /*warns*/switch { Cat => 1, Dog => 2 } : Recursive(p, depth + 1);

                public int O(Pet pet) => pet is Bird ? 0 : Recursive(pet, 0);

                // Public, so it could be called from anywhere.
                public static int Shared(Pet p) => p /*warns*/switch { Cat => 1, Dog => 2 };

                public int P(Pet pet) => pet is Bird ? 0 : Shared(pet);
            }
            """
        );

    [Fact]
    public Task Private_methods_that_call_each_other_from_a_do_loop() =>
        Verify.Suppressions("""
            public class C
            {
                public int M(Pet pet) => pet is Bird ? 0 : Walk(pet, 3);

                private static int Walk(Pet p, int steps)
                {
                    var total = 0;
                    do { total += Step(p, steps); } while (p is not Bird && --steps > 0);
                    return total;
                }

                private static int Step(Pet q, int steps)
                {
                    var legs = q /*suppressed*/switch { Cat => 4, Dog => 4 };
                    return steps > 1 ? legs + Walk(q, steps - 1) : legs;
                }

                // The same, but nothing rules out Bird on the way in.
                public int N(Pet pet) => Wander(pet, 3);

                private static int Wander(Pet p, int steps)
                {
                    var total = 0;
                    do { total += Stray(p, steps); } while (p is not Bird && --steps > 0);
                    return total;
                }

                private static int Stray(Pet q, int steps)
                {
                    var legs = q /*warns*/switch { Cat => 4, Dog => 4 };
                    return steps > 1 ? legs + Wander(q, steps - 1) : legs;
                }
            }
            """
        );
}
