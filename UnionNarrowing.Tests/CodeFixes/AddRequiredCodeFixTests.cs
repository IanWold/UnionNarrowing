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
