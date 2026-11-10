using UnionNarrowing.Suppressors.Analyzers;

namespace UnionNarrowing.Tests;

/// <summary>
/// UN0014, UN0015 and UN0019:
/// a boxed union, and a union formatted as text.
/// </summary>
public class BoxedUnionTests
{
    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> Diagnostics(string source, IEnumerable<string>? enabled = null) =>
        Verify.Diagnostics(new BoxedUnionAnalyzer(), source, enabled: enabled);

    [Fact]
    public async Task Equals_and_GetType()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                void M(Pet pet, Pet other, Cat cat)
                {
                    var a = /*!UN0014*/pet.Equals(cat);
                    var b = /*!UN0014*/pet.GetType() == typeof(Cat);
                    var c = /*!UN0014*/typeof(Dog) != pet.GetType();

                    var d = pet.Equals(other);
                    var e = pet.Equals(new Pet(cat));
                    var f = pet.GetType() == typeof(Pet);
                    var g = pet.Value?.GetType() == typeof(Cat);
                    Console.WriteLine($"{a}{b}{c}{d}{e}{f}{g}");
                }
            }
            """
        );

        Assert.Contains("'pet.Equals(cat)' is never true: a Pet only ever equals another Pet. Compare with a Pet made from it, or compare its Value.", diagnostics.Select(d => d.GetMessage()));
        Assert.Contains("'typeof(Dog) != pet.GetType()' is always true: GetType() on a Pet is always typeof(Pet). Test the union itself, which tests what it holds.", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public Task Equals_through_object_or_the_default_comparer() =>
        Diagnostics("""
            class C
            {
                void M(Pet pet, Pet other, Cat cat, object any, IEqualityComparer<object> comparer)
                {
                    var a = /*!UN0014*/Equals(pet, cat);
                    var b = /*!UN0014*/object.Equals(cat, pet);
                    var c = /*!UN0014*/EqualityComparer<object>.Default.Equals(pet, cat);

                    var d = Equals(pet, other);
                    var e = object.Equals(pet, any);
                    var f = EqualityComparer<object>.Default.Equals(pet, new Pet(cat));
                    var g = comparer.Equals(pet, cat);
                    var h = Equals(pet.Value, cat);
                    Console.WriteLine($"{a}{b}{c}{d}{e}{f}{g}{h}");
                }
            }
            """
        );

    [Fact]
    public async Task Record_that_prints_a_union_member()
    {
        var diagnostics = await Diagnostics("""
            record Owner(Pet /*!UN0019*/Pet, int Age);
            record class Keeper
            {
                public Pet? /*!UN0019*/Favourite { get; init; }
                private Pet Hidden { get; init; }
            }
            record Described(Pet Pet)
            {
                protected virtual bool PrintMembers(System.Text.StringBuilder builder)
                {
                    builder.Append($"Pet = {Pet.Value}");
                    return true;
                }
            }
            record struct Entry(Pet /*!UN0019*/Pet);
            record Shown(Pet Pet)
            {
                public override string ToString() => $"Shown {{ Pet = {Pet.Value} }}";
            }

            union ShownPet(Cat, Dog)
            {
                public override string ToString() => Value?.ToString() ?? "";
            }
            record HoldsShownPet(ShownPet Pet, ShownPet? Maybe);
            """
        );

        Assert.Contains("'Owner' prints its Pet as 'Pet', the union's type name, in ToString: declare PrintMembers to print its Value, or override ToString in Pet", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public async Task Switch_on_a_boxed_union_and_reference_equality()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                void M(Pet pet, Pet other)
                {
                    var a = ((object)pet) switch { /*!UN0014*/Cat => 1, /*!UN0014*/Dog or Bird => 2, Pet => 3, _ => 0 };
                    switch ((object)pet)
                    {
                        case /*!UN0014*/Cat: break;
                        case Pet: break;
                    }

                    var b = /*!UN0014*/ReferenceEquals(pet, other);
                    var c = ReferenceEquals(pet.Value, other.Value);
                    Console.WriteLine($"{a}{b}{c}");

                    switch ((object)pet)
                    {
                        case /*!UN0014*/string: break;
                    }
                }
            }
            """
        );

        Assert.Contains("'Cat' never matches: a boxed Pet is a Pet, not what it holds. Switch on the union itself, without boxing it.", diagnostics.Select(d => d.GetMessage()));
        Assert.Contains("'string' never matches: a boxed Pet is a Pet, not what it holds. Switch on the union itself, without boxing it.", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public async Task Type_tests_and_casts_on_a_boxed_union()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                void M(Pet pet)
                {
                    var a = /*!UN0014*/(object)pet is Cat;
                    var b = /*!UN0014*/(object)pet is Cat or Dog;
                    var c = /*!UN0014*/(object)pet is Cat { Name: "Tom" } kitty;
                    var d = /*!UN0014*/(Cat)(object)pet;
                    var e = /*!UN0014*/(object)pet as Cat;

                    // What a boxed union can be.
                    var f = (object)pet is Pet;
                    var g = (object)pet is System.Runtime.CompilerServices.IUnion;
                    var h = (object)pet is ValueType;
                    var i = (object)pet is Cat or null;
                    var j = pet is Cat;
                    var k = pet.Value is Cat;
                    Console.WriteLine($"{a}{b}{c}{d}{e}{f}{g}{h}{i}{j}{k}");
                }
            }
            """
        );

        Assert.Contains("'(object)pet is Cat' is never true: a boxed Pet is a Pet, not what it holds. Test the union itself, without boxing it.", diagnostics.Select(d => d.GetMessage()));
        Assert.Contains("'(Cat)(object)pet' always throws: a boxed Pet is a Pet, not what it holds. Use a pattern on the union, or cast its Value.", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public async Task Union_formatted_as_text()
    {
        var diagnostics = await Diagnostics("""
            union Described(Cat, Dog)
            {
                public override string ToString() => Value?.ToString() ?? "";
            }

            class C
            {
                void M(Pet pet, Described described, System.Text.StringBuilder builder, System.IO.TextWriter writer)
                {
                    var a = $"pet: {/*!UN0015*/pet}";
                    var b = "pet: " + /*!UN0015*/pet;
                    var c = /*!UN0015*/pet.ToString();
                    Console.WriteLine(/*!UN0015*/pet);
                    builder.Append(/*!UN0015*/pet);
                    writer.WriteLine(/*!UN0015*/pet);
                    var d = string.Format("{0} and {1}", /*!UN0015*/pet, 1);

                    // What it holds, or a union with its own ToString.
                    var e = $"pet: {pet.Value}";
                    var f = $"{described}";
                    Console.WriteLine(described);
                    Console.WriteLine($"{a}{b}{c}{d}{e}{f}");
                }
            }
            """
        );

        Assert.Contains("'pet' shows the union's type name, 'Pet', not what it holds: format 'pet.Value' instead, or override ToString in Pet", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public Task Union_formatted_through_an_interpolated_string_handler() =>
        Diagnostics("""
            using System.Diagnostics;
            using System.Runtime.CompilerServices;
            using System.Text;

            union Described(Cat, Dog)
            {
                public override string ToString() => Value?.ToString() ?? "";
            }

            [InterpolatedStringHandler]
            public ref struct AnyHandler
            {
                public AnyHandler(int literalLength, int formattedCount) { }
                public void AppendLiteral(string literal) { }
                public void AppendFormatted<T>(T value) { }
            }

            // Formats a Pet its own way.
            [InterpolatedStringHandler]
            public ref struct PetHandler
            {
                public PetHandler(int literalLength, int formattedCount) { }
                public void AppendLiteral(string literal) { }
                public void AppendFormatted(Pet pet) { }
            }

            class C
            {
                void M(Pet pet, Described described, StringBuilder builder, IFormatProvider provider)
                {
                    builder.Append($"{/*!UN0015*/pet}");
                    builder.AppendLine($"now {/*!UN0015*/pet,-10}");
                    Debug.Assert(pet is not Bird, $"a bird: {/*!UN0015*/pet}");
                    var a = string.Create(provider, $"pet {/*!UN0015*/pet}");
                    Log($"pet {/*!UN0015*/pet}");

                    builder.Append($"{pet.Value} {described}");
                    LogPet($"pet {pet}");
                    Console.WriteLine(a);
                }

                static void Log(AnyHandler handler) { }
                static void LogPet(PetHandler handler) { }
            }
            """
        );

    [Fact]
    public async Task Nullable_union_formatted_as_text()
    {
        var diagnostics = await Diagnostics("""
            union Described(Cat, Dog)
            {
                public override string ToString() => Value?.ToString() ?? "";
            }

            class C
            {
                void M(Pet? maybe, List<Pet?> maybes, Described? described)
                {
                    var a = $"{/*!UN0015*/maybe}";
                    var b = "maybe: " + /*!UN0015*/maybe;
                    var c = /*!UN0015*/maybe.ToString();
                    Console.WriteLine(/*!UN0015*/maybe);
                    var d = string.Join(", ", /*!UN0015*/maybes);

                    var e = $"{described} {maybe?.Value}";
                    Console.WriteLine($"{a}{b}{c}{d}{e}");
                }
            }
            """
        );

        var messages = diagnostics.Select(d => d.GetMessage()).ToList();
        Assert.Contains("'maybe' shows the union's type name, 'Pet', not what it holds: format 'maybe?.Value' instead, or override ToString in Pet", messages);
        Assert.Contains("'maybes' shows the union's type name, 'Pet', not what it holds: format 'maybes.Select(x => x?.Value)' instead, or override ToString in Pet", messages);
    }

    [Fact]
    public async Task Unions_joined_or_logged()
    {
        var diagnostics = await Diagnostics("""
            using System.Diagnostics;
            using Microsoft.Extensions.Logging;

            class C
            {
                void M(ILogger logger, Pet pet, List<Pet> pets, System.Text.StringBuilder builder)
                {
                    logger.LogInformation("pet {Pet}", /*!UN0015*/pet);
                    logger.LogWarning("pets {A} and {B}", /*!UN0015*/pet, 1);
                    using var scope = logger.BeginScope("pet {Pet}", /*!UN0015*/pet);
                    var joined = string.Join(", ", /*!UN0015*/pets);
                    builder.AppendJoin(", ", /*!UN0015*/pets);
                    var converted = Convert.ToString(/*!UN0015*/pet);
                    Trace.TraceInformation("pet {0}", /*!UN0015*/pet);

                    logger.LogInformation("pet {Pet}", pet.Value);
                    var values = string.Join(", ", pets.Select(p => p.Value));
                    Console.WriteLine(joined + values + converted);
                }
            }
            """
        );

        Assert.Contains("'pets' shows the union's type name, 'Pet', not what it holds: format 'pets.Select(x => x.Value)' instead, or override ToString in Pet", diagnostics.Select(d => d.GetMessage()));
    }
}
