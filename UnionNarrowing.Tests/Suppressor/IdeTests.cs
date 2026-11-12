namespace UnionNarrowing.Tests;

/// <summary>
/// The IDE's "populate switch" suggestions (IDE0072, IDE0010) don't understand unions, and their fix adds a throwing default.
/// On a switch that handles every case that's left, they're suppressed.
/// </summary>
public class IdeTests
{
    [Fact]
    public Task Switch_expression_that_handles_every_case() =>
        Verify.Suppressions("""
            public closed record Shape;
            public record Circle : Shape;
            public record Square : Shape;

            class C
            {
                int M(Pet pet) => pet /*suppressed*/switch { Cat => 1, Dog => 2, Bird => 3 };

                int N(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int O(Shape shape) => shape /*suppressed*/switch { Circle => 1, Square => 2 };
            }
            """, diagnosticId: "IDE0072"
        );

    /// <summary>
    /// CS8509 already reports the missing case, with Add missing arms as its fix.
    /// IDE0072's fix would add a throwing default instead.
    /// </summary>
    [Fact]
    public Task Switch_expression_that_misses_a_case() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet) => pet /*suppressed*/switch { Cat => 1, Dog => 2 };

                // Not a union: the IDE's suggestion stands.
                int N(object value) => value /*warns*/switch { string => 1, int => 2 };
            }
            """, diagnosticId: "IDE0072"
        );

    [Fact]
    public Task Switch_statement_that_handles_every_case() =>
        Verify.Suppressions("""
            class C
            {
                void M(Pet pet)
                {
                    /*suppressed*/switch (pet)
                    {
                        case Cat:
                            Console.WriteLine("cat");
                            break;
                        case Dog or Bird:
                            Console.WriteLine("not a cat");
                            break;
                    }
                }

                void N(Pet pet)
                {
                    if (pet is Bird) return;
                    /*suppressed*/switch (pet)
                    {
                        case Cat:
                            Console.WriteLine("cat");
                            break;
                        case Dog:
                            Console.WriteLine("dog");
                            break;
                    }
                }
            }
            """, diagnosticId: "IDE0010"
        );


    [Fact]
    public Task Switch_statement_whose_sections_assign_what_it_switches_on() =>
        Verify.Suppressions("""
            class C
            {
                static Pet Next() => new Cat("Tom");

                Pet Step(Pet pet)
                {
                    if (pet is Bird) return pet;
                    /*suppressed*/switch (pet)
                    {
                        case Cat:
                            pet = new Dog("Rex");
                            break;
                        case Dog:
                            pet = new Bird("Polly");
                            break;
                    }

                    return pet;
                }

                Pet Run(Pet pet)
                {
                    while (pet is not Bird)
                    {
                        /*suppressed*/switch (pet)
                        {
                            case Cat:
                                pet = new Dog("Rex");
                                break;
                            case Dog:
                                pet = new Bird("Polly");
                                break;
                        }
                    }

                    return pet;
                }

                Pet WrittenBefore(Pet pet, Pet other)
                {
                    if (pet is Bird) return pet;
                    /*because*/pet = other;
                    /*warns*/switch (pet)
                    {
                        case Cat:
                            pet = new Dog("Rex");
                            break;
                        case Dog:
                            pet = new Bird("Polly");
                            break;
                    }

                    return pet;
                }
            }
            """, diagnosticId: "IDE0010"
        );

    [Fact]
    public Task Switch_statement_that_misses_a_case() =>
        Verify.Suppressions("""
            class C
            {
                void M(Pet pet)
                {
                    /*warns*/switch (pet)
                    {
                        case Cat:
                            Console.WriteLine("cat");
                            break;
                    }
                }
            }
            """, diagnosticId: "IDE0010"
        );

    [Fact]
    public Task Switch_statement_a_goto_enters_from_another_section() =>
        Verify.Suppressions("""
            class C
            {
                void M(Pet pet)
                {
                    /*warns*/switch (pet)
                    {
                        case Cat:
                        Inside:
                            /*warns*/switch (pet) { case Cat: break; }
                            break;
                        case Bird:
                            goto Inside;
                    }
                }
            }
            """, diagnosticId: "IDE0010"
        );
}
