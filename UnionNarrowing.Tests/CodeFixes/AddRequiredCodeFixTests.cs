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
}
