using UnionNarrowing.Suppressors.Analyzers;
using UnionNarrowing.CodeFixes;

namespace UnionNarrowing.Tests;

/// <summary>
/// Format a union's Value instead of the union (UN0015).
/// </summary>
public class FormatValueCodeFixTests
{
    [Fact]
    public Task Fix_all() =>
        Verify.FixAll(new FormatValueCodeFix(), """
            class C
            {
                string M(Pet pet, Pet other) => $"{pet} and {other}";
            }
            """, """
            class C
            {
                string M(Pet pet, Pet other) => $"{pet.Value} and {other.Value}";
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Fix_all_with_one_using_for_LINQ() =>
        Verify.FixAll(new FormatValueCodeFix(), """
            using System.Collections.Generic;

            class C
            {
                string M(List<Pet> pets) => string.Join(", ", pets);

                string N(Pet[] pets) => string.Concat(pets);

                string O(IEnumerable<Pet> pets) => string.Join("; ", pets);
            }
            """, """
            using System.Collections.Generic;
            using System.Linq;

            class C
            {
                string M(List<Pet> pets) => string.Join(", ", pets.Select(x => x.Value));

                string N(Pet[] pets) => string.Concat(pets.Select(x => x.Value));

                string O(IEnumerable<Pet> pets) => string.Join("; ", pets.Select(x => x.Value));
            }
            """, new BoxedUnionAnalyzer(), usings: false
        );

    [Fact]
    public Task Format_the_value() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            class C
            {
                string M(Pet pet) => $"pet: {pet}";
            }
            """, """
            class C
            {
                string M(Pet pet) => $"pet: {pet.Value}";
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_of_a_nullable_union() =>
        Verify.FixAll(new FormatValueCodeFix(), """
            class C
            {
                string M(Pet? maybe, List<Pet?> maybes) => $"{maybe} {maybe.ToString()} {string.Join(", ", maybes)}";
            }
            """, """
            class C
            {
                string M(Pet? maybe, List<Pet?> maybes) => $"{maybe?.Value} {maybe?.Value?.ToString()} {string.Join(", ", maybes.Select(x => x?.Value))}";
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_in_a_concatenation() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            class C
            {
                string M(Pet pet) => "pet: " + pet;
            }
            """, """
            class C
            {
                string M(Pet pet) => "pet: " + pet.Value;
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_passed_to_WriteLine() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            class C
            {
                void M(Pet pet) => Console.WriteLine(pet);
            }
            """, """
            class C
            {
                void M(Pet pet) => Console.WriteLine(pet.Value);
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_with_ToString() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            class C
            {
                string M(Pet pet)
                {
                    string text = pet.ToString();
                    return text;
                }
            }
            """, """
            class C
            {
                string M(Pet pet)
                {
                    string text = pet.Value?.ToString() ?? "";
                    return text;
                }
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_with_ToString_and_a_fallback() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            class C
            {
                string M(Pet pet) => pet.ToString() ?? "none";
            }
            """, """
            class C
            {
                string M(Pet pet) => pet.Value?.ToString() ?? "none";
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_with_ToString_in_a_concatenation() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            class C
            {
                string M(Pet pet) => "pet: " + pet.ToString();
            }
            """, """
            class C
            {
                string M(Pet pet) => "pet: " + (pet.Value?.ToString() ?? "");
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_with_ToString_then_use_it() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            class C
            {
                string M(Pet pet) => pet.ToString().ToUpper();
            }
            """, """
            class C
            {
                string M(Pet pet) => (pet.Value?.ToString() ?? "").ToUpper();
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_with_ToString_then_use_it_if_there_is_one() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            class C
            {
                int? M(Pet pet) => pet.ToString()?.Length;
            }
            """, """
            class C
            {
                int? M(Pet pet) => pet.Value?.ToString()?.Length;
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_values_of_a_sequence() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            class C
            {
                string M(List<Pet> pets) => string.Join(", ", pets);
            }
            """, """
            class C
            {
                string M(List<Pet> pets) => string.Join(", ", pets.Select(x => x.Value));
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_values_of_a_sequence_where_a_namespace_uses_LINQ() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            using System.Collections.Generic;

            namespace App
            {
                using System.Linq;

                class C
                {
                    string M(List<Pet> pets) => string.Join(", ", pets);
                }
            }
            """, """
            using System.Collections.Generic;

            namespace App
            {
                using System.Linq;

                class C
                {
                    string M(List<Pet> pets) => string.Join(", ", pets.Select(x => x.Value));
                }
            }
            """, new BoxedUnionAnalyzer(), usings: false
        );

    [Fact]
    public Task Format_the_values_of_a_sequence_with_no_using_for_LINQ() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            using System.Collections.Generic;

            class C
            {
                string M(List<Pet> pets) => string.Join(", ", pets);
            }
            """, """
            using System.Collections.Generic;
            using System.Linq;

            class C
            {
                string M(List<Pet> pets) => string.Join(", ", pets.Select(x => x.Value));
            }
            """, new BoxedUnionAnalyzer(), usings: false
        );
}
