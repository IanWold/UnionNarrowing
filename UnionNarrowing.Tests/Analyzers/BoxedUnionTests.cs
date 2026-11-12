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
    public async Task Equals_the_other_way_round_and_types_as_keys()
    {
        var diagnostics = await Diagnostics("""
            class C
            {
                void M(Pet pet, Pet other, Cat cat, object any, Dictionary<Type, int> handlers)
                {
                    // Reported on the union, since the Look at what it holds fix rewrites a call as though the union came first.
                    var a = cat.Equals(/*!UN0014*/pet);
                    var b = handlers.TryGetValue(/*!UN0014*/pet.GetType(), out var handler);
                    var c = handlers[/*!UN0014*/pet.GetType()];

                    var d = other.Equals(pet);
                    var e = any.Equals(pet);
                    var f = handlers.TryGetValue(pet.Value!.GetType(), out var found);
                    var g = handlers.ContainsKey(typeof(Pet));
                    Console.WriteLine($"{a}{b}{c}{d}{e}{f}{g}{handler}{found}");
                }
            }
            """
        );

        var messages = diagnostics.Select(d => d.GetMessage().Replace("/*!UN0014*/", "")).ToList();
        Assert.Contains("'cat.Equals(pet)' is never true: a Pet only ever equals another Pet. Compare with a Pet made from it, or compare its Value.", messages);
        Assert.Contains("'pet.GetType()' is always typeof(Pet), so it never finds what's keyed by the type of what a Pet holds. Look up the type of its Value instead.", messages);
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
    public async Task Records_that_print_a_union_from_another_assembly()
    {
        var diagnostics = await Verify.Diagnostics(new BoxedUnionAnalyzer(), """
            record Owner(Shape /*!UN0019*/Shape, Shape? Other);
            record Keeper(Shape /*!UN0019*/Shape);
            """, references: [Verify.CompileToMetadata("""
                public record Circle;
                public record Square;
                public union Shape(Circle, Square);
                """)]
        );

        Assert.Equal("Owner prints 'Shape' as its type name in ToString: declare PrintMembers to print its Value", diagnostics[0].GetMessage());
        Assert.Single(diagnostics[0].AdditionalLocations);
    }

    [Fact]
    public async Task Union_that_records_print()
    {
        var diagnostics = await Diagnostics("""
            record Owner(Status Status, int Age);
            record class Keeper
            {
                public Status? Favourite { get; init; }
                private Status Hidden { get; init; }
            }
            record Described(Status Status)
            {
                protected virtual bool PrintMembers(System.Text.StringBuilder builder)
                {
                    builder.Append($"Status = {Status.Value}");
                    return true;
                }
            }
            record struct Entry(Status Status, Status Previous);
            record Shown(Status Status)
            {
                public override string ToString() => $"Shown {{ Status = {Status.Value} }}";
            }

            // Records derived from one that seals ToString don't generate their own.
            abstract record Audited
            {
                public sealed override string ToString() => GetType().Name;
            }
            record StatusAudited(Status Status) : Audited;
            record StatusAuditedAgain(Status Again) : StatusAudited(Again);

            record Pending;
            record Done;
            union /*!UN0019*/Status(Pending, Done);

            union ShownStatus(Pending, Done)
            {
                public override string ToString() => Value?.ToString() ?? "";
            }
            record HoldsShownStatus(ShownStatus Status, ShownStatus? Maybe);

            union /*!UN0019*/Phase(Pending, Done);
            record First(Phase Phase);
            record Second(Phase Phase);
            record Third(Phase Phase);
            record Fourth(Phase Phase);
            """
        );

        Assert.Equal(
            [
                "Owner, Keeper and Entry print 'Status' as its type name in ToString: declare PrintMembers to print its Value, or override ToString in Status",
                "First, Second and 2 more print 'Phase' as its type name in ToString: declare PrintMembers to print its Value, or override ToString in Phase",
            ],
            diagnostics.Select(d => d.GetMessage())
        );
        Assert.Equal(4, diagnostics[0].AdditionalLocations.Count);
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
                    var l = /*!UN0014*/(object)pet is not Cat;

                    // What a boxed union can be.
                    var f = (object)pet is Pet;
                    var g = (object)pet is System.Runtime.CompilerServices.IUnion;
                    var h = (object)pet is ValueType;
                    var i = (object)pet is Cat or null;
                    var j = pet is Cat;
                    var k = pet.Value is Cat;
                    var m = (object)pet is not Pet;
                    Console.WriteLine($"{a}{b}{c}{d}{e}{f}{g}{h}{i}{j}{k}{l}{m}");
                }
            }
            """
        );

        Assert.Contains("'(object)pet is Cat' is never true: a boxed Pet is a Pet, not what it holds. Test the union itself, without boxing it.", diagnostics.Select(d => d.GetMessage()));
        Assert.Contains("'(object)pet is not Cat' is always true: a boxed Pet is a Pet, not what it holds. Test the union itself, without boxing it.", diagnostics.Select(d => d.GetMessage()));
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

    [Fact]
    public async Task Unions_logged_through_generated_and_template_methods_tags_and_exceptions()
    {
        var diagnostics = await Diagnostics("""
            using System.Diagnostics;
            using Microsoft.Extensions.Logging;

            namespace Serilog.Core
            {
                [AttributeUsage(AttributeTargets.Method)]
                public sealed class MessageTemplateFormatMethodAttribute(string messageTemplateParameterName) : Attribute;
            }

            namespace JetBrains.Annotations
            {
                [AttributeUsage(AttributeTargets.Parameter)]
                public sealed class StructuredMessageTemplateAttribute : Attribute;
            }

            static class Log
            {
                [Serilog.Core.MessageTemplateFormatMethod("messageTemplate")]
                public static void Information<T>(string messageTemplate, T propertyValue) { }
            }

            static class Templated
            {
                public static void Info([JetBrains.Annotations.StructuredMessageTemplate] string message, params object?[] args) { }
            }

            class Owner
            {
                public Pet Pet { get; init; } = new Cat("Tom");
            }

            partial class C
            {
                static readonly Action<ILogger, int, Pet, Exception?> s_changed = LoggerMessage.Define<int, Pet>(LogLevel.Information, new EventId(1), "Pet {Id} is {Pet}");

                [LoggerMessage(Level = LogLevel.Information, Message = "Pet is {Pet}")]
                static partial void LogPet(ILogger logger, Pet pet);

                [LoggerMessage(Level = LogLevel.Warning, Message = "Pet is {Pet}")]
                partial void LogPetHere(Pet pet);

                void M(ILogger logger, Pet pet, Activity activity, Owner? owner)
                {
                    LogPet(logger, /*!UN0015*/pet);
                    LogPetHere(/*!UN0015*/pet);
                    s_changed(logger, 1, /*!UN0015*/pet, null);
                    Log.Information("{Pet}", /*!UN0015*/pet);
                    Templated.Info("{Pet}", /*!UN0015*/pet);
                    activity.SetTag("pet", /*!UN0015*/pet);
                    using var scope = logger.BeginScope(new List<KeyValuePair<string, object>> { new("Pet", /*!UN0015*/pet) });
                    using var dictionaryScope = logger.BeginScope(new Dictionary<string, object> { ["Pet"] = /*!UN0015*/pet });
                    var name = /*!UN0015*/pet.GetType().Name;
                    var outOfRange = new ArgumentOutOfRangeException(nameof(pet), /*!UN0015*/pet, "no birds");
                    var text = owner?/*!UN0015*/.Pet.ToString();

                    // What it holds.
                    Log.Information("{Pet}", pet.Value);
                    activity.SetTag("pet", pet.Value);
                    using var heldScope = logger.BeginScope(new List<KeyValuePair<string, object?>> { new("Pet", pet.Value) });
                    var held = pet.Value?.GetType().Name;
                    var argument = new ArgumentException("no birds", nameof(pet));
                    Console.WriteLine($"{name}{outOfRange}{text}{held}{argument}");
                }
            }
            """
        );

        var messages = diagnostics.Select(d => d.GetMessage().Replace("/*!UN0015*/", "")).ToList();
        Assert.Contains("'pet.GetType().Name' shows the union's type name, 'Pet', not what it holds: use 'pet.Value?.GetType().Name' instead", messages);
        Assert.Contains("'owner?.Pet.ToString()' shows the union's type name, 'Pet', not what it holds: format 'owner?.Pet.Value' instead, or override ToString in Pet", messages);
    }
}
