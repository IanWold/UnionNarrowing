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
    public Task Format_the_value_in_a_conditional_access() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            record Owner(Pet Pet);

            class C
            {
                string M(Owner? owner) => $"pet: {owner?.Pet}";
            }
            """, """
            record Owner(Pet Pet);

            class C
            {
                string M(Owner? owner) => $"pet: {owner?.Pet.Value}";
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_in_conditional_accesses() =>
        Verify.FixAll(new FormatValueCodeFix(), """
            record Owner(Pet Pet, Pet? Maybe, Owner? Friend);

            class C
            {
                string M(Owner? owner, Owner[]? owners) => $"{owner?.Friend?.Pet} {owner?.Maybe} {owners?[0].Pet}";
            }
            """, """
            record Owner(Pet Pet, Pet? Maybe, Owner? Friend);

            class C
            {
                string M(Owner? owner, Owner[]? owners) => $"{owner?.Friend?.Pet.Value} {owner?.Maybe?.Value} {owners?[0].Pet.Value}";
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
    public Task Format_the_value_with_ToString_in_a_conditional_access() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            record Owner(Pet Pet);

            class C
            {
                string M(Owner? owner) => owner?.Pet.ToString() ?? "";
            }
            """, """
            record Owner(Pet Pet);

            class C
            {
                string M(Owner? owner) => owner?.Pet.Value?.ToString() ?? "";
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_with_ToString_in_conditional_accesses() =>
        Verify.FixAll(new FormatValueCodeFix(), """
            record Owner(Pet Pet, Pet? Maybe, Owner Friend);

            class C
            {
                string? M(Owner? owner, Pet? maybe) => owner?.Friend.Pet.ToString() + owner?.Maybe.ToString() + maybe?.ToString() + owner?.Maybe?.ToString();
            }
            """, """
            record Owner(Pet Pet, Pet? Maybe, Owner Friend);

            class C
            {
                string? M(Owner? owner, Pet? maybe) => owner?.Friend.Pet.Value?.ToString() + owner?.Maybe?.Value?.ToString() + maybe?.Value?.ToString() + owner?.Maybe?.Value?.ToString();
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
    public Task Format_the_value_with_ToString_then_use_it_in_a_conditional_access() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            record Owner(Pet Pet);

            class C
            {
                string? M(Owner? owner) => owner?.Pet.ToString().ToUpper();
            }
            """, """
            record Owner(Pet Pet);

            class C
            {
                string? M(Owner? owner) => owner?.Pet.Value?.ToString().ToUpper();
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

    [Fact]
    public Task Format_the_type_of_its_Value() =>
        Verify.FixAll(new FormatValueCodeFix(), """
            class C
            {
                string? M(Pet pet) => pet.GetType().Name;

                string N(Pet pet) => $"{pet.GetType().FullName}";
            }
            """, """
            class C
            {
                string? M(Pet pet) => pet.Value?.GetType().Name ?? "";

                string N(Pet pet) => $"{pet.Value?.GetType().FullName}";
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_type_of_its_Value_as_a_string() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            class C
            {
                string Name(Pet pet) => pet.GetType().Name;
            }
            """, """
            class C
            {
                string Name(Pet pet) => pet.Value?.GetType().Name ?? "";
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_type_of_its_Value_as_a_string_where_what_it_is_in_deals_with_null_or_not() =>
        Verify.FixAll(new FormatValueCodeFix(), """
            record Owner(Pet Pet);

            class C
            {
                string Concatenated(Pet pet) => "No handler for " + pet.GetType().Name;

                string Interpolated(Pet pet) => $"No handler for {pet.GetType().Name}";

                int Used(Pet pet) => pet.GetType().Name.Length;

                string Fallback(Pet pet) => pet.GetType().Name ?? "none";

                string? InAConditionalAccess(Owner? owner) => owner?.Pet.GetType().Name;
            }
            """, """
            record Owner(Pet Pet);

            class C
            {
                string Concatenated(Pet pet) => "No handler for " + (pet.Value?.GetType().Name ?? "");

                string Interpolated(Pet pet) => $"No handler for {pet.Value?.GetType().Name}";

                int Used(Pet pet) => (pet.Value?.GetType().Name ?? "").Length;

                string Fallback(Pet pet) => pet.Value?.GetType().Name ?? "none";

                string? InAConditionalAccess(Owner? owner) => owner?.Pet.Value?.GetType().Name;
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_in_an_expression_tree() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            record Order(Pet Status);

            class C
            {
                IQueryable<string> M(IQueryable<Order> orders) => orders.Select(o => "status: " + o.Status);
            }
            """, """
            record Order(Pet Status);

            class C
            {
                IQueryable<string> M(IQueryable<Order> orders) => orders.Select(o => "status: " + o.Status.Value);
            }
            """, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_not_offered_for_the_type_s_name_in_an_expression_tree() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            record Order(Pet Status);

            class C
            {
                IQueryable<string> M(IQueryable<Order> orders) => orders.Select(o => o.Status.GetType().Name);
            }
            """, null, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_not_offered_with_ToString_in_an_expression_tree() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            record Order(Pet Status);

            class Summary
            {
                public string Status { get; init; } = "";
            }

            class C
            {
                IQueryable<Summary> M(IQueryable<Order> orders) => orders.Select(o => new Summary { Status = o.Status.ToString() });
            }
            """, null, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_not_offered_for_a_LoggerMessage_method_that_takes_the_union() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            using Microsoft.Extensions.Logging;

            partial class C
            {
                [LoggerMessage(Level = LogLevel.Information, Message = "Order {Id} is {Status}")]
                static partial void LogStatusUpdated(ILogger logger, int id, Pet status);

                void M(ILogger logger, int id, Pet status) => LogStatusUpdated(logger, id, status);
            }
            """, null, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Format_the_value_not_offered_for_a_LoggerMessage_Define_delegate_that_takes_the_union() =>
        Verify.CodeFix(new FormatValueCodeFix(), """
            using Microsoft.Extensions.Logging;

            class C
            {
                static readonly Action<ILogger, int, Pet, Exception?> s_statusUpdated = LoggerMessage.Define<int, Pet>(LogLevel.Information, new EventId(1), "Order {Id} is {Status}");

                void M(ILogger logger, int id, Pet status) => s_statusUpdated(logger, id, status, null);
            }
            """, null, new BoxedUnionAnalyzer()
        );

    [Fact]
    public Task Fix_all_leaves_alone_what_takes_the_union_itself() =>
        Verify.FixAll(new FormatValueCodeFix(), """
            using Microsoft.Extensions.Logging;

            partial class C
            {
                static readonly Action<ILogger, int, Pet, Exception?> s_statusUpdated = LoggerMessage.Define<int, Pet>(LogLevel.Information, new EventId(1), "Order {Id} is {Status}");

                [LoggerMessage(Level = LogLevel.Information, Message = "Order {Id} is {Status}")]
                static partial void LogStatusUpdated(ILogger logger, int id, Pet status);

                void M(ILogger logger, int id, Pet status)
                {
                    logger.LogInformation("Order {Id} is {Status}", id, status);
                    LogStatusUpdated(logger, id, status);
                    s_statusUpdated(logger, id, status, null);
                }
            }
            """, """
            using Microsoft.Extensions.Logging;

            partial class C
            {
                static readonly Action<ILogger, int, Pet, Exception?> s_statusUpdated = LoggerMessage.Define<int, Pet>(LogLevel.Information, new EventId(1), "Order {Id} is {Status}");

                [LoggerMessage(Level = LogLevel.Information, Message = "Order {Id} is {Status}")]
                static partial void LogStatusUpdated(ILogger logger, int id, Pet status);

                void M(ILogger logger, int id, Pet status)
                {
                    logger.LogInformation("Order {Id} is {Status}", id, status.Value);
                    LogStatusUpdated(logger, id, status);
                    s_statusUpdated(logger, id, status, null);
                }
            }
            """, new BoxedUnionAnalyzer(), someLeftAlone: true
        );
}
