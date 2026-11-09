using UnionNarrowing.Suppressors.Analyzers;

namespace UnionNarrowing.Tests;

/// <summary>
/// UN0007: a union member some constructor leaves holding no case.
/// </summary>
public class UnassignedUnionMemberTests
{
    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> Diagnostics(string source) =>
        Verify.Diagnostics(new UnassignedUnionMemberAnalyzer(), source);

    [Fact]
    public Task Member_one_constructor_leaves_out() =>
        Diagnostics("""
            class C
            {
                readonly Pet /*!UN0007*/_pet;

                public C(Pet pet) => _pet = pet;

                public C() { }

                public Pet Read() => _pet;
            }
            """
        );

    [Fact]
    public Task Members_every_constructor_assigns() =>
        Diagnostics("""
            class C
            {
                readonly Pet _pet;
                readonly Pet _other;
                public Pet Pet { get; }

                public C(Pet pet)
                {
                    _pet = pet;
                    (_other, Pet) = (pet, pet);
                }

                // Leaves it to the constructor it calls.
                public C() : this(new Cat("Tom")) { }

                public Pet Read() => _pet;
            }

            class WithInitializers
            {
                readonly Pet _pet = new Cat("Tom");
                public Pet Pet { get; } = new Dog("Rex");
                public Pet Read() => _pet;
            }

            static class Statics
            {
                public static readonly Pet Default;
                static Statics() => Default = new Cat("Tom");
            }
            """
        );

    [Fact]
    public async Task Members_no_constructor_assigns()
    {
        var diagnostics = await Diagnostics("""
            class NoConstructor
            {
                readonly Pet /*!UN0007*/_pet;
                public Pet /*!UN0007*/Pet { get; }
                public Pet Read() => _pet;
            }

            class Primary(string name)
            {
                readonly Pet /*!UN0007*/_pet;
                readonly Pet _named = new Cat(name);
                public Pet Read() => _pet;
            }

            static class Statics
            {
                public static readonly Pet /*!UN0007*/Default;
            }
            """
        );

        Assert.Contains("'_pet' isn't assigned by every constructor, so it can be left as default(Pet), which holds none of its cases", diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public Task Members_that_are_not_reported() =>
        Diagnostics("""
            union MaybeCat(Cat?, Dog);

            class C
            {
                // Callers have to set these.
                public required Pet Required { get; set; }
                public required Pet RequiredField;
                readonly MaybeCat _maybe;
                public MaybeCat Read() => _maybe;
            }

            // A struct's fields are default whenever the struct is.
            struct S
            {
                readonly Pet _pet;
                public Pet Read() => _pet;
            }
            """
        );

    [Fact]
    public Task Settable_members_no_constructor_assigns() =>
        Diagnostics("""
            class Dto
            {
                public Pet /*!UN0007*/Settable { get; set; }
                public Pet /*!UN0007*/Initialized { get; init; }
                public Pet /*!UN0007*/Field;
                public Pet WithInitializer { get; set; } = new Cat("Tom");
            }

            class Assigned
            {
                public Pet Pet { get; set; }
                public Assigned(Pet pet) => Pet = pet;
            }
            """
        );
}
