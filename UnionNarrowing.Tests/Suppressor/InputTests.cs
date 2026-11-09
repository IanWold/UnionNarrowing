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
