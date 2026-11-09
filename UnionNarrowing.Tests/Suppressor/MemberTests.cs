namespace UnionNarrowing.Tests;

/// <summary>
/// Switches over members that can't change once constructed:
/// readonly fields and get-only auto-properties.
/// </summary>
public class MemberTests
{
    [Fact]
    public Task Field_keyword_properties() =>
        Verify.Suppressions("""
            class Owner
            {
                public Pet Pet { get => field; } = new Cat("Tom");
                public Pet Spare { get { return field; } init => field = value; }
                public Pet Lazy { get => field; set => field = value; }
            }

            class C
            {
                int M(Owner owner)
                {
                    if (owner.Pet is Bird || owner.Spare is Bird) return 0;
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 }
                        + owner.Spare /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Owner owner)
                {
                    if (owner.Lazy is Bird) return 0;
                    return owner.Lazy /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Fields_of_a_struct_in_a_readonly_field() =>
        Verify.Suppressions("""
            struct Box { public Pet Pet; }

            class C(Box box)
            {
                private readonly Box _box = box;
                private Box _mutable = box;

                int M()
                {
                    if (_box.Pet is Bird) return 0;
                    return _box.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N()
                {
                    if (_mutable.Pet is Bird) return 0;
                    return _mutable.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Fields_of_struct_values_and_tuple_elements() =>
        Verify.Suppressions("""
            struct Box { public Pet Pet; public int N; }
            struct Outer { public Box Inner; }
            record struct Entry(Pet Pet, int N);

            class C
            {
                (Pet Pet, int N) Get() => (new Cat("Tom"), 1);

                int Local(Pet pet)
                {
                    var t = (pet, 1);
                    if (t.Item1 is Bird) return 0;
                    return t.Item1 /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int FromACall()
                {
                    var r = Get();
                    if (r.Pet is Bird) return 0;
                    return r.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // The element by name and by position is the same field.
                int ByNameAndPosition()
                {
                    var r = Get();
                    if (r.Pet is Bird) return 0;
                    return r.Item1 /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Struct(Box box, Outer outer)
                {
                    if (box.Pet is Bird || outer.Inner.Pet is Bird) return 0;
                    return box.Pet /*suppressed*/switch { Cat => 1, Dog => 2 }
                        + outer.Inner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Parameter((Pet Pet, int N) t)
                {
                    if (t.Pet is Bird) return 0;
                    return t.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int ForEach(IEnumerable<(Pet Pet, int N)> items)
                {
                    var sum = 0;
                    foreach (var item in items)
                    {
                        if (item.Pet is Bird) continue;
                        sum += item.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                    return sum;
                }

                // A record struct's positional properties have setters, but setting one writes the struct.
                int RecordStruct(Entry entry)
                {
                    if (entry.Pet is Bird) return 0;
                    return entry.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                IEnumerable<int> Linq(IEnumerable<(Pet Pet, int N)> items) =>
                    items.Where(x => x.Pet is not Bird).Select(x => x.Pet /*suppressed*/switch { Cat => 1, Dog => 2 });
            }
            """
        );

    [Fact]
    public Task Get_only_and_init_auto_properties() =>
        Verify.Suppressions("""
            class Owner
            {
                public Pet Pet { get; } = new Cat("Tom");
                public required Pet Spare { get; init; }
            }

            class C
            {
                int M(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Owner owner)
                {
                    if (owner.Spare is Bird) return 0;
                    return owner.Spare /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Getters_that_return_a_member_that_cant_change() =>
        Verify.Suppressions("""
            class Owner(Pet pet)
            {
                private static readonly Pet s_default = new Cat("Tom");
                private readonly Pet _pet = pet;
                private Pet _mutable = pet;

                public Pet Pet => _pet;
                public Pet Block { get { return _pet; } }
                public Pet Alias => Pet;
                public Pet ThisPet => this._pet;
                public static Pet Default => s_default;

                public Pet Mutable => _mutable;
                public Pet Fresh => new Cat("Tom");
                public virtual Pet Overridable => _pet;
                public void Swap() => _mutable = new Bird("Polly");
            }

            class C
            {
                int M(Owner owner)
                {
                    if (owner.Pet is Bird || owner.Block is Bird || owner.Alias is Bird || owner.ThisPet is Bird || Owner.Default is Bird) return 0;
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 }
                        + owner.Block /*suppressed*/switch { Cat => 1, Dog => 2 }
                        + owner.Alias /*suppressed*/switch { Cat => 1, Dog => 2 }
                        + owner.ThisPet /*suppressed*/switch { Cat => 1, Dog => 2 }
                        + Owner.Default /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Owner owner)
                {
                    if (owner.Mutable is Bird || owner.Fresh is Bird || owner.Overridable is Bird) return 0;
                    return owner.Mutable /*warns*/switch { Cat => 1, Dog => 2 }
                        + owner.Fresh /*warns*/switch { Cat => 1, Dog => 2 }
                        + owner.Overridable /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Key_value_pairs_and_system_tuples() =>
        Verify.Suppressions("""
            class C
            {
                int Guard(KeyValuePair<string, Pet> entry)
                {
                    if (entry.Value is Bird) return 0;
                    return entry.Value /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Positional(KeyValuePair<string, Pet> entry)
                {
                    if (entry is (_, Bird)) return 0;
                    return entry.Value /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Filtered(Dictionary<string, Pet> byName)
                {
                    var total = 0;
                    foreach (var entry in byName.Where(e => e.Value is not Bird))
                        total += entry.Value /*suppressed*/switch { Cat => 1, Dog => 2 };
                    foreach (var (name, pet) in byName.Where(e => e.Value is not Bird))
                        total += pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return total;
                }

                int Tuple(Tuple<Pet, int> pair)
                {
                    if (pair.Item1 is Bird) return 0;
                    return pair.Item1 /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Reassigned(KeyValuePair<string, Pet> entry, KeyValuePair<string, Pet> other)
                {
                    if (entry.Value is Bird) return 0;
                    /*because*/entry = other;
                    return entry.Value /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // A dictionary's indexer gives whatever is there now.
                int Indexer(Dictionary<string, Pet> byName)
                {
                    if (byName["Tom"] is Bird) return 0;
                    return byName["Tom"] /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Members_every_constructor_sets() =>
        Verify.Suppressions("""
            class Owner
            {
                private readonly Pet _pet;
                public Pet Pet { get; }

                public Owner(Pet pet)
                {
                    if (pet is Bird) throw new ArgumentException("No birds.");
                    _pet = pet;
                    Pet = pet;
                }

                public Owner(Dog dog)
                {
                    _pet = dog;
                    Pet = dog;
                }

                public int Legs() => _pet /*suppressed*/switch { Cat => 4, Dog => 4 };
                public int Paws() => Pet /*suppressed*/switch { Cat => 4, Dog => 4 };
            }

            class Chained
            {
                private readonly Pet _pet;
                public Chained(Pet pet) { if (pet is Bird) throw new ArgumentException(); this._pet = pet; }
                public Chained() : this(new Cat("Tom")) { }
                public int Legs() => _pet /*suppressed*/switch { Cat => 4, Dog => 4 };
            }

            // A constructor that doesn't check.
            class Unchecked
            {
                private readonly Pet _pet;
                public Unchecked(Pet pet) { if (pet is Bird) throw new ArgumentException(); _pet = pet; }
                public Unchecked(Pet pet, int legs) { _pet = pet; }
                public int Legs() => _pet /*warns*/switch { Cat => 4, Dog => 4 };
            }

            // Set on some paths through the constructor.
            class Branches
            {
                private readonly Pet _pet;
                public Branches(Pet pet, bool keep) { if (pet is Bird) throw new ArgumentException(); if (keep) _pet = pet; else _pet = new Bird("Polly"); }
                public int Legs() => _pet /*warns*/switch { Cat => 4, Dog => 4 };
            }

            // An init accessor can set a readonly field too.
            class Initialized
            {
                private readonly Pet _pet;
                public Initialized(Pet pet) { if (pet is Bird) throw new ArgumentException(); _pet = pet; }
                public Pet Override { init => _pet = value; }
                public int Legs() => _pet /*warns*/switch { Cat => 4, Dog => 4 };
            }

            // And an object initializer an init-only property.
            class InitOnly
            {
                public Pet Pet { get; init; }
                public InitOnly(Pet pet) { if (pet is Bird) throw new ArgumentException(); Pet = pet; }
                public int Legs() => Pet /*warns*/switch { Cat => 4, Dog => 4 };
            }

            // The default constructor sets nothing.
            class Defaulted
            {
                private readonly Pet _pet;
                public int Legs() => _pet /*warns*/switch { Cat => 4, Dog => 4 };
            }
            """
        );

    [Fact]
    public Task Members_that_can_change() =>
        Verify.Suppressions("""
            class Owner
            {
                public Pet Settable { get; set; }
                public virtual Pet Virtual { get; } = new Cat("Tom");
                public Pet Computed => Settable;
            }

            class C
            {
                int Settable(Owner owner)
                {
                    if (owner.Settable is Bird) return 0;
                    return owner.Settable /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Virtual(Owner owner)
                {
                    if (owner.Virtual is Bird) return 0;
                    return owner.Virtual /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // It returns a settable property.
                int Computed(Owner owner)
                {
                    if (owner.Computed is Bird) return 0;
                    return owner.Computed /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Positional_record_properties_and_chains() =>
        Verify.Suppressions("""
            record Owner(Pet Pet);
            record Household(Owner Owner);
            readonly record struct Box(Pet Pet);

            class C
            {
                int M(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Household household)
                {
                    if (household.Owner.Pet is Bird) return 0;
                    return household.Owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int O(Box box)
                {
                    if (box.Pet is Bird) return 0;
                    return box.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Properties_declared_in_another_assembly() =>
        Verify.Suppressions("""
            class C
            {
                int M(External.Owner owner)
                {
                    if (owner.Pet is External.Bird) return 0;
                    return owner.Pet /*suppressed*/switch { External.Cat => 1, External.Dog => 2 };
                }

                int N(External.Owner owner)
                {
                    if (owner.Settable is External.Bird) return 0;
                    return owner.Settable /*warns*/switch { External.Cat => 1, External.Dog => 2 };
                }

                int O(External.Owner owner)
                {
                    if (owner.Computed is External.Bird) return 0;
                    return owner.Computed /*warns*/switch { External.Cat => 1, External.Dog => 2 };
                }

                int P(External.Owner owner)
                {
                    if (owner.Virtual is External.Bird) return 0;
                    return owner.Virtual /*warns*/switch { External.Cat => 1, External.Dog => 2 };
                }

                int R(External.Owner owner)
                {
                    if (owner.Fixed is External.Bird || owner.Initialized is External.Bird) return 0;
                    return owner.Fixed /*suppressed*/switch { External.Cat => 1, External.Dog => 2 }
                        + owner.Initialized /*suppressed*/switch { External.Cat => 1, External.Dog => 2 };
                }

                // The private setter isn't visible from here, but Reset can call it.
                int Q(External.Owner owner)
                {
                    if (owner.PrivatelySet is External.Bird) return 0;
                    owner.Reset();
                    return owner.PrivatelySet /*warns*/switch { External.Cat => 1, External.Dog => 2 };
                }
            }
            """,
            references: [Verify.CompileToMetadata("""
                namespace External;
                public record Cat;
                public record Dog;
                public record Bird;
                public union Pet(Cat, Dog, Bird);
                public record Owner(Pet Pet)
                {
                    public Pet Settable { get; set; } = Pet;
                    public Pet Computed => Settable;
                    public virtual Pet Virtual { get; } = Pet;
                    public Pet PrivatelySet { get; private set; } = Pet;
                    public Pet Fixed { get; } = Pet;
                    public Pet Initialized { get; init; } = Pet;
                    public void Reset() => PrivatelySet = new Bird();
                }
                """
            )]);

    [Fact]
    public Task Readonly_field() =>
        Verify.Suppressions("""
            class C(Pet pet)
            {
                readonly Pet _pet = pet;

                int M()
                {
                    if (_pet is Bird) return 0;
                    Console.WriteLine("calls can't change a readonly field");
                    return this._pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Readonly_field_of_a_struct_this() =>
        Verify.Suppressions("""
            struct Holder(Pet pet)
            {
                readonly Pet _pet = pet;

                void Reset() => this = new Holder(new Bird("Polly"));

                int M()
                {
                    if (_pet is Bird) return 0;
                    Reset();
                    return _pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Readonly_members_followed_inside_the_constructor() =>
        Verify.Suppressions("""
            class C
            {
                readonly Pet _pet;
                Pet Pet { get; }
                static readonly Pet s_pet;

                static C()
                {
                    s_pet = new Cat("Tom");
                    Console.WriteLine(s_pet /*suppressed*/switch { Cat => 1 });
                }

                C(Pet pet)
                {
                    _pet = pet;
                    if (_pet is Bird) return;
                    Console.WriteLine(_pet /*suppressed*/switch { Cat => 1, Dog => 2 });
                }

                C(Pet pet, int n)
                {
                    if (pet is Bird) return;
                    Pet = pet;
                    Console.WriteLine(Pet /*suppressed*/switch { Cat => 1, Dog => 2 });
                }

                // Assigned again in between.
                C(Pet pet, double d)
                {
                    _pet = pet;
                    if (_pet is Bird) return;
                    /*because*/_pet = new Bird("Polly");
                    Console.WriteLine(_pet /*warns*/switch { Cat => 1, Dog => 2 });
                }

                // A lambda could run after another assignment.
                C(Pet pet, string s)
                {
                    _pet = pet;
                    if (_pet is Bird) return;
                    Func<int> f = () => _pet /*warns*/switch { Cat => 1, Dog => 2 };
                    Console.WriteLine(f());
                }

                static void Keep(ref Pet pet) { }

                // A reference to it could be written through.
                C(Pet pet, bool flag)
                {
                    _pet = pet;
                    Keep(ref _pet);
                    if (_pet is Bird) return;
                    Console.WriteLine(_pet /*warns*/switch { Cat => 1, Dog => 2 });
                }
            }
            """
        );

    [Fact]
    public Task Root_written_between() =>
        Verify.Suppressions("""
            record Owner(Pet Pet);

            class C
            {
                int M(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    /*because*/owner = new Owner(new Bird("Polly"));
                    return owner.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int N(Owner owner, Owner other)
                {
                    if (owner.Pet is Bird) return 0;
                    return other.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Static_readonly_field() =>
        Verify.Suppressions("""
            static class Defaults
            {
                public static readonly Pet Pet = new Cat("Tom");
            }

            class C
            {
                int M()
                {
                    if (Defaults.Pet is Bird) return 0;
                    return Defaults.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );
}
