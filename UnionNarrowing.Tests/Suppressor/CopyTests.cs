namespace UnionNarrowing.Tests;

/// <summary>
/// What's known about a value carries over to a copy of it.
/// </summary>
public class CopyTests
{
    [Fact]
    public Task Chain_of_copies() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    var a = pet;
                    var b = a;
                    if (pet is Bird) return 0;
                    return b /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Copies_bound_inside_subpatterns() =>
        Verify.Suppressions("""
            record Owner(Pet Pet, int Age);

            class C
            {
                int Property(Owner owner) => owner switch
                {
                    { Pet: not Bird and var p } => p /*suppressed*/switch { Cat => 1, Dog => 2 },
                    _ => 0,
                };

                int Positional(Owner owner) => owner is (not Bird and var p, _) ? p /*suppressed*/switch { Cat => 1, Dog => 2 } : 0;

                int Tuple(Pet a, Pet b) => (a, b) is (not Bird and var x, _) ? x /*suppressed*/switch { Cat => 1, Dog => 2 } : 0;

                int InACase(Young y) => y is Kitten { Toy: not Ball and var toy } ? toy /*suppressed*/switch { Mouse => 1 } : 0;

                int Guarded(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    return owner is { Pet: var p } ? p /*suppressed*/switch { Cat => 1, Dog => 2 } : 0;
                }

                // The other element.
                int Other(Pet a, Pet b) => (a, b) is (not Bird, var y) ? y /*warns*/switch { Cat => 1, Dog => 2 } : 0;
            }
            """ + Snippets.Toys
        );

    [Fact]
    public Task Copies_made_by_deconstruction() =>
        Verify.Suppressions("""
            record Owner(Pet Pet, int N);

            class C
            {
                int Literal(Pet pet)
                {
                    if (pet is Bird) return 0;
                    var (x, n) = (pet, 1);
                    return x /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Record(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    var (x, n) = owner;
                    return x /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // owner doesn't change after the copy, so a guard on owner.Pet afterwards holds for x too.
                int GuardAfter(Owner owner)
                {
                    var (x, n) = owner;
                    if (owner.Pet is Bird) return 0;
                    return x /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Tuple(Pet pet)
                {
                    var t = (pet, 1);
                    if (t.Item1 is Bird) return 0;
                    (Pet x, int n) = t;
                    return x /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Nested(Pet pet, Pet other)
                {
                    if (other is Bird) return 0;
                    var (a, (b, c)) = (pet, (other, 1));
                    return b /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // The wrong element.
                int Other(Pet pet, Pet other)
                {
                    if (pet is Bird) return 0;
                    var (x, y) = (pet, other);
                    return y /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Copies_made_by_typed_designations() =>
        Verify.Suppressions("""
            record Owner(Pet Pet);
            public closed record Shape;
            public record Circle : Shape;
            public closed record Polygon : Shape;
            public record Square : Polygon;
            public record Triangle : Polygon;

            class C
            {
                int SameObject(object value, Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    if (owner is Owner o) return o.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return -1;
                }

                int Closed(Shape shape)
                {
                    if (shape is Square) return 0;
                    if (shape is Polygon p) return p /*suppressed*/switch { Triangle => 1 };
                    return -1;
                }

                // A case taken out of a union isn't the union.
                int Case(Pet pet, Pet other)
                {
                    if (pet is Cat c) return other /*warns*/switch { Cat => 1, Dog => 2 };
                    return -1;
                }
            }
            """
        );

    [Fact]
    public Task Copies_made_by_var_patterns() =>
        Verify.Suppressions("""
            class C
            {
                int Arm(Pet pet) => pet switch
                {
                    Bird => 0,
                    var other => other /*suppressed*/switch { Cat => 1, Dog => 2 },
                };

                int IsVar(Pet pet)
                {
                    if (pet is Bird) return 0;
                    if (pet is var copy) return copy /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return -1;
                }

                int Case(Pet pet)
                {
                    switch (pet)
                    {
                        case Bird:
                            return 0;
                        case var other:
                            return other /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                }

                // The copy is written afterwards.
                int Written(Pet pet, Func<Pet> next)
                {
                    if (pet is Bird) return 0;
                    if (pet is var copy)
                    {
                        /*because*/copy = next();
                        return copy /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                    return -1;
                }
            }
            """
        );

    [Fact]
    public Task Copy_of_what_a_member_belongs_to() =>
        Verify.Suppressions("""
            record Owner(Pet Pet);

            class C
            {
                int M(Owner owner)
                {
                    var same = owner;
                    if (owner.Pet is Bird) return 0;
                    return same.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Copy_written_after_the_copy() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, Func<Pet> next)
                {
                    if (pet is Bird) return 0;
                    var local = pet;
                    /*because*/local = next();
                    return local /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_on_the_source_after_the_copy() =>
        Verify.Suppressions("""
            class C
            {
                // pet doesn't change after the copy, so the two still match at the switch.
                int M(Pet pet)
                {
                    var local = pet;
                    if (pet is Bird) return 0;
                    return local /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // pet changes after the copy, so what's known about it afterwards isn't about local.
                int N(Pet pet, Func<Pet> next)
                {
                    var local = pet;
                    /*because*/pet = next();
                    if (pet is Bird) return 0;
                    return local /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_on_the_source_before_the_copy() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    var local = pet;
                    return local /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet pet, Pet other)
                {
                    if (pet is Bird) return 0;
                    other = pet;
                    return other /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                IEnumerable<int> O(Pet pet, int[] counts)
                {
                    if (pet is Bird) return [];
                    var local = pet;
                    return counts.Select(n => local /*suppressed*/switch { Cat => n, Dog => -n });
                }
            }
            """
        );
}
