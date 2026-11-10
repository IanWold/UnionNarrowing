using UnionNarrowing.Suppressors.Analyzers;

namespace UnionNarrowing.Tests;

/// <summary>
/// UN0003, UN0006, UN0010 and UN0018:
/// places a union can be left holding no case.
/// </summary>
public class DefaultUnionTests
{
    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> Diagnostics(string source, IEnumerable<string>? enabled = null) =>
        Verify.Diagnostics(new DefaultUnionAnalyzer(), source, enabled: enabled);

    [Fact]
    public async Task Arrays()
    {
        var diagnostics = await Diagnostics("""
            union MaybeCat(Cat?, Dog);

            class C
            {
                void M(int count)
                {
                    var some = /*!UN0006*/new Pet[count];
                    var three = /*!UN0006*/new Pet[3];
                    var grid = /*!UN0006*/new Pet[2, count];
                    var none = new Pet[0];
                    var filled = new Pet[] { new Cat("Tom"), new Dog("Rex") };
                    var sized = new Pet[2] { new Cat("Tom"), new Dog("Rex") };
                    var maybe = new MaybeCat[count];
                    Pet[] collected = [new Cat("Tom")];
                    Console.WriteLine($"{some}{three}{grid}{none}{filled}{sized}{maybe}{collected}");
                }
            }
            """
        );

        Assert.Contains("'new Pet[count]' fills the array with default(Pet), which holds none of its cases", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public Task Custom_union_with_its_own_parameterless_constructor() =>
        Diagnostics("""
            [System.Runtime.CompilerServices.Union]
            readonly struct CatOrDog : System.Runtime.CompilerServices.IUnion
            {
                public CatOrDog() => Value = new Cat("Tom");
                public CatOrDog(Cat cat) => Value = cat;
                public CatOrDog(Dog dog) => Value = dog;
                public object? Value { get; }
            }

            class C
            {
                CatOrDog M() => new CatOrDog();
            }
            """
        );

    [Fact]
    public async Task Default_values()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                void M(Pet optional = /*!UN0003*/default)
                {
                    Pet a = /*!UN0003*/default;
                    var b = /*!UN0003*/default(Pet);
                    var c = /*!UN0003*/new Pet();
                    Console.WriteLine($"{a}{b}{c}");
                }
            }
            """
        );

        Assert.Equal("default(Pet) holds none of Pet's cases, so a switch over it throws", diagnostics.Single(d => d.GetMessage().StartsWith("default(")).GetMessage());
    }

    [Fact]
    public Task Defaults_the_null_forgiving_operator_says_are_meant() =>
        Diagnostics("""
            struct Box { public Pet Pet; }

            class C
            {
                public Pet Field = default!;
                public Pet Property { get; set; } = (default)!;

                void M()
                {
                    Pet a = default!;
                    var b = default(Pet)!;
                    Box c = default!;
                    Pet d = /*!UN0003*/default;
                    Console.WriteLine($"{a}{b}{c}{d}");
                }
            }
            """
        );

    [Fact]
    public Task Defaults_that_are_only_tested_or_never_read() =>
        Diagnostics("""
            class C
            {
                string M(List<Pet> pets, bool flag, Pet a, Pet b)
                {
                    var first = pets.FirstOrDefault() is Cat c ? c.Name : "";
                    var any = pets.FirstOrDefault(p => p is Dog) is not null;
                    var n = pets.LastOrDefault() switch { Cat => 1, _ => 0 };
                    switch (pets.SingleOrDefault()) { case Cat: break; }

                    Pet chosen = default;
                    if (flag) chosen = a;
                    else chosen = b;

                    var filled = new Pet[3];
                    for (var i = 0; i < filled.Length; i++)
                        filled[i] = a;

                    var alsoFilled = new Pet[3];
                    Array.Fill(alsoFilled, b);

                    return $"{first}{any}{n}{chosen}{filled}{alsoFilled}";
                }

                int Throws(List<Pet> pets) => /*!UN0010*/pets.FirstOrDefault() switch { Cat => 1, Dog => 2, Bird => 3 };

                Pet Read(bool flag, Pet a)
                {
                    Pet maybe = /*!UN0003*/default;
                    if (flag) maybe = a;
                    return maybe;
                }
            }
            """
        );

    [Fact]
    public async Task Generic_helpers_that_return_the_default()
    {
        var diagnostics = await Diagnostics("""
            union MaybeCat(Cat?, Dog);

            static class Helpers
            {
                public static T Fallback<T>() => default!;

                public static T OrElse<T>(bool ok, T value)
                {
                    if (ok) return value;
                    return default!;
                }

                public static T Echo<T>(T value) => value;

                // The default is the lambda's, not this method's.
                public static T Make<T>(Func<T> make)
                {
                    Func<T> none = () => default!;
                    return make();
                }
            }

            class Box<T>
            {
                public T Get() => default(T)!;
            }

            class C
            {
                void M(Pet pet, bool flag)
                {
                    var a = /*!UN0010*/Helpers.Fallback<Pet>();
                    var b = /*!UN0010*/Helpers.OrElse(flag, pet);
                    var c = /*!UN0010*/new Box<Pet>().Get();
                    var d = Helpers.Echo(pet);
                    var e = Helpers.Make(() => pet);
                    var f = Helpers.Fallback<int>();
                    var g = Helpers.Fallback<MaybeCat>();
                    Console.WriteLine($"{a}{b}{c}{d}{e}{f}{g}");
                }
            }
            """
        );

        Assert.Contains("'Helpers.Fallback<Pet>()' gives default(Pet) on some paths, which holds none of its cases", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public Task Generic_helpers_that_store_the_default() =>
        Diagnostics("""
            static class Helpers
            {
                public static void Reset<T>(ref T value) => value = default!;
                public static void ResetIf<T>(ref T value, bool reset) => value = reset ? default! : value;
                public static void Clear<T>(T[] items) { for (var i = 0; i < items.Length; i++) items[i] = default!; }
                public static void Keep<T>(ref T value) { }
            }

            class C
            {
                void M(Pet pet, Pet[] pets)
                {
                    /*!UN0010*/Helpers.Reset(ref pet);
                    /*!UN0010*/Helpers.ResetIf(ref pet, true);
                    /*!UN0010*/Helpers.Clear(pets);
                    Helpers.Keep(ref pet);
                }
            }
            """
        );

    [Fact]
    public async Task Generic_methods_that_say_they_can_give_the_default()
    {
        var diagnostics = await Diagnostics("""
            using System.Collections.Immutable;
            using System.Diagnostics.CodeAnalysis;
            using System.Runtime.InteropServices;

            static class External
            {
                public static T? Annotated<T>() => default;
                [return: MaybeNull] public static T Attributed<T>() => default!;
                public static T Plain<T>() => throw new NotImplementedException();
            }

            class C
            {
                void M(List<Pet> list, Pet[] array, ImmutableArray<Pet> immutable, string json, System.Threading.AsyncLocal<Pet> local, Dictionary<string, Pet> byName)
                {
                    var a = /*!UN0010*/list.Find(p => p is Cat);
                    var b = /*!UN0010*/Array.FindLast(array, p => p is Cat);
                    var c = /*!UN0010*/immutable.FirstOrDefault();
                    var d = /*!UN0010*/System.Text.Json.JsonSerializer.Deserialize<Pet>(json);
                    var e = /*!UN0010*/local.Value;
                    var f = /*!UN0010*/External.Annotated<Pet>();
                    var g = /*!UN0010*/External.Attributed<Pet>();
                    ref var h = ref /*!UN0010*/CollectionsMarshal.GetValueRefOrAddDefault(byName, "Tom", out _);

                    // Throws for an empty sequence of a struct.
                    var i = list.MaxBy(p => 1);
                    var j = External.Plain<Pet>();
                    local.Value = new Cat("Tom");
                    Console.WriteLine($"{a}{b}{c}{d}{e}{f}{g}{h}{i}{j}");
                }
            }
            """
        );

        Assert.Contains("'list.Find(p => p is Cat)' gives default(Pet) on some paths, which holds none of its cases", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public Task Lazy_without_a_factory_and_arrays_filled_through_a_span() =>
        Diagnostics("""
            class C
            {
                void M(int n, Pet fill)
                {
                    var lazy = /*!UN0010*/new Lazy<Pet>();
                    var threadSafe = /*!UN0010*/new Lazy<Pet>(true);
                    var made = new Lazy<Pet>(() => fill);
                    var given = new Lazy<Pet>(fill);

                    var arr = new Pet[n];
                    arr.AsSpan().Fill(fill);
                    var arr2 = new Pet[n];
                    new Span<Pet>(arr2).Fill(fill);

                    Console.WriteLine($"{lazy}{threadSafe}{made}{given}{arr}{arr2}");
                }
            }
            """
        );

    [Fact]
    public async Task Library_methods_that_create_or_fill_with_defaults()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                void M(Pet[] pets, List<Pet> list, System.Buffers.ArrayPool<Pet> pool)
                {
                    var a = /*!UN0006*/GC.AllocateArray<Pet>(4);
                    var b = /*!UN0006*/GC.AllocateUninitializedArray<Pet>(4);
                    var c = /*!UN0006*/Array.CreateInstance(typeof(Pet), 4);
                    /*!UN0006*/Array.Resize(ref pets, 10);
                    /*!UN0006*/System.Runtime.InteropServices.CollectionsMarshal.SetCount(list, 10);
                    /*!UN0006*/Array.Clear(pets);
                    /*!UN0006*/pets.AsSpan().Clear();
                    var d = /*!UN0006*/pool.Rent(8);

                    var e = /*!UN0003*/Activator.CreateInstance<Pet>();
                    var f = /*!UN0003*/Activator.CreateInstance(typeof(Pet));
                    var g = /*!UN0003*/System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Pet));
                    var h = Activator.CreateInstance(typeof(Pet), new Cat("Tom")); // calls a constructor
                    Console.WriteLine($"{a}{b}{c}{d}{e}{f}{g}{h}");
                }
            }
            """
        );

        var messages = diagnostics.Select(d => d.GetMessage()).ToList();
        Assert.Contains("'Array.Resize(ref pets, 10)' fills any new elements with default(Pet), which holds none of its cases", messages);
        Assert.Contains("'pets.AsSpan().Clear()' resets the elements to default(Pet), which holds none of its cases", messages);
    }

    [Fact]
    public async Task Library_methods_that_fall_back_to_the_default()
    {
        var diagnostics = await Diagnostics("""
            union MaybeCat(Cat?, Dog);

            class C
            {
                void M(List<Pet> pets, Dictionary<string, Pet> byName, Pet? maybe, List<MaybeCat> maybeCats)
                {
                    var a = /*!UN0010*/pets.FirstOrDefault();
                    var b = /*!UN0010*/pets.LastOrDefault(p => p is Cat);
                    var c = /*!UN0010*/pets.SingleOrDefault();
                    var d = /*!UN0010*/pets.ElementAtOrDefault(3);
                    var e = /*!UN0010*/pets.DefaultIfEmpty();
                    var f = /*!UN0010*/byName.GetValueOrDefault("Tom");
                    var g = /*!UN0010*/maybe.GetValueOrDefault();

                    // With a fallback, or where a default is a legitimate null.
                    var h = pets.FirstOrDefault(new Cat("Tom"));
                    var i = byName.GetValueOrDefault("Tom", new Cat("Tom"));
                    var j = maybe.GetValueOrDefault(new Dog("Rex"));
                    var k = maybeCats.FirstOrDefault();
                    Console.WriteLine($"{a}{b}{c}{d}{e}{f}{g}{h}{i}{j}{k}");
                }
            }
            """
        );

        var messages = diagnostics.Select(d => d.GetMessage()).ToList();
        Assert.Contains("'pets.FirstOrDefault()' gives default(Pet) when the sequence is empty, which holds none of its cases", messages);
        Assert.Contains("'pets.LastOrDefault(p => p is Cat)' gives default(Pet) when nothing matches, which holds none of its cases", messages);
        Assert.Contains("'byName.GetValueOrDefault(\"Tom\")' gives default(Pet) when the key isn't there, which holds none of its cases", messages);
    }

    [Fact]
    public Task Other_structs() =>
        Diagnostics("""
            class C
            {
                (int, string) M() => default;
                DateTime N() => new DateTime();
            }
            """
        );

    [Fact]
    public Task Out_parameter_of_a_try_method() =>
        Diagnostics("""
            class C
            {
                static bool TryGet(int id, out Pet pet)
                {
                    if (id > 0)
                    {
                        pet = new Cat("Tom");
                        return true;
                    }

                    pet = default;
                    return false;
                }
            }
            """
        );

    [Fact]
    public async Task Out_value_of_a_try_method_that_failed()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                void M(Dictionary<string, Pet> byName, Queue<Pet> queue)
                {
                    byName.TryGetValue("Tom", /*!UN0010*/out var ignored);

                    if (!byName.TryGetValue("Rex", /*!UN0010*/out var missing))
                        Console.WriteLine(missing.Value);

                    if (queue.TryDequeue(/*!UN0010*/out var next)) Console.WriteLine("dequeued");
                    else Console.WriteLine(next.Value);

                    // Only read where the call succeeded.
                    if (byName.TryGetValue("Polly", out var found))
                        Console.WriteLine(found.Value);

                    Console.WriteLine(ignored.Value);
                }
            }
            """
        );

        Assert.Contains("'out var ignored' gives default(Pet) when 'TryGetValue' returns false, which holds none of its cases", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public Task Out_value_of_a_try_method_that_failed_replaced_before_it_is_read() =>
        Diagnostics("""
            class C
            {
                void M(Queue<Pet> inbox, Dictionary<string, Pet> byName)
                {
                    if (!inbox.TryDequeue(out var next)) next = new Cat("Tom");

                    if (!byName.TryGetValue("Rex", out var found))
                    {
                        found = new Dog("Rex");
                    }

                    Pet earlier;
                    if (byName.TryGetValue("Polly", out earlier)) Console.WriteLine("found");
                    else earlier = new Bird("Polly");

                    var chosen = byName.TryGetValue("Tom", out var tom) ? tom : new Cat("Tom");

                    // Read before it's replaced.
                    if (!byName.TryGetValue("Max", /*!UN0010*/out var read))
                    {
                        Console.WriteLine(read.Value);
                        read = new Dog("Max");
                    }

                    Console.WriteLine($"{next}{found}{earlier}{chosen}{read}");
                }
            }
            """
        );

    [Fact]
    public async Task Structs_and_tuples_holding_a_union()
    {
        var diagnostics = await Diagnostics("""
            struct Box { public Pet Pet; }
            struct Outer { public Box Inner; public int N; }
            record struct Entry(Pet Pet, int N);
            struct WithConstructor { public Pet Pet; public WithConstructor() => Pet = new Cat("Tom"); }

            class C
            {
                void M(int n, List<Entry> entries)
                {
                    var a = /*!UN0018*/default(Box);
                    Outer b = /*!UN0018*/default;
                    var c = /*!UN0018*/new Entry();
                    var d = /*!UN0018*/new Box[n];
                    var e = /*!UN0018*/default((Pet, int));
                    var f = /*!UN0018*/entries.FirstOrDefault();
                    var g = new WithConstructor();
                    var h = new Entry(new Cat("Tom"), 1);
                    Console.WriteLine($"{a}{b}{c}{d}{e}{f}{g}{h}");
                }
            }
            """
        );

        var messages = diagnostics.Select(d => d.GetMessage()).ToList();
        Assert.Contains("'default(Box)' leaves default(Pet) in Box.Pet, which holds none of its cases", messages);
        Assert.Contains("'default' leaves default(Pet) in Outer.Inner.Pet, which holds none of its cases", messages);
        Assert.Contains("'new Box[n]' leaves default(Pet) in each element's Box.Pet, which holds none of its cases", messages);
    }

    [Fact]
    public async Task Structs_holding_a_union_set_by_an_initializer()
    {
        var diagnostics = await Diagnostics("""
            struct Slot { public Pet State; public int Version; }
            struct TwoPets { public Pet A; public Pet B; }
            struct Outer { public Slot Inner; public int N; }
            record struct Envelope(Guid Id, Pet State);
            ref struct PetRef { public ref Pet Target; }

            class C
            {
                void M(Pet pet)
                {
                    var a = new Slot { State = pet, Version = 1 };
                    var b = new Envelope { Id = Guid.NewGuid(), State = pet };
                    var c = new TwoPets { A = pet, B = pet };
                    var d = new Outer { Inner = new Slot { State = pet } };
                    var e = new Outer { Inner = { State = pet } };
                    var f = default(Envelope) with { State = pet };

                    // A ref field's default refers to nothing, rather than holding a default union.
                    var g = new PetRef { Target = ref pet };
                    var h = new PetRef();

                    var i = /*!UN0018*/new Slot { Version = 1 };
                    var j = /*!UN0018*/new TwoPets { A = pet };
                    var k = /*!UN0018*/new Outer { N = 1 };
                    var l = /*!UN0018*/default(TwoPets) with { B = pet };
                    Console.WriteLine($"{a}{b}{c}{d}{e}{f}{i}{j}{k}{l}");
                }
            }
            """
        );

        Assert.Contains("'new TwoPets { A = pet }' leaves default(Pet) in TwoPets.B, which holds none of its cases", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public Task Union_that_admits_null() =>
        Diagnostics("""
            union MaybeCat(Cat?, Dog);

            class C
            {
                MaybeCat M() => default;
            }
            """
        );
}
