namespace UnionNarrowing.Tests;

/// <summary>
/// Anything that could change the variable between the guard and the switch.
/// </summary>
public class MutationTests
{
    [Fact]
    public Task Assignment_before_the_guard_is_fine() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    pet = new Cat("Tom");
                    if (pet is Bird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Assignment_between() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    /*because*/pet = new Bird("Polly");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Assignment_earlier_in_the_same_statement() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return (pet = new Bird("Polly")).Value is null ? 0 : pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Assignment_from_the_switch_itself() =>
        Verify.Suppressions("""
            class C
            {
                Pet _last = new Cat("Tom");

                // State-machine style: the write is the assignment of the switch's result.
                Pet M(Pet pet)
                {
                    if (pet is Bird) return pet;
                    pet = pet /*suppressed*/switch { Cat => new Dog("Rex"), Dog => new Bird("Polly") };
                    return pet;
                }

                Pet N(Pet pet, bool flag)
                {
                    if (pet is not Bird && flag) pet = pet /*suppressed*/switch { Cat => new Dog("Rex"), Dog => new Bird("Polly") };
                    return pet;
                }

                void O(Pet pet)
                {
                    if (pet is Bird) return;
                    this._last = pet /*suppressed*/switch { Cat => new Dog("Rex"), Dog => new Bird("Polly") };
                }
            }
            """
        );

    [Fact]
    public Task Assignment_in_a_nested_statement_between() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, bool flag)
                {
                    if (pet is Bird) return 0;
                    if (flag)
                    {
                        /*because*/pet = new Bird("Polly");
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Assignment_whose_right_hand_side_writes_before_the_switch() =>
        Verify.Suppressions("""
            class C
            {
                static Pet[] _pets = [new Cat("Tom")];
                static int Index(out Pet pet)
                {
                    pet = new Bird("Polly");
                    return 0;
                }

                Pet M(Pet pet)
                {
                    if (pet is Bird) return pet;
                    pet = (pet = new Bird("Polly")) is var _ ? pet /*warns*/switch { Cat => new Dog("Rex"), Dog => new Bird("Polly") } : pet;
                    return pet;
                }

                // Working out where to store (the index) runs first, and here it writes.
                void N(Pet pet)
                {
                    if (pet is Bird) return;
                    _pets[Index(out pet)] = pet /*warns*/switch { Cat => new Dog("Rex"), Dog => new Bird("Polly") };
                }
            }
            """
        );

    [Fact]
    public Task Deconstruction_between() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    /*because*/(pet, _) = (new Bird("Polly"), 1);
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Local_variable() =>
        Verify.Suppressions("""
            class C
            {
                Pet Get() => new Cat("Tom");

                int M()
                {
                    var pet = Get();
                    if (pet is Bird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Makeref_before_the_guard() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    var reference = __makeref(pet);
                    if (pet is Bird) return 0;
                    __refvalue(reference, Pet) = new Bird("Polly");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Other_assignments_from_the_switch() =>
        Verify.Suppressions("""
            class C
            {
                readonly Pet[] _pets = [new Cat("Tom")];

                // A deconstruction evaluates the whole right side before it stores anything.
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    (pet, var count) = (pet /*suppressed*/switch { Cat => new Dog("Rex"), Dog => new Bird("Polly") }, 1);
                    return count;
                }

                // The index is worked out first, but doesn't write pet.
                void N(Pet pet, int i)
                {
                    if (pet is Bird) return;
                    _pets[i + 1] = pet /*suppressed*/switch { Cat => new Dog("Rex"), Dog => new Bird("Polly") };
                }
            }
            """
        );

    [Fact]
    public Task Out_argument_before_the_guard_is_fine() =>
        Verify.Suppressions("""
            class C
            {
                static bool TryGet(out Pet pet)
                {
                    pet = new Cat("Tom");
                    return true;
                }

                int M()
                {
                    if (!TryGet(out var pet)) return -1;
                    if (pet is Bird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Pointer_to_the_variable_taken_before_the_guard() =>
        Verify.Suppressions("""
            unsafe class C
            {
                int M(Pet pet)
                {
                    #pragma warning disable CS8500 // address of a managed type
                    Pet* pointer = &pet;
                    if (pet is Bird) return 0;
                    *pointer = new Bird("Polly");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """, allowUnsafe: true
        );

    [Fact]
    public Task Primary_constructor_parameters() =>
        Verify.Suppressions("""
            // Nothing assigns it.
            class C(Pet pet)
            {
                int M()
                {
                    if (pet is Bird) return 0;
                    Console.WriteLine("calls can't change it");
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                Func<int> N()
                {
                    if (pet is Bird) return () => 0;
                    return () => pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }

            // Something assigns it, so it isn't followed anywhere, even where nothing comes between the test and the switch.
            class D(Pet pet)
            {
                void Reset() => pet = new Bird("Polly");

                int M()
                {
                    if (pet is Bird) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }

            // In a struct, `this = ...` (as in Reset) replaces the parameter along with everything else, so a struct's
            // primary constructor parameters aren't followed anywhere either.
            struct S(Pet pet)
            {
                void Reset() => this = new S(new Bird("Polly"));

                int M()
                {
                    if (pet is Bird) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Reading_the_variable_in_between_is_fine() =>
        Verify.Suppressions("""
            class C
            {
                static void Show(Pet pet) => Console.WriteLine(pet.Value);
                static void ShowIn(in Pet pet) => Console.WriteLine(pet.Value);

                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    Show(pet);
                    ShowIn(in pet);
                    Console.WriteLine(pet.ToString());
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Ref_and_out_arguments_between() =>
        Verify.Suppressions("""
            class C
            {
                static void Reset(ref Pet pet) => pet = new Bird("Polly");
                static void Make(out Pet pet) => pet = new Bird("Polly");

                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    /*because*/Reset(ref pet);
                    var a = pet /*warns*/switch { Cat => 1, Dog => 2 };
                    return a;
                }

                int N(Pet pet)
                {
                    if (pet is Bird) return 0;
                    /*because*/Make(out pet);
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Ref_argument_that_can_escape_before_the_guard() =>
        Verify.Suppressions("""
            ref struct Holder(ref Pet pet)
            {
                private ref Pet _pet = ref pet;
                public void Reset() => _pet = new Bird("Polly");
            }

            class C
            {
                int M(Pet pet)
                {
                    var holder = new Holder(ref pet);
                    if (pet is Bird) return 0;
                    holder.Reset();
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Ref_argument_to_a_scoped_parameter_before_the_guard_is_fine() =>
        Verify.Suppressions("""
            static class PetExtensions
            {
                public static void Tidy(this scoped ref Pet pet) { }
            }

            class C
            {
                static void Normalize(scoped ref Pet pet) { }

                int M(Pet pet)
                {
                    // A scoped ref can't outlive the call, so nothing can write through it later.
                    Normalize(ref pet);
                    pet.Tidy();
                    if (pet is Bird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Ref_argument_to_a_scoped_parameter_between_is_still_a_write() =>
        Verify.Suppressions("""
            class C
            {
                static void Reset(scoped ref Pet pet) => pet = new Bird("Polly");

                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    /*because*/Reset(ref pet);
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Ref_extension_block_member_between() =>
        Verify.Suppressions("""
            static class PetExtensions
            {
                extension(ref Pet pet)
                {
                    public void Reset() => pet = new Bird("Polly");
                }
            }

            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    /*because*/pet.Reset();
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );


    [Fact]
    public Task Scoped_ref_extension_method_between() =>
        Verify.Suppressions("""
            static class PetExtensions
            {
                public static void MakeBird(this scoped ref Pet pet) => pet = new Bird("Polly");
            }

            class C
            {
                int Direct(Pet pet)
                {
                    if (pet is Bird) return 0;
                    /*because*/pet.MakeBird();
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Captured(Pet pet)
                {
                    if (pet is Bird) return 0;
                    Func<int> legs = () => pet /*warns*/switch { Cat => 1, Dog => 2 };
                    /*because*/pet.MakeBird();
                    return legs();
                }

                static bool TryGet(out Pet pet)
                {
                    pet = new Cat("Tom");
                    pet.MakeBird();
                    return true;
                }

                int OutArgument()
                {
                    TryGet(out var pet);
                    return pet /*warns*/switch { Cat => 1 };
                }
            }

            class Owner
            {
                private readonly Pet _pet;

                public Owner(Pet pet)
                {
                    if (pet is Bird) throw new ArgumentException();
                    _pet = pet;
                    _pet.MakeBird();
                }

                public int Legs() => _pet /*warns*/switch { Cat => 4, Dog => 4 };
            }
            """
        );

    [Fact]
    public Task Members_of_a_struct_local_that_write_it() =>
        Verify.Suppressions("""
            struct Slot
            {
                public Pet State;
                public int Version;
                public int Prop { get => 0; set => State = new Bird("Polly"); }
                public int Getter { get { State = new Bird("Polly"); return 5; } }
                public void Deconstruct(out int a, out int b) { State = new Bird("Polly"); a = 1; b = 1; }
                public void MakeBird() => State = new Bird("Polly");
            }

            struct Outer
            {
                public Slot Inner;
                public void BirdInner() => Inner.MakeBird();
            }

            class C
            {
                int Increment(Slot s)
                {
                    if (s.State is Bird) return 0;
                    /*because*/s.Prop++;
                    return s.State /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Decrement(Slot s)
                {
                    if (s.State is Bird) return 0;
                    /*because*/--s.Prop;
                    return s.State /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int DeconstructionTarget(Slot s)
                {
                    if (s.State is Bird) return 0;
                    /*because*/(s.Prop, var other) = (1, 2);
                    return s.State /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int PositionalPattern(Slot s)
                {
                    if (s.State is Bird) return 0;
                    /*because*/var matched = s is (1, 1);
                    return s.State /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int PropertyPattern(Slot s)
                {
                    if (s.State is Bird) return 0;
                    /*because*/var matched = s is { Getter: 5 };
                    return s.State /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int FieldPattern(Slot s)
                {
                    if (s.State is Bird) return 0;
                    var matched = s is { Version: 5 };
                    return s.State /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int MethodThatWritesAField(Outer o)
                {
                    if (o.Inner.State is Bird) return 0;
                    /*because*/o.BirdInner();
                    return o.Inner.State /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Reference_handed_back_by_a_call() =>
        Verify.Suppressions("""
            using System.Runtime.CompilerServices;

            class C
            {
                int AssignedThroughIt(Pet pet)
                {
                    if (pet is Bird) return 0;
                    /*because*/Unsafe.AsRef(in pet) = new Bird("Polly");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int AliasTakenBeforeTheGuard(Pet pet)
                {
                    ref var alias = ref Unsafe.AsRef(in pet);
                    if (pet is Bird) return 0;
                    alias = new Bird("Polly");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int ReinterpretedBeforeTheGuard(Pet pet)
                {
                    ref var alias = ref Unsafe.As<Pet, Pet>(ref pet);
                    if (pet is Bird) return 0;
                    alias = new Bird("Polly");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int SpanBeforeTheGuard(Pet pet)
                {
                    var span = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref pet, 1);
                    if (pet is Bird) return 0;
                    span[0] = new Bird("Polly");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Ref_extension_block_property_before_the_guard() =>
        Verify.Suppressions("""
            static class PetExtensions
            {
                extension(ref Pet pet)
                {
                    public Span<Pet> AsSpan => new Span<Pet>(ref pet);
                }
            }

            class C
            {
                int M(Pet pet)
                {
                    var span = pet.AsSpan;
                    if (pet is Bird) return 0;
                    span[0] = new Bird("Polly");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Ref_local_alias_created_before_the_guard() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    ref Pet alias = ref pet;
                    if (pet is Bird) return 0;
                    alias = new Bird("Polly");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Ref_local_alias_created_between() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    /*because*/ref Pet alias = ref pet;
                    /*because*/alias = new Bird("Polly");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Ref_this_extension_method_between() =>
        Verify.Suppressions("""
            static class PetExtensions
            {
                public static void Reset(this ref Pet pet) => pet = new Bird("Polly");
            }

            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    /*because*/pet.Reset();
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Reference_into_a_struct() =>
        Verify.Suppressions("""
            struct Box { public Pet Pet; }

            class C
            {
                int M(Box box)
                {
                    ref var alias = ref box.Pet;
                    if (box.Pet is Bird) return 0;
                    alias = new Bird("Polly");
                    return box.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Span_over_the_variable_created_before_the_guard() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    var span = new Span<Pet>(ref pet);
                    if (pet is Bird) return 0;
                    span[0] = new Bird("Polly");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Struct_written_through_a_member_between() =>
        Verify.Suppressions("""
            struct Box
            {
                public Pet Pet;
                public void Reset() => Pet = new Bird("Polly");
                public readonly int Count() => 1;
            }
            struct Outer { public Box Inner; }
            record struct Entry(Pet Pet, int N);

            class C
            {
                static void Set(out Pet pet) => pet = new Bird("Polly");

                int Field(Pet pet)
                {
                    var t = (pet, 1);
                    if (t.Item1 is Bird) return 0;
                    /*because*/t.Item1 = new Bird("Polly");
                    return t.Item1 /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Whole(Box box)
                {
                    if (box.Pet is Bird) return 0;
                    /*because*/box = new Box { Pet = new Bird("Polly") };
                    return box.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Out(Box box)
                {
                    if (box.Pet is Bird) return 0;
                    /*because*/Set(out box.Pet);
                    return box.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Nested(Outer outer)
                {
                    if (outer.Inner.Pet is Bird) return 0;
                    /*because*/outer.Inner.Pet = new Bird("Polly");
                    return outer.Inner.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int MutatingCall(Box box)
                {
                    if (box.Pet is Bird) return 0;
                    /*because*/box.Reset();
                    return box.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int NestedMutatingCall(Outer outer)
                {
                    if (outer.Inner.Pet is Bird) return 0;
                    /*because*/outer.Inner.Reset();
                    return outer.Inner.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int ReadonlyCall(Box box)
                {
                    if (box.Pet is Bird) return 0;
                    Console.WriteLine(box.Count());
                    return box.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Setter(Entry entry)
                {
                    if (entry.Pet is Bird) return 0;
                    /*because*/entry.Pet = new Bird("Polly");
                    return entry.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // Writing any part of the struct counts, even another field.
                int OtherField(Box box, Pet pet)
                {
                    var t = (pet, 1);
                    if (t.Item1 is Bird) return 0;
                    /*because*/t.Item2 = 2;
                    return t.Item1 /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Union_member_that_calls_one_that_reassigns_this() =>
        Verify.Suppressions("""
            union Animal(Cat, Dog, Bird)
            {
                public bool Reset() { this = new Bird("Polly"); return true; }
                public bool Check() => Reset() && this is Cat;
                public bool IsBird => this is Bird;
            }

            class C
            {
                int M(Animal animal)
                {
                    if (animal.IsBird) return 0;
                    /*because*/Console.WriteLine(animal.Check());
                    return animal /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Union_member_that_is_readonly() =>
        Verify.Suppressions("""
            union Describable(Cat, Dog, Bird)
            {
                public readonly string Describe() => Value?.ToString() ?? "";
            }

            class C
            {
                int M(Describable pet)
                {
                    if (pet is Bird) return 0;
                    Console.WriteLine(pet.Describe());
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Union_member_that_reassigns_this() =>
        Verify.Suppressions("""
            union Resettable(Cat, Dog, Bird)
            {
                public void MakeBird() => this = new Resettable(new Bird("Polly"));
            }

            class C
            {
                int M(Resettable pet)
                {
                    if (pet is Bird) return 0;
                    /*because*/pet.MakeBird();
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    /// <summary>
    /// A union member that isn't marked readonly only counts as not changing the union if its body only reads <c>this</c>:
    /// passing it by reference, or reading a property whose getter isn't readonly, could change it.
    /// </summary>
    [Fact]
    public Task Union_members_that_could_change_this_another_way() =>
        Verify.Suppressions("""
            union Animal(Cat, Dog, Bird)
            {
                static bool Touch(ref Animal animal) { animal = new Animal(new Bird("Polly")); return true; }
                public bool PassesThis => Touch(ref this) && this is Cat;

                public bool Mutating { get { this = new Animal(new Bird("Polly")); return true; } }
                public bool ReadsAMutatingGetter => Mutating && this is Cat;
            }

            class C
            {
                int ByReference(Animal animal)
                {
                    if (animal is Bird) return 0;
                    /*because*/Console.WriteLine(animal.PassesThis);
                    return animal /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int ThroughAGetter(Animal animal)
                {
                    if (animal is Bird) return 0;
                    /*because*/Console.WriteLine(animal.ReadsAMutatingGetter);
                    return animal /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Unscoped_ref_member_hands_out_a_reference_to_the_union() =>
        Verify.Suppressions("""
            union Selfish(Cat, Dog, Bird)
            {
                [System.Diagnostics.CodeAnalysis.UnscopedRef]
                public ref Selfish Self => ref this;
            }

            class C
            {
                int M(Selfish pet)
                {
                    ref var alias = ref pet.Self;
                    if (pet is Bird) return 0;
                    alias = new Selfish(new Bird("Polly"));
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );
}
