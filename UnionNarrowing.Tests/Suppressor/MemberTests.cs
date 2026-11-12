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
    public Task Lambdas_made_while_the_object_is_constructed() =>
        Verify.Suppressions("""
            class Early
            {
                private readonly Pet _pet;
                public Func<int> Legs = () => 0;
                public Func<int> Later = () => 0;

                public Early(Pet pet)
                {
                    _pet = new Cat("Tom");
                    Init();
                    _pet = pet;
                }

                // Runs before the constructor sets _pet for good.
                void Init()
                {
                    if (_pet is Bird) return;
                    Legs = () => _pet /*warns*/switch { Cat => 4, Dog => 4 };
                }

                public void Reset()
                {
                    if (_pet is Bird) return;
                    Later = () => _pet /*suppressed*/switch { Cat => 4, Dog => 4 };
                }
            }

            abstract class Base
            {
                protected Base() => Setup();
                protected abstract void Setup();
            }

            class Derived : Base
            {
                private readonly Pet _pet;
                public Func<int> Legs = () => 0;

                public Derived(Pet pet) => _pet = pet;

                // The base constructor calls this before _pet is set.
                protected override void Setup()
                {
                    if (_pet is Bird) return;
                    Legs = () => _pet /*warns*/switch { Cat => 4, Dog => 4 };
                }
            }
            """
        );


    [Fact]
    public Task Getters_that_return_another_member() =>
        Verify.Suppressions("""
            class Filter
            {
                private readonly Pet _input;

                public Filter(Pet input)
                {
                    if (input is Bird) throw new ArgumentException();
                    _input = input;
                }

                public Pet Input => _input;
                public Pet Child { get { return Input; } }

                public int Legs() => Input /*suppressed*/switch { Cat => 4, Dog => 4 };
            }

            class C
            {
                int Guarded(Filter f)
                {
                    if (f.Input is Dog) return 0;
                    return f.Child /*suppressed*/switch { Cat => 1, Bird => 2 };
                }

                int Unguarded(Filter f) => f.Child /*warns*/switch { Cat => 1, Bird => 2 };
            }
            """
        );

    [Fact]
    public Task Code_construction_starts_that_waits() =>
        Verify.Suppressions("""
            using System.Collections.Generic;
            using System.Runtime.InteropServices;
            using System.Threading.Tasks;

            class Connection
            {
                public static TaskCompletionSource Gate = new();
                private readonly Pet _pet;
                private readonly Task<int> _pump;
                private readonly IEnumerator<int> _steps;

                // Both start running before _pet is set, and carry on after.
                public Connection(Pet pet)
                {
                    _pump = PumpAsync();
                    _steps = Steps();
                    _steps.MoveNext();
                    _pet = pet;
                }

                async Task<int> PumpAsync()
                {
                    if (_pet is Bird) return 0;
                    /*because*/await Gate.Task;
                    return _pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                IEnumerator<int> Steps()
                {
                    if (_pet is Bird) yield break;
                    /*because*/yield return 0;
                    yield return _pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // Construction doesn't start this one.
                public async Task<int> LaterAsync()
                {
                    if (_pet is Bird) return 0;
                    await Gate.Task;
                    return _pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }

            // Writing one field of a class that lays its fields out explicitly can change another.
            [StructLayout(LayoutKind.Explicit)]
            class Overlap
            {
                [FieldOffset(0)] public readonly Pet Fixed;
                [FieldOffset(0)] public Pet Loose;

                public Overlap(Pet pet) => Fixed = pet;

                public int M()
                {
                    if (Fixed is Bird) return 0;
                    Loose = new Bird("Polly");
                    return Fixed /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    /// <summary>
    /// Objects whose construction hands them to other code before their readonly members are set:
    /// code anywhere (another thread, or code that waits meanwhile) can read a member before and after it's set.
    /// </summary>
    [Fact]
    public Task Readonly_members_of_objects_handed_out_while_constructed() =>
        Verify.Suppressions("""
            using System.Collections.Generic;
            using System.Threading.Tasks;

            class Registry
            {
                public readonly TaskCompletionSource<Node> Added = new();
                public void Add(Node node) => Added.TrySetResult(node);
            }

            class Node
            {
                public readonly Pet Pet;
                private readonly Pet _pet;
                public Node(Registry registry, Pet pet) { registry.Add(this); Pet = pet; _pet = pet; }
                public Pet Held => _pet;
            }

            class Settled
            {
                public readonly Pet Pet;
                public Settled(Pet pet) => Pet = pet;
            }

            class Poller
            {
                private readonly Pet _pet;
                public readonly Task<int> Loop;

                public Poller(Pet pet)
                {
                    Loop = Task.Run(Tick);
                    _pet = pet;
                }

                int Tick()
                {
                    if (_pet is Bird) return 0;
                    Wait();
                    return _pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                static void Wait() { }
            }

            abstract class Service
            {
                protected Service() => Running = Task.Run(Execute);
                public Task<int> Running { get; }
                protected abstract int Execute();
            }

            sealed class Worker : Service
            {
                private readonly Pet _pet;
                public Worker(Pet pet) => _pet = pet;

                protected override int Execute()
                {
                    if (_pet is Bird) return 0;
                    return _pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }

            class Announced
            {
                public static event Func<Announced, Task>? Created;
                public required Pet Pet { get; init; }
                public Announced() => Created?.Invoke(this);
            }

            static class Defaults
            {
                public static readonly Pet Pet;
                static Defaults() { Loader.Start(); Pet = new Cat("Tom"); }
            }

            static class Loader
            {
                public static TaskCompletionSource Gate = new();
                public static Task<int>? Running;
                public static void Start() => Running = Read();

                static async Task<int> Read()
                {
                    if (Defaults.Pet is Bird) return 0;
                    /*because*/await Gate.Task;
                    return Defaults.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }

            class C
            {
                async Task<int> Watch(Registry registry, Task gate)
                {
                    var node = await registry.Added.Task;
                    if (node.Pet is Bird) return 0;
                    await gate;
                    return node.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                async Task<int> WatchGetter(Registry registry, Task gate)
                {
                    var node = await registry.Added.Task;
                    if (node.Held is Bird) return 0;
                    await gate;
                    return node.Held /*warns*/switch { Cat => 1, Dog => 2 };
                }

                async Task<int> OnCreated(Announced announced, Task gate)
                {
                    if (announced.Pet is Bird) return 0;
                    await gate;
                    return announced.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Plain(Settled settled)
                {
                    if (settled.Pet is Bird) return 0;
                    return settled.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Lambdas_made_while_the_object_is_constructed_in_other_ways() =>
        Verify.Suppressions("""
            static class Other
            {
                public static void Go() => Statics.Make();
            }

            class Statics
            {
                private static readonly Pet s_pet;
                public static Func<int> Legs = () => 0;

                static Statics()
                {
                    s_pet = new Cat("Tom");
                    Other.Go();
                    s_pet = new Bird("Polly");
                }

                public static void Make()
                {
                    if (s_pet is Bird) return;
                    Legs = () => s_pet /*warns*/switch { Cat => 4, Dog => 4 };
                }
            }

            class Initialized
            {
                private readonly Pet _pet;
                public Func<int> Legs = () => 0;

                public Pet Pet { init { MakeLambda(); _pet = value; } }

                public Initialized() => _pet = new Cat("Tom");

                void MakeLambda()
                {
                    if (_pet is Bird) return;
                    Legs = () => _pet /*warns*/switch { Cat => 4, Dog => 4 };
                }
            }

            class Owner
            {
                public readonly Pet Pet;
                public Func<int> Legs = () => 0;

                public Owner(Pet pet)
                {
                    Pet = new Cat("Tom");
                    Make(this);
                    Pet = pet;
                }

                static void Make(Owner owner)
                {
                    if (owner.Pet is Bird) return;
                    owner.Legs = () => owner.Pet /*warns*/switch { Cat => 4, Dog => 4 };
                }
            }
            """
        );

    [Fact]
    public Task Partial_properties() =>
        Verify.Suppressions("""
            public partial class Owner
            {
                public partial Pet Pet { get; }
            }

            public partial class Owner
            {
                private Pet _pet = new Cat("Tom");
                public partial Pet Pet { get => _pet; }
                public void Change() => _pet = new Bird("Polly");
            }

            class C
            {
                int M(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    owner.Change();
                    return owner.Pet /*warns*/switch { Cat => 1, Dog => 2 };
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

    [Fact]
    public Task Async_local_function_a_constructor_starts() =>
        Verify.Suppressions("""
            using System.Threading.Tasks;

            class Connection
            {
                public static TaskCompletionSource Gate = new();
                private readonly Pet _pet;
                public readonly Task<int> Pump;

                public Connection(Pet pet)
                {
                    Pump = Local();
                    _pet = pet;

                    async Task<int> Local()
                    {
                        if (_pet is Bird) return 0;
                        /*because*/await Gate.Task;
                        return _pet /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                }
            }
            """
        );
}
