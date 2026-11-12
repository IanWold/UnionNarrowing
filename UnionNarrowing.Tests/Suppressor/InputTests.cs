namespace UnionNarrowing.Tests;

/// <summary>
/// Switch inputs that aren't a plain variable or member:
/// casts, conditionals, and tuple-typed variables.
/// </summary>
public class InputTests
{
    [Fact]
    public Task Conditional_and_coalesce() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet a, Pet b, bool flag)
                {
                    if (a is Bird || b is Bird) return 0;
                    return (flag ? a : b) /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // Each branch gets what its condition says, and the last is a Cat.
                int N(Pet a, Pet b)
                {
                    return (a is not Bird ? a : b is not Bird ? b : new Cat("Tom")) /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int NotEveryBranch(Pet a, Pet b)
                {
                    return (a is not Bird ? a : b) /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int O(Pet a, Pet b)
                {
                    if (b is Bird) return 0;
                    return (a is not Bird ? a : b) /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int P(Pet? maybe, Pet fallback)
                {
                    if (maybe is Bird || fallback is Bird) return 0;
                    return (maybe ?? fallback) /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // Only one side is narrowed.
                int Q(Pet a, Pet b, bool flag)
                {
                    if (a is Bird) return 0;
                    return (flag ? a : b) /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Boxed_union() =>
        Verify.Suppressions("""
            class C
            {
                // A type pattern on an object tests the box, which holds a Pet, never a Bird: the Bird arm says nothing
                // about what the Pet holds.
                int Arm(object message) =>
                    message switch { Bird => 0, Pet and var pet => pet /*warns*/switch { Cat => 1, Dog => 2 }, _ => -1 };

                int Condition(object o)
                {
                    if (o is not Bird and Pet and var pet) return pet /*warns*/switch { Cat => 1, Dog => 2 };
                    return 0;
                }

                int Copy(object o)
                {
                    if (o is Bird or not Pet) return 0;
                    return o is Pet and var pet ? pet /*warns*/switch { Cat => 1, Dog => 2 } : 0;
                }

                int Loop(System.Collections.Generic.Queue<object> q)
                {
                    var n = 0;
                    while (q.TryDequeue(out var next) && next is not Bird and Pet and var pet) n += pet /*warns*/switch { Cat => 1, Dog => 2 };
                    return n;
                }

                // Once it's a Pet, patterns test what it holds.
                int Unboxed(object o)
                {
                    if (o is Pet pet && pet is not Bird) return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return 0;
                }

                int OfValue(Pet pet)
                {
                    if (pet.Value is Bird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Identity_cast() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return ((Pet)pet) /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );
}
