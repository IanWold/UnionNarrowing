namespace UnionNarrowing.Tests;

/// <summary>
/// With <c>unionnarrowing.follow_mutable_state = true</c>, values that only something else could change (another thread, or code that runs between) are followed too, as long as nothing that could run other code comes between the test and the switch.
/// </summary>
public class MutableStateTests
{
    /// <summary>
    /// What the option follows, with <c>{0}</c> for the markers:
    /// <c>suppressed</c> with it, <c>warns</c> without.
    /// </summary>
    private const string Followed = """
        class C
        {
            private Pet _state;
            public Pet State { get; set; }
            private static Pet s_shared;

            int Field()
            {
                if (_state is Bird) return 0;
                return _state /*{0}*/switch { Cat => 1, Dog => 2 };
            }

            int Property(C other)
            {
                if (other.State is Bird) return 0;
                return other.State /*{0}*/switch { Cat => 1, Dog => 2 };
            }

            int Static()
            {
                if (s_shared is Bird) return 0;
                return s_shared /*{0}*/switch { Cat => 1, Dog => 2 };
            }

            // What the arms run comes after the switch reads _state.
            int CallInAnArm()
            {
                if (_state is Bird) return 0;
                return _state /*{0}*/switch { Cat c => Describe(c), Dog => 2 };
            }

            int Describe(Cat cat) => cat.Name.Length;

            // Another reference to the same storage could write it.
            int Ref(ref Pet pet)
            {
                if (pet is Bird) return 0;
                return pet /*{0}*/switch { Cat => 1, Dog => 2 };
            }

            int RefReadonly(ref readonly Pet pet)
            {
                if (pet is Bird) return 0;
                return pet /*{0}*/switch { Cat => 1, Dog => 2 };
            }

            int In(in Pet pet)
            {
                if (pet is Bird) return 0;
                return pet /*{0}*/switch { Cat => 1, Dog => 2 };
            }
        }
        """;

    private static readonly Dictionary<string, string> FollowMutableState = new() { ["unionnarrowing.follow_mutable_state"] = "true" };

    /// <summary>
    /// Everything else that could run other code, take a reference, or write what the value is read through, between the test and the switch.
    /// </summary>
    [Fact]
    public Task Anything_else_that_could_change_it_in_between() =>
        Verify.Suppressions("""
            struct Money
            {
                public int Cents;
                public static Money operator +(Money a, Money b) => new() { Cents = a.Cents + b.Cents };
                public static implicit operator int(Money m) => m.Cents;
            }

            class C : IDisposable
            {
                private Pet _state;
                private Money _a, _b;
                private readonly List<int> _list = [1];
                private readonly Pet[] _pets = [new Cat("Tom")];
                public event Action? Changed;
                public void Dispose() { }

                int Creation()
                {
                    if (_state is Bird) return 0;
                    /*because*/var o = new object();
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Interpolation()
                {
                    if (_state is Bird) return 0;
                    /*because*/var s = $"{_a.Cents}";
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Operator()
                {
                    if (_state is Bird) return 0;
                    /*because*/var sum = _a + _b;
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Conversion()
                {
                    if (_state is Bird) return 0;
                    /*because*/int cents = _a;
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int CompoundAssignment()
                {
                    if (_state is Bird) return 0;
                    /*because*/_a += _b;
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Indexer()
                {
                    if (_state is Bird) return 0;
                    /*because*/var first = _list[0];
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Foreach()
                {
                    if (_state is Bird) return 0;
                    /*because*/foreach (var n in _list) { }
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Using()
                {
                    if (_state is Bird) return 0;
                    /*because*/using (this) { }
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Event()
                {
                    if (_state is Bird) return 0;
                    /*because*/Changed += () => { };
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Reference()
                {
                    if (_state is Bird) return 0;
                    /*because*/ref var cents = ref _a.Cents;
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // Any array element could be what's read, through another array variable.
                int ArrayElement()
                {
                    if (_state is Bird) return 0;
                    /*because*/_pets[0] = new Cat("Tom");
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                IEnumerable<int> Yield()
                {
                    if (_state is Bird) yield break;
                    /*because*/yield return 0;
                    yield return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """, editorConfig: FollowMutableState
        );

    [Fact]
    public Task Array_elements_and_references() =>
        Verify.Suppressions("""
            class C
            {
                int Array(Pet[] pets)
                {
                    var total = 0;
                    for (var i = 0; i < pets.Length; i++)
                    {
                        if (pets[i] is Bird) continue;
                        total += pets[i] /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                    return total;
                }

                int ElementWritten(Pet[] pets, int i)
                {
                    if (pets[i] is Bird) return 0;
                    /*because*/pets[0] = new Bird("Polly");
                    return pets[i] /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int IndexWritten(Pet[] pets, int i)
                {
                    if (pets[i] is Bird) return 0;
                    /*because*/i++;
                    return pets[i] /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int RefLocal(Pet[] pets)
                {
                    ref var first = ref pets[0];
                    if (first is Bird) return 0;
                    return first /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """, editorConfig: FollowMutableState
        );

    [Fact]
    public Task Array_elements_at_a_constant_index() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet[] pets) => pets[0] is Bird ? 0 : pets[0] /*suppressed*/switch { Cat => 1, Dog => 2 };

                int N(Pet[] pets)
                {
                    if (pets[0] is Bird) return 0;
                    pets[1] = new Bird("Polly");
                    return pets[0] /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """, editorConfig: FollowMutableState
        );

    [Fact]
    public Task Elements_at_a_constant_index() =>
        Verify.Suppressions("""
            using System.Collections.Immutable;

            class C
            {
                int Immutable(ImmutableArray<Pet> pets) => pets[0] is Bird ? 0 : pets[0] /*suppressed*/switch { Cat => 1, Dog => 2 };

                // Another element.
                int Other(ImmutableArray<Pet> pets) => pets[0] is Bird ? 0 : pets[1] /*warns*/switch { Cat => 1, Dog => 2 };

                // An array only with the option.
                int Array(Pet[] pets) => pets[0] is Bird ? 0 : pets[0] /*warns*/switch { Cat => 1, Dog => 2 };
            }
            """
        );

    /// <summary>
    /// An ImmutableArray never changes, so its elements are followed without the option.
    /// </summary>
    [Fact]
    public Task Immutable_array_elements() =>
        Verify.Suppressions("""
            using System.Collections.Immutable;

            class C
            {
                int M(ImmutableArray<Pet> pets)
                {
                    var total = 0;
                    for (var i = 0; i < pets.Length; i++)
                    {
                        if (pets[i] is Bird) continue;
                        total += pets[i] /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                    return total;
                }

                int Array(Pet[] pets, int i)
                {
                    if (pets[i] is Bird) return 0;
                    return pets[i] /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Mutable_fields_properties_and_references() =>
        Verify.Suppressions(Followed.Replace("{0}", "suppressed"), editorConfig: FollowMutableState);

    [Fact]
    public Task Mutable_fields_properties_and_references_are_not_followed_by_default() =>
        Verify.Suppressions(Followed.Replace("{0}", "warns"));

    /// <summary>
    /// Arms that look inside the input at a member that isn't readonly:
    /// the member is followed the same way as when it's switched on itself.
    /// </summary>
    [Fact]
    public Task Nested_patterns_on_a_member_that_isnt_readonly() =>
        Verify.Suppressions("""
            class Owner
            {
                public Pet Pet;
            }

            class C
            {
                int M(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    /*because*/Console.WriteLine("anything could happen");
                    return owner /*warns*/switch { { Pet: Cat } => 1, { Pet: Dog } => 2 };
                }
            }
            """, editorConfig: FollowMutableState
        );

    /// <summary>
    /// What's tested in one function doesn't carry into another for a value that only something else could change:
    /// other code can run between them, like the rest of a LINQ chain.
    /// </summary>
    [Fact]
    public Task Not_followed_into_other_functions() =>
        Verify.Suppressions("""
            class Owner
            {
                public Pet Pet;
            }

            class C
            {
                IEnumerable<int> Lambda(List<Owner> owners) =>
                    owners.Where(o => o.Pet is not Bird).ToList().Select(o => o.Pet /*warns*/switch { Cat => 1, Dog => 2 });

                IEnumerable<int> Selected(List<Owner> owners) =>
                    owners.Where(o => o.Pet is not Bird).Select(o => o.Pet).ToList().Select(p => p /*warns*/switch { Cat => 1, Dog => 2 });

                int Loop(List<Owner> owners)
                {
                    var total = 0;
                    foreach (var o in owners.Where(o => o.Pet is not Bird).ToList())
                        total += o.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                    return total;
                }

                int First(List<Owner> owners)
                {
                    var owner = owners.Where(o => o.Pet is not Bird).OrderBy(o => o.Pet.GetHashCode()).First();
                    return owner.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Caller(Owner owner) => owner.Pet is Bird ? 0 : Legs(owner);

                private static int Legs(Owner o) => o.Pet /*warns*/switch { Cat => 4, Dog => 4 };
            }
            """, editorConfig: FollowMutableState
        );

    [Fact]
    public Task Something_that_could_change_it_in_between() =>
        Verify.Suppressions("""
            class C
            {
                private Pet _state;
                public Pet State { get; set; }
                public Pet Computed => _state;

                int Call()
                {
                    if (_state is Bird) return 0;
                    /*because*/Console.WriteLine("anything could happen");
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Write(C other)
                {
                    if (_state is Bird) return 0;
                    /*because*/other._state = new Bird("Polly");
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Getter()
                {
                    if (_state is Bird) return 0;
                    /*because*/var computed = Computed;
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                async System.Threading.Tasks.Task<int> Await()
                {
                    if (_state is Bird) return 0;
                    /*because*/await System.Threading.Tasks.Task.Yield();
                    return _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                Func<int> Lambda()
                {
                    if (_state is Bird) return () => 0;
                    return () => _state /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Loop(int n)
                {
                    var total = 0;
                    if (_state is Bird) return 0;
                    for (var i = 0; i < n; i++)
                    {
                        total += _state /*warns*/switch { Cat => 1, Dog => 2 };
                        /*because*/Reset();
                    }
                    return total;
                }

                void Reset() => _state = new Bird("Polly");

                // reset could write pet through another reference to the same storage.
                int RefParameter(ref Pet pet, Action reset)
                {
                    if (pet is Bird) return 0;
                    /*because*/reset();
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """, editorConfig: FollowMutableState
        );

    [Fact]
    public Task This_in_a_union_member() =>
        Verify.Suppressions("""
            union Animal(Cat, Dog, Bird)
            {
                public int Legs()
                {
                    if (this is Bird) return 2;
                    return this /*suppressed*/switch { Cat => 4, Dog => 4 };
                }

                public int Reassigned()
                {
                    if (this is Bird) return 2;
                    /*because*/this = new Animal(new Bird("Polly"));
                    return this /*warns*/switch { Cat => 4, Dog => 4 };
                }

                public int Call()
                {
                    if (this is Bird) return 2;
                    /*because*/Console.WriteLine();
                    return this /*warns*/switch { Cat => 4, Dog => 4 };
                }
            }
            """, editorConfig: FollowMutableState
        );
}
