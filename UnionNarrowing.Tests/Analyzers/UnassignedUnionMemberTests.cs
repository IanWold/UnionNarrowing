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
    public Task Member_a_constructor_assigns_on_only_some_paths() =>
        Diagnostics("""
            class C
            {
                readonly Pet /*!UN0007*/_conditional;
                readonly Pet /*!UN0007*/_afterReturn;
                readonly Pet /*!UN0007*/_inLoop;
                readonly Pet _bothBranches;
                readonly Pet _ternary;
                readonly Pet _coalesced;
                readonly Pet _afterThrow;
                readonly Pet _deconstructed;
                readonly Pet _out;

                public C(bool flag, Pet pet, Pet? maybe, List<Pet> pets)
                {
                    if (flag) _conditional = pet;

                    if (flag) _bothBranches = pet;
                    else _bothBranches = new Cat("Tom");

                    _ternary = flag ? pet : new Dog("Rex");
                    _coalesced = maybe ?? pet;
                    (_deconstructed, _) = (pet, 1);
                    Make(out _out);

                    foreach (var each in pets)
                        _inLoop = each;

                    if (pet is Bird) throw new ArgumentException();
                    _afterThrow = pet;

                    if (!flag) return;
                    _afterReturn = pet;
                }

                static void Make(out Pet pet) => pet = new Cat("Tom");
                public Pet Read() => _conditional;
            }
            """
        );

    [Fact]
    public Task Member_a_member_not_null_helper_assigns() =>
        Diagnostics("""
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                private Pet _state;
                private Pet _other;
                private Pet /*!UN0007*/_unnamed;

                public C(Pet pet)
                {
                    Reset(pet);
                    SetUnnamed(pet);
                }

                public C() : this(new Cat("Tom")) { }

                [MemberNotNull(nameof(_state), nameof(_other))]
                private void Reset(Pet pet) => (_state, _other) = (pet, pet);

                // Doesn't say what it assigns.
                private void SetUnnamed(Pet pet) => _unnamed = pet;

                public Pet Read() => _state;
            }

            class NotCalled
            {
                private Pet /*!UN0007*/_state;

                [MemberNotNull(nameof(_state))]
                private void Reset(Pet pet) => _state = pet;

                public Pet Read() => _state;
            }
            """
        );

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
    public Task Members_of_records() =>
        Diagnostics("""
            // The copy constructor `with` uses copies every member from an object that was already constructed.
            public record R1
            {
                public Pet A { get; init; }
                public R1(Pet a) { A = a; }
            }

            record R2
            {
                readonly Pet _a;
                public R2(Pet a) { _a = a; }
                public Pet Read() => _a;
            }

            record Positional(Pet Pet);

            record LeftOut
            {
                readonly Pet /*!UN0007*/_a;
                public LeftOut() { }
                public Pet Read() => _a;
            }
            """
        );

    [Fact]
    public Task Members_with_a_backing_field_of_their_own() =>
        Diagnostics("""
            class FieldKeyword
            {
                public Pet /*!UN0007*/State { get => field; set => field = value; }
                public Pet Initialized { get => field; set => field = value; } = new Cat("Tom");
                public Pet Assigned { get => field; set => field = value; }
                public FieldKeyword(Pet pet) => Assigned = pet;
            }

            partial class PartialProperties
            {
                public partial Pet Explicit { get; set; }
                public partial Pet /*!UN0007*/Implicit { get; set; }
                public partial Pet Assigned { get; set; }
            }

            partial class PartialProperties
            {
                // The property's own field, which it isn't an auto-property of.
                private Pet /*!UN0007*/_explicit;
                public partial Pet Explicit { get => _explicit; set => _explicit = value; }
                public partial Pet Implicit { get => field; set => field = value; }
                public partial Pet Assigned { get => field; set => field = value; }
                public PartialProperties(Pet pet) => Assigned = pet;
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

        var messages = diagnostics.Select(d => d.GetMessage()).ToList();
        Assert.Contains("'_pet' isn't assigned by every constructor, so it can be left as default(Pet), which holds none of its cases", messages);
        Assert.Contains("'Default' isn't assigned by an initializer or the static constructor, so it can be left as default(Pet), which holds none of its cases", messages);
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

                // Meant to start out as the default.
                public Pet Forgiven { get; set; } = default!;
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
    public async Task Required_members_a_constructor_says_it_sets()
    {
        var diagnostics = await Diagnostics("""
            using System.Diagnostics.CodeAnalysis;

            class SaysItSets
            {
                public required Pet /*!UN0007*/State { get; init; }

                [SetsRequiredMembers]
                public SaysItSets() { }

                // Callers set it.
                public SaysItSets(int _) { }
            }

            [method: SetsRequiredMembers]
            class PrimarySaysItSets()
            {
                public required Pet /*!UN0007*/State { get; init; }
            }

            class Sets
            {
                public required Pet State { get; init; }

                [SetsRequiredMembers]
                public Sets(Pet pet) => State = pet;
            }
            """
        );

        Assert.Contains("'State' isn't assigned by every constructor with [SetsRequiredMembers], so it can be left as default(Pet), which holds none of its cases", diagnostics.Select(d => d.GetMessage()));
    }

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

    [Fact]
    public async Task Struct_members_with_a_union_inside()
    {
        var diagnostics = await Diagnostics("""
            struct Entry { public Pet Pet; public int N; }

            class C
            {
                private Entry /*!UN0007*/_entry;
                private Entry _assigned;
                private DateTime _when;

                public C(Entry entry) => _assigned = entry;

                public int Read() => _entry.N + _assigned.N + _when.Day;
            }
            """
        );

        Assert.Equal("'_entry' isn't assigned by every constructor, so it can be left as default(Entry), with default(Pet) in Entry.Pet, which holds none of its cases", diagnostics.Single().GetMessage());
    }
}
