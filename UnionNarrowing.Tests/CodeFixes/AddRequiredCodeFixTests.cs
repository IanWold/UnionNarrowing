using UnionNarrowing.Suppressors.Analyzers;
using UnionNarrowing.CodeFixes;

namespace UnionNarrowing.Tests;

/// <summary>
/// Make it required (UN0007).
/// </summary>
public class AddRequiredCodeFixTests
{
    [Fact]
    public Task Fix_all() =>
        Verify.FixAll(new AddRequiredCodeFix(), """
            public class Dto
            {
                public Pet Pet { get; set; }
                public Pet Other { get; init; }
            }
            """, """
            public class Dto
            {
                public required Pet Pet { get; set; }
                public required Pet Other { get; init; }
            }
            """, new UnassignedUnionMemberAnalyzer()
        );

    [Fact]
    public Task Make_it_required() =>
        Verify.CodeFix(new AddRequiredCodeFix(), """
            public class Dto
            {
                public Pet Pet { get; set; }
            }
            """, """
            public class Dto
            {
                public required Pet Pet { get; set; }
            }
            """, new UnassignedUnionMemberAnalyzer()
        );

    [Fact]
    public Task Make_it_required_where_the_code_that_creates_it_sets_it() =>
        Verify.CodeFix(new AddRequiredCodeFix(), """
            public class Dto
            {
                public string Name { get; set; } = "";
                public Pet Pet { get; set; }
            }

            public class Usage
            {
                Dto Created(Pet p) => new Dto { Name = "Tom", Pet = p };
                Dto TargetTyped(Pet p) => new() { Pet = p };
                Dto Copied(Dto dto) => dto;
            }
            """, """
            public class Dto
            {
                public string Name { get; set; } = "";
                public required Pet Pet { get; set; }
            }

            public class Usage
            {
                Dto Created(Pet p) => new Dto { Name = "Tom", Pet = p };
                Dto TargetTyped(Pet p) => new() { Pet = p };
                Dto Copied(Dto dto) => dto;
            }
            """, new UnassignedUnionMemberAnalyzer()
        );

    [Fact]
    public Task Make_it_required_and_add_a_using_for_SetsRequiredMembers() =>
        Verify.CodeFix(new AddRequiredCodeFix(), """
            using System;

            public class Order
            {
                public Pet Pet { get; set; }

                public Order() { }

                public Order(Pet pet) => Pet = pet;
            }
            """, """
            using System;
            using System.Diagnostics.CodeAnalysis;

            public class Order
            {
                public required Pet Pet { get; set; }

                public Order() { }

                [SetsRequiredMembers]
                public Order(Pet pet) => Pet = pet;
            }
            """, new UnassignedUnionMemberAnalyzer(), usings: false
        );

    /// <summary>
    /// With a <c>SetsRequiredMembersAttribute</c> of its own in scope, the code gets the one in System.Diagnostics.CodeAnalysis, named in full, and no using.
    /// </summary>
    [Fact]
    public Task Make_it_required_beside_a_SetsRequiredMembers_of_its_own() =>
        Verify.CodeFix(new AddRequiredCodeFix(), """
            namespace App
            {
                public class SetsRequiredMembersAttribute : System.Attribute { }

                public class Order
                {
                    public Pet Pet { get; set; }

                    public Order() { }

                    public Order(Pet pet) => Pet = pet;
                }
            }
            """, """
            namespace App
            {
                public class SetsRequiredMembersAttribute : System.Attribute { }

                public class Order
                {
                    public required Pet Pet { get; set; }

                    public Order() { }

                    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
                    public Order(Pet pet) => Pet = pet;
                }
            }
            """, new UnassignedUnionMemberAnalyzer(), usings: false
        );

    [Fact]
    public Task Make_it_required_and_say_the_constructors_that_assign_it_set_it() =>
        Verify.CodeFix(new AddRequiredCodeFix(), """
            using System.Diagnostics.CodeAnalysis;

            public class Order
            {
                public Guid Id { get; set; }
                public required string Name { get; init; }
                public Pet Pet { get; set; }

                public Order() { }

                /// <summary>A new order.</summary>
                public Order(Guid id, string name, Pet pet) { Id = id; Name = name; Pet = pet; }

                public Order(Pet pet) : this(Guid.NewGuid(), "", pet) { }
            }

            public class Usage
            {
                Order Created(Pet p) => new Order(Guid.NewGuid(), "Tom", p);
                Order Chained(Pet p) => new Order(p);
                Order Initialized(Pet p) => new Order { Name = "Rex", Pet = p };
            }
            """, """
            using System.Diagnostics.CodeAnalysis;

            public class Order
            {
                public Guid Id { get; set; }
                public required string Name { get; init; }
                public required Pet Pet { get; set; }

                public Order() { }

                /// <summary>A new order.</summary>
                [SetsRequiredMembers]
                public Order(Guid id, string name, Pet pet) { Id = id; Name = name; Pet = pet; }

                [SetsRequiredMembers]
                public Order(Pet pet) : this(Guid.NewGuid(), "", pet) { }
            }

            public class Usage
            {
                Order Created(Pet p) => new Order(Guid.NewGuid(), "Tom", p);
                Order Chained(Pet p) => new Order(p);
                Order Initialized(Pet p) => new Order { Name = "Rex", Pet = p };
            }
            """, new UnassignedUnionMemberAnalyzer()
        );

    [Fact]
    public Task Not_required_on_an_obsolete_member() =>
        Verify.CodeFix(new AddRequiredCodeFix(), """
            public class Dto
            {
                [Obsolete]
                public Pet Pet { get; set; }
            }
            """, null, new UnassignedUnionMemberAnalyzer()
        );

    [Fact]
    public Task Not_required_where_a_class_derives_from_it() =>
        Verify.CodeFix(new AddRequiredCodeFix(), """
            public class Dto
            {
                public Pet Pet { get; set; }
            }

            public class Derived : Dto { }

            public class Usage
            {
                Derived Created() => new Derived();
            }
            """, null, new UnassignedUnionMemberAnalyzer()
        );

    [Fact]
    public Task Not_required_where_a_constructor_that_assigns_it_leaves_another_required_member_to_its_callers() =>
        Verify.CodeFix(new AddRequiredCodeFix(), """
            public class Order
            {
                public required string Name { get; init; }
                public Pet Pet { get; set; }

                public Order() { }

                public Order(Pet pet) => Pet = pet;
            }
            """, null, new UnassignedUnionMemberAnalyzer()
        );

    [Fact]
    public Task Not_required_where_it_would_be_less_visible_than_its_type() =>
        Verify.CodeFix(new AddRequiredCodeFix(), """
            public class Dto
            {
                private Pet _pet;
                public Pet Read() => _pet;
                public void Write(Pet pet) => _pet = pet;
            }
            """, null, new UnassignedUnionMemberAnalyzer()
        );

    [Fact]
    public Task Not_required_where_its_setter_would_be_less_visible_than_its_type() =>
        Verify.CodeFix(new AddRequiredCodeFix(), """
            public class Dto
            {
                public Pet Pet { get; private set; }
            }
            """, null, new UnassignedUnionMemberAnalyzer()
        );

    [Fact]
    public Task Not_required_where_code_creates_it_without_setting_it() =>
        Verify.CodeFix(new AddRequiredCodeFix(), """
            public class Dto
            {
                public Pet Pet { get; set; }
            }

            public class Usage
            {
                Dto Created() => new();
            }
            """, null, new UnassignedUnionMemberAnalyzer()
        );

    [Fact]
    public Task Not_required_where_an_object_initializer_leaves_it_unset() =>
        Verify.CodeFix(new AddRequiredCodeFix(), """
            public class Dto
            {
                public string Name { get; set; } = "";
                public Pet Pet { get; set; }
            }

            public class Usage
            {
                Dto Created() => new Dto { Name = "Tom" };
            }
            """, null, new UnassignedUnionMemberAnalyzer()
        );

    /// <summary>
    /// A type with required members doesn't meet a <c>new()</c> constraint.
    /// </summary>
    [Fact]
    public Task Not_required_where_it_is_a_type_argument_for_a_new_constraint() =>
        Verify.CodeFix(new AddRequiredCodeFix(), """
            public class Dto
            {
                public Pet Pet { get; set; }
            }

            public static class Usage
            {
                static T Make<T>() where T : new() => new T();

                static Dto Created() => Make<Dto>();
            }
            """, null, new UnassignedUnionMemberAnalyzer()
        );
}
