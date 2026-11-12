using UnionNarrowing.Suppressors.Analyzers;
using UnionNarrowing.CodeFixes;

namespace UnionNarrowing.Tests;

/// <summary>
/// Look at what a boxed union holds (UN0014).
/// </summary>
public class BoxedUnionCodeFixTests
{
    [Fact]
    public Task As_the_value() =>
        Verify.CodeFix(new BoxedUnionCodeFix(), """
            class C
            {
                Cat? M(Pet pet) => (object)pet as Cat;
            }
            """, """
            class C
            {
                Cat? M(Pet pet) => pet.Value as Cat;
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Cast_the_value() =>
        Verify.CodeFix(new BoxedUnionCodeFix(), """
            class C
            {
                Cat M(Pet pet) => (Cat)(object)pet;
            }
            """, """
            class C
            {
                Cat M(Pet pet) => (Cat)pet.Value!;
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Compare_the_value_instead_of_the_union() =>
        Verify.CodeFix(new BoxedUnionCodeFix(), """
            class C
            {
                bool M(Pet pet, Cat cat) => pet.Equals(cat);
            }
            """, """
            class C
            {
                bool M(Pet pet, Cat cat) => object.Equals(pet.Value, cat);
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Compare_the_value_instead_of_the_union_in_a_conditional_access() =>
        Verify.CodeFix(new BoxedUnionCodeFix(), """
            record Owner(Pet Pet);

            class C
            {
                bool? M(Owner? owner, Cat cat) => owner?.Pet.Equals(cat);
            }
            """, """
            record Owner(Pet Pet);

            class C
            {
                bool? M(Owner? owner, Cat cat) => owner?.Pet.Value?.Equals(cat);
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Fix_all() =>
        Verify.FixAll(new BoxedUnionCodeFix(), """
            class C
            {
                int M(Pet pet) => ((object)pet) switch { Cat => 1, Dog => 2, _ => 0 };

                bool N(Pet pet) => (object)pet is Bird;
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, Dog => 2, _ => 0 };

                bool N(Pet pet) => pet is Bird;
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Get_the_type_of_the_value() =>
        Verify.CodeFix(new BoxedUnionCodeFix(), """
            class C
            {
                bool M(Pet pet) => pet.GetType() == typeof(Cat);
            }
            """, """
            class C
            {
                bool M(Pet pet) => pet.Value?.GetType() == typeof(Cat);
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Get_the_type_of_the_value_not_offered_in_an_expression_tree() =>
        Verify.CodeFix(new BoxedUnionCodeFix(), """
            record Row(Pet Result);

            class C
            {
                IQueryable<Row> M(IQueryable<Row> rows) => rows.Where(x => x.Result.GetType() == typeof(Cat));
            }
            """, null, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Get_the_type_of_the_value_with_not_equals() =>
        Verify.CodeFix(new BoxedUnionCodeFix(), """
            class C
            {
                bool M(Pet pet) => pet.GetType() != typeof(Cat);
            }
            """, """
            class C
            {
                bool M(Pet pet) => pet.Value?.GetType() != typeof(Cat);
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Switch_on_the_union_instead_of_the_box() =>
        Verify.CodeFix(new BoxedUnionCodeFix(), """
            class C
            {
                int M(Pet pet) => ((object)pet) switch { Cat => 1, _ => 0 };
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, _ => 0 };
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Switch_statement_on_the_union_instead_of_the_box() =>
        Verify.CodeFix(new BoxedUnionCodeFix(), """
            class C
            {
                int M(Pet pet)
                {
                    switch ((object)pet)
                    {
                        case Cat: return 1;
                        default: return 0;
                    }
                }
            }
            """, """
            class C
            {
                int M(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat: return 1;
                        default: return 0;
                    }
                }
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Test_the_union_instead_of_the_box() =>
        Verify.CodeFix(new BoxedUnionCodeFix(), """
            class C
            {
                bool M(Pet pet) => (object)pet is Cat;
            }
            """, """
            class C
            {
                bool M(Pet pet) => pet is Cat;
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Test_the_union_instead_of_the_box_not_offered_in_an_expression_tree() =>
        Verify.CodeFix(new BoxedUnionCodeFix(), """
            record Row(Pet Result);

            class C
            {
                IQueryable<Row> M(IQueryable<Row> rows) => rows.Where(x => (object)x.Result is Cat);
            }
            """, null, new BoxedUnionAnalyzer()
        );
}
