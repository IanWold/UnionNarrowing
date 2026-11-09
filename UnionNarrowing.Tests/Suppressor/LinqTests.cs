namespace UnionNarrowing.Tests;

/// <summary>
/// LINQ: what a chain's filters say about its elements, in the lambdas that get them and in the elements it hands back.
/// </summary>
public class LinqTests
{
    [Fact]
    public Task All_and_Any_on_an_immutable_collection() =>
        Verify.Suppressions("""
            using System.Collections.Immutable;
            class C
            {
                int All(ImmutableArray<Pet> pets)
                {
                    if (!pets.All(p => p is not Bird)) return 0;
                    var total = 0;
                    foreach (var p in pets) total += p /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return total + pets.Sum(p => p /*suppressed*/switch { Cat => 1, Dog => 2 }) + pets.First() /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Any(ImmutableList<Pet> pets) => pets.Any(p => p is Bird) ? 0 : pets.Sum(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });

                // A list can change after it's checked.
                int List(List<Pet> pets) => pets.Any(p => p is Bird) ? 0 : pets.Sum(p => p /*warns*/switch { Cat => 1, Dog => 2 });

                int Reassigned(ImmutableArray<Pet> pets, ImmutableArray<Pet> others)
                {
                    if (pets.Any(p => p is Bird)) return 0;
                    pets = others;
                    return pets.Sum(p => p /*warns*/switch { Cat => 1, Dog => 2 });
                }

                // Some aren't all.
                int SomeAreNot(ImmutableArray<Pet> pets) => pets.Any(p => p is not Bird) ? pets.Sum(p => p /*warns*/switch { Cat => 1, Dog => 2 }) : 0;
            }
            """
        );

    [Fact]
    public Task Chains_through_a_local() =>
        Verify.Suppressions("""
            class C
            {
                int Lazy(IEnumerable<Pet> pets)
                {
                    var alive = pets.Where(p => p is not Bird);
                    return alive.Sum(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });
                }

                int ForEach(IEnumerable<Pet> pets)
                {
                    var alive = pets.Where(p => p is not Bird).OrderBy(p => 1);
                    var total = 0;
                    foreach (var p in alive)
                        total += p /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return total;
                }

                // A list can be changed after it's made.
                int Materialized(IEnumerable<Pet> pets)
                {
                    var alive = pets.Where(p => p is not Bird).ToList();
                    alive.Add(new Bird("Polly"));
                    return alive.Sum(p => p /*warns*/switch { Cat => 1, Dog => 2 });
                }

                int Reassigned(IEnumerable<Pet> pets)
                {
                    var alive = pets.Where(p => p is not Bird);
                    alive = pets;
                    return alive.Sum(p => p /*warns*/switch { Cat => 1, Dog => 2 });
                }
            }
            """
        );

    [Fact]
    public Task Deconstructed_foreach_over_a_filtered_chain() =>
        Verify.Suppressions("""
            record Owner(Pet Pet, int Age);

            class C
            {
                int Tuple(IEnumerable<(Pet Pet, int N)> items)
                {
                    var total = 0;
                    foreach (var (pet, n) in items.Where(x => x.Pet is not Bird))
                        total += pet /*suppressed*/switch { Cat => n, Dog => -n };
                    return total;
                }

                int Record(IEnumerable<Owner> owners)
                {
                    var total = 0;
                    foreach (var (pet, age) in owners.Where(o => o.Pet is not Bird))
                        total += pet /*suppressed*/switch { Cat => age, Dog => -age };
                    return total;
                }

                int OtherElement(IEnumerable<(Pet A, Pet B)> items)
                {
                    var total = 0;
                    foreach (var (a, b) in items.Where(x => x.A is not Bird))
                        total += b /*warns*/switch { Cat => 1, Dog => 2 };
                    return total;
                }
            }
            """
        );

    [Fact]
    public Task Elements_handed_back() =>
        Verify.Suppressions("""
            class C
            {
                int First(IEnumerable<Pet> pets) => pets.First(p => p is not Bird) /*suppressed*/switch { Cat => 1, Dog => 2 };
                int Single(IEnumerable<Pet> pets) => pets.Single(p => p is not Bird) /*suppressed*/switch { Cat => 1, Dog => 2 };
                int Last(IEnumerable<Pet> pets) => pets.Last(p => p is not Bird) /*suppressed*/switch { Cat => 1, Dog => 2 };
                int AfterWhere(IEnumerable<Pet> pets) => pets.Where(p => p is not Bird).First() /*suppressed*/switch { Cat => 1, Dog => 2 };
                int ElementAt(IEnumerable<Pet> pets) => pets.Where(p => p is not Bird).ElementAt(2) /*suppressed*/switch { Cat => 1, Dog => 2 };
                int MaxBy(IEnumerable<Pet> pets) => pets.Where(p => p is not Bird).MaxBy(p => 1) /*suppressed*/switch { Cat => 1, Dog => 2 };

                // Could be a default union, which holds no case: not something the switch has to handle, but not a Bird.
                int OrDefault(IEnumerable<Pet> pets) => pets.FirstOrDefault(p => p is not Bird) /*suppressed*/switch { Cat => 1, Dog => 2 };
                int Find(List<Pet> pets) => pets.Find(p => p is not Bird) /*suppressed*/switch { Cat => 1, Dog => 2 };
                int ArrayFind(Pet[] pets) => Array.Find(pets, p => p is not Bird) /*suppressed*/switch { Cat => 1, Dog => 2 };

                int Member(IEnumerable<Owner> owners) => owners.First(o => o.Pet is not Bird).Pet /*suppressed*/switch { Cat => 1, Dog => 2 };

                int NoFilter(IEnumerable<Pet> pets) => pets.First() /*warns*/switch { Cat => 1, Dog => 2 };

                // The fallback could be anything.
                int Fallback(IEnumerable<Pet> pets, Pet fallback) => pets.FirstOrDefault(p => p is not Bird, fallback) /*warns*/switch { Cat => 1, Dog => 2 };
            }

            record Owner(Pet Pet);
            """
        );

    [Fact]
    public Task Immutable_collections_made_from_a_chain() =>
        Verify.Suppressions("""
            using System.Collections.Immutable;
            using System.Collections.Frozen;
            class C
            {
                int Array(IEnumerable<Pet> pets)
                {
                    var kept = pets.Where(p => p is not Bird).ToImmutableArray();
                    var total = 0;
                    foreach (var p in kept) total += p /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return total + kept.Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 }).Sum();
                }

                int Frozen(IEnumerable<Pet> pets)
                {
                    var kept = pets.Where(p => p is not Bird).ToFrozenSet();
                    return kept.Sum(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });
                }

                // A list could be changed after it's made.
                int List(IEnumerable<Pet> pets)
                {
                    var kept = pets.Where(p => p is not Bird).ToList();
                    return kept.Sum(p => p /*warns*/switch { Cat => 1, Dog => 2 });
                }
            }
            """
        );

    [Fact]
    public Task Linq_chains_that_do_not_filter_the_elements() =>
        Verify.Suppressions("""
            class C
            {
                // Concat adds elements the filter never saw.
                IEnumerable<int> N(IEnumerable<Pet> pets, IEnumerable<Pet> more) =>
                    pets.Where(p => p is not Bird).Concat(more).Select(p => p /*warns*/switch { Cat => 1, Dog => 2 });

                IEnumerable<int> O(IEnumerable<Pet> pets) =>
                    pets.Select(p => p /*warns*/switch { Cat => 1, Dog => 2 });
            }
            """
        );

    [Fact]
    public Task Linq_where_then_a_lambda_over_the_elements() =>
        Verify.Suppressions("""
            class C
            {
                IEnumerable<int> M(IEnumerable<Pet> pets) =>
                    pets.Where(p => p is not Bird).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });

                // Through methods that pass the elements on as they are.
                int N(List<Pet> pets) =>
                    pets.Where(p => p is not Bird).OrderBy(p => p.ToString()).Take(10).ToList().Sum(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });

                IEnumerable<int> Query(IEnumerable<Pet> pets) =>
                    from p in pets where p is not Bird select p /*suppressed*/switch { Cat => 1, Dog => 2 };

                int Foreach(IEnumerable<Pet> pets)
                {
                    var total = 0;
                    foreach (var p in pets.Where(p => p is Cat or Dog))
                        total += p /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return total;
                }
            }
            """
        );

    [Fact]
    public Task More_filters() =>
        Verify.Suppressions("""
            using System.Collections.Immutable;

            class C
            {
                static bool IsNotBird(Pet p) => p is not Bird;

                IEnumerable<int> TakeWhile(IEnumerable<Pet> pets) => pets.TakeWhile(p => p is not Bird).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });
                IEnumerable<int> WithIndex(IEnumerable<Pet> pets) => pets.Where((p, i) => p is not Bird).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });
                IEnumerable<int> FindAll(List<Pet> pets) => pets.FindAll(p => p is not Bird).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });
                IEnumerable<int> ArrayFindAll(Pet[] pets) => Array.FindAll(pets, p => p is not Bird).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });
                IEnumerable<int> MethodGroup(IEnumerable<Pet> pets) => pets.Where(IsNotBird).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });
                IEnumerable<int> Parallel(IEnumerable<Pet> pets) => pets.AsParallel().Where(p => p is not Bird).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });
                IEnumerable<int> Immutable(ImmutableArray<Pet> pets) => pets.Where(p => p is not Bird).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });
                IEnumerable<int> Cast(IEnumerable<Pet> pets) => pets.Where(p => p is not Bird).Cast<Pet>().Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });

                // SkipWhile lets everything after the first match through.
                IEnumerable<int> SkipWhile(IEnumerable<Pet> pets) => pets.SkipWhile(p => p is not Bird).Select(p => p /*warns*/switch { Cat => 1, Dog => 2 });
            }
            """
        );

    [Fact]
    public Task Other_lambdas_that_get_elements() =>
        Verify.Suppressions("""
            class C
            {
                IEnumerable<int> Zip(IEnumerable<Pet> a, IEnumerable<Pet> b) =>
                    a.Where(p => p is not Bird).Zip(b.Where(p => p is not Dog), (x, y) => x /*suppressed*/switch { Cat => 1, Dog => 2 } + y /*suppressed*/switch { Cat => 1, Bird => 3 });

                int Aggregate(IEnumerable<Pet> pets) =>
                    pets.Where(p => p is not Bird).Aggregate(0, (total, p) => total + p /*suppressed*/switch { Cat => 1, Dog => 2 });

                IEnumerable<int> SelectMany(IEnumerable<int> ns, IEnumerable<Pet> pets) =>
                    ns.SelectMany(n => pets.Where(p => p is not Bird), (n, p) => p /*suppressed*/switch { Cat => n, Dog => -n });

                IEnumerable<int> Join(IEnumerable<Pet> pets, IEnumerable<Pet> others) =>
                    pets.Where(p => p is not Bird).Join(others, p => 1, o => 1, (p, o) => p /*suppressed*/switch { Cat => 1, Dog => 2 });

                void ForEach(List<Pet> pets) =>
                    pets.Where(p => p is not Bird).ToList().ForEach(p => Console.WriteLine(p /*suppressed*/switch { Cat => 1, Dog => 2 }));

                void ArrayForEach(Pet[] pets) =>
                    Array.ForEach(Array.FindAll(pets, p => p is not Bird), p => Console.WriteLine(p /*suppressed*/switch { Cat => 1, Dog => 2 }));

                bool Exists(List<Pet> pets) => pets.FindAll(p => p is not Bird).Exists(p => p /*suppressed*/switch { Cat => true, Dog => false });

                List<int> ConvertAll(List<Pet> pets) => pets.FindAll(p => p is not Bird).ConvertAll(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });

                // Without a seed, the first parameter is the running total, which starts as the first element.
                Pet AggregateWithoutSeed(IEnumerable<Pet> pets) =>
                    pets.Where(p => p is not Bird).Aggregate((acc, p) => acc /*warns*/switch { Cat => p, Dog => acc });
            }
            """
        );

    [Fact]
    public Task Projections() =>
        Verify.Suppressions("""
            record Owner(Pet Pet);

            class C
            {
                IEnumerable<int> Member(IEnumerable<Owner> owners) =>
                    owners.Where(o => o.Pet is not Bird).Select(o => o.Pet).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });

                IEnumerable<int> Identity(IEnumerable<Pet> pets) =>
                    pets.Where(p => p is not Bird).Select(p => p).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });

                IEnumerable<int> Tuple(IEnumerable<Pet> pets) =>
                    pets.Where(p => p is not Bird).Select(p => (p, 1)).Select(t => t.Item1 /*suppressed*/switch { Cat => 1, Dog => 2 });

                IEnumerable<int> Anonymous(IEnumerable<Pet> pets) =>
                    pets.Where(p => p is not Bird).Select(p => new { Pet = p }).Select(x => x.Pet /*suppressed*/switch { Cat => 1, Dog => 2 });

                static Pet Swap(Pet p) => p;

                // A projection that makes something new.
                IEnumerable<int> New(IEnumerable<Pet> pets) =>
                    pets.Where(p => p is not Bird).Select(p => Swap(p)).Select(p => p /*warns*/switch { Cat => 1, Dog => 2 });

                IEnumerable<int> Prepend(IEnumerable<Pet> pets) =>
                    pets.Where(p => p is not Bird).Prepend(new Bird("Polly")).Select(p => p /*warns*/switch { Cat => 1, Dog => 2 });
            }
            """
        );

    [Fact]
    public Task Query_syntax() =>
        Verify.Suppressions("""
            class C
            {
                IEnumerable<int> Let(IEnumerable<Pet> pets) =>
                    from p in pets where p is not Bird let q = p select q /*suppressed*/switch { Cat => 1, Dog => 2 };

                IEnumerable<int> SecondFrom(IEnumerable<Pet> pets, IEnumerable<int> ns) =>
                    from p in pets where p is not Bird from n in ns select p /*suppressed*/switch { Cat => n, Dog => -n };

                IEnumerable<int> Into(IEnumerable<Pet> pets) =>
                    from p in pets where p is not Bird group p by 1 into g from q in g select q /*suppressed*/switch { Cat => 1, Dog => 2 };
            }
            """
        );

    [Fact]
    public Task Set_operators_and_skipping() =>
        Verify.Suppressions("""
            class C
            {
                IEnumerable<int> Except(IEnumerable<Pet> pets, IEnumerable<Pet> others) =>
                    pets.Where(p => p is not Bird).Except(others).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });

                IEnumerable<int> Intersect(IEnumerable<Pet> pets, IEnumerable<Pet> others) =>
                    pets.Where(p => p is not Bird).Intersect(others).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });

                IEnumerable<int> BothFiltered(IEnumerable<Pet> pets, IEnumerable<Pet> others) =>
                    pets.Where(p => p is not Bird).Concat(others.Where(p => p is Cat or Dog)).Select(p => p /*suppressed*/switch { Cat => 1, Dog => 2 });

                int FirstAfterSkipWhile(IEnumerable<Pet> pets) => pets.SkipWhile(p => p is Bird).First() /*suppressed*/switch { Cat => 1, Dog => 2 };

                // Union brings in the other sequence's elements.
                IEnumerable<int> Union(IEnumerable<Pet> pets, IEnumerable<Pet> others) =>
                    pets.Where(p => p is not Bird).Union(others).Select(p => p /*warns*/switch { Cat => 1, Dog => 2 });

                // Only the first one is sure to be past the skipping.
                int LastAfterSkipWhile(IEnumerable<Pet> pets) => pets.SkipWhile(p => p is Bird).Last() /*warns*/switch { Cat => 1, Dog => 2 };
            }
            """
        );
}
