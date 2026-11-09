namespace UnionNarrowing.Tests;

/// <summary>
/// Unions whose cases are unions, and unions inside a case or inside the object switched on.
/// </summary>
public class NestedTests
{
    private const string Thing = """

        public record Rock;
        public union Thing(Pet, Rock);
        """;

    [Fact]
    public Task Nested_patterns_in_a_tuple_switch() =>
        Verify.Suppressions("""
            record Owner(Pet Pet);

            class C
            {
                int M(Thing t, bool flag)
                {
                    if (t is Rock) return 0;
                    if (t is Pet and Bird) return 1;
                    return (t, flag) /*suppressed*/switch { (Pet and (Cat or Dog), _) => 2 };
                }

                int N(Owner a, Owner b)
                {
                    if (a.Pet is Bird || b.Pet is Bird) return 0;
                    return (a, b) /*suppressed*/switch { ({ Pet: Cat or Dog }, { Pet: Cat or Dog }) => 1 };
                }

                // b's Pet could be a Bird.
                int O(Owner a, Owner b)
                {
                    if (a.Pet is Bird) return 0;
                    return (a, b) /*warns*/switch { ({ Pet: Cat or Dog }, { Pet: Cat or Dog }) => 1 };
                }

                int EitherTuplePattern(Owner a, Owner b)
                {
                    if (a.Pet is Bird) return 0;
                    return (a, b) /*suppressed*/switch { ({ Pet: Cat }, _) or ({ Pet: Dog }, _) => 1 };
                }

                // A Dog whose owner's partner has a Dog or a Bird is left.
                int EitherTuplePatternPartly(Owner a, Owner b)
                {
                    if (a.Pet is Bird) return 0;
                    return (a, b) /*warns*/switch { ({ Pet: Cat }, _) or ({ Pet: Dog }, { Pet: Cat }) => 1 };
                }
            }
            """ + Thing
        );

    /// <summary>
    /// In `Pet and Bird`, Bird tests the Pet inside, not the Thing.
    /// </summary>
    [Fact]
    public Task Pattern_on_the_union_inside_says_nothing_about_the_outer_one() =>
        Verify.Suppressions("""
            class C
            {
                int M(Thing t)
                {
                    if (t is Pet and Bird) return t /*warns*/switch { Rock => 0 };
                    return 1;
                }
            }
            """ + Thing
        );

    [Fact]
    public Task Union_member_of_a_case() =>
        Verify.Suppressions("""
            class C
            {
                int M(Young y)
                {
                    if (y is Kitten { Toy: Ball }) return 0;
                    return y /*suppressed*/switch { Kitten { Toy: Mouse } => 1, Dog => 2 };
                }

                int N(Young y)
                {
                    if (y is Kitten(Ball)) return 0;
                    return y /*suppressed*/switch { Kitten(Mouse) => 1, Dog => 2 };
                }

                int O(Young y)
                {
                    if (y is Kitten { Toy: Ball }) return 0;
                    return y /*warns*/switch { Kitten { Toy: Mouse } => 1 };
                }

                // The guard is about a Kitten in another Young.
                int P(Young y, Young other)
                {
                    if (other is Kitten { Toy: Ball }) return 0;
                    return y /*warns*/switch { Kitten { Toy: Mouse } => 1, Dog => 2 };
                }
            }
            """ + Snippets.Toys
        );

    [Fact]
    public Task Union_member_of_a_closed_hierarchy_case() =>
        Verify.Suppressions("""
            public record Ball;
            public record Mouse;
            public union Toy(Ball, Mouse);
            public closed record Animal;
            public record Kitten(Toy Toy) : Animal;
            public record Puppy : Animal;

            class C
            {
                int M(Animal animal)
                {
                    if (animal is Kitten { Toy: Ball }) return 0;
                    return animal /*suppressed*/switch { Kitten { Toy: Mouse } => 1, Puppy => 2 };
                }

                int N(Animal animal) => animal /*warns*/switch { Kitten { Toy: Mouse } => 1, Puppy => 2 };
            }
            """
        );

    [Fact]
    public Task Union_member_of_the_object_switched_on() =>
        Verify.Suppressions("""
            record Owner(Pet Pet, int Age);

            class C
            {
                int M(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    return owner /*suppressed*/switch { { Pet: Cat } => 1, { Pet: Dog } => 2 };
                }

                int N(Owner owner)
                {
                    if (owner is { Pet: Bird }) return 0;
                    return owner /*suppressed*/switch { (Cat, _) => 1, (Dog, var age) => age };
                }

                // Could be null.
                int O(Owner? owner)
                {
                    if (owner?.Pet is Bird) return 0;
                    return owner /*warns*/switch { { Pet: Cat } => 1, { Pet: Dog } => 2 };
                }

                int P(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    return owner /*warns*/switch { { Pet: Cat } => 1, { Pet: Dog, Age: > 1 } => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Union_whose_case_is_a_union() =>
        Verify.Suppressions("""
            class C
            {
                int M(Thing t)
                {
                    if (t is Rock) return 0;
                    if (t is Pet and Bird) return 1;
                    return t /*suppressed*/switch { Pet and (Cat or Dog) => 2 };
                }

                int N(Thing t)
                {
                    if (t is Pet and Bird) return 1;
                    return t /*suppressed*/switch { Pet and (Cat or Dog) => 2, Rock => 3 };
                }

                // A Bird in a Thing gets past.
                int O(Thing t)
                {
                    if (t is Rock) return 0;
                    return t /*warns*/switch { Pet and (Cat or Dog) => 2 };
                }

                // Only some Birds are ruled out.
                int P(Thing t, bool flag)
                {
                    if (t is Pet and Bird && flag) return 1;
                    return t /*warns*/switch { Pet and (Cat or Dog) => 2, Rock => 3 };
                }
            }
            """ + Thing
        );
}
