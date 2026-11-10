using UnionNarrowing.CodeFixes;

namespace UnionNarrowing.Tests;

/// <summary>
/// Add missing cases, for IDE0010 on a switch statement.
/// </summary>
public class AddMissingCasesCodeFixTests
{
    [Fact]
    public Task Add_missing_cases_to_a_switch_statement() =>
        Verify.CodeFix(new AddMissingCasesCodeFix(), """
            class C
            {
                void M(Pet pet)
                {
                    if (pet is Bird) return;
                    switch (pet)
                    {
                        case Cat:
                            Console.WriteLine("cat");
                            break;
                    }
                }
            }
            """, """
            class C
            {
                void M(Pet pet)
                {
                    if (pet is Bird) return;
                    switch (pet)
                    {
                        case Cat:
                            Console.WriteLine("cat");
                            break;
                        case Dog:
                            throw new NotImplementedException();
                    }
                }
            }
            """, Verify.IdePopulateSwitchStatement
        );

    [Fact]
    public Task Add_missing_cases_qualifies_a_case_type_a_member_of_its_name_hides() =>
        Verify.CodeFix(new AddMissingCasesCodeFix(), """
            namespace Shop.Models
            {
                public record Success;
                public record Failure(string Reason);
                public union Outcome(Success, Failure);
            }

            namespace Shop.Api
            {
                using Shop.Models;

                class Response
                {
                    public Failure Failure { get; } = new("x");

                    void M(Outcome outcome)
                    {
                        switch (outcome)
                        {
                            case Success:
                                break;
                        }
                    }
                }
            }
            """, """
            namespace Shop.Models
            {
                public record Success;
                public record Failure(string Reason);
                public union Outcome(Success, Failure);
            }

            namespace Shop.Api
            {
                using Shop.Models;

                class Response
                {
                    public Failure Failure { get; } = new("x");

                    void M(Outcome outcome)
                    {
                        switch (outcome)
                        {
                            case Success:
                                break;
                            case Models.Failure:
                                throw new NotImplementedException();
                        }
                    }
                }
            }
            """, Verify.IdePopulateSwitchStatement
        );

    [Fact]
    public Task Fix_all() =>
        Verify.FixAll(new AddMissingCasesCodeFix(), """
            class C
            {
                void M(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat:
                            break;
                    }
                }

                void N(Pet pet)
                {
                    if (pet is Bird) return;
                    switch (pet)
                    {
                        case Dog:
                            break;
                    }
                }
            }
            """, """
            class C
            {
                void M(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat:
                            break;
                        case Dog:
                            throw new NotImplementedException();
                        case Bird:
                            throw new NotImplementedException();
                    }
                }

                void N(Pet pet)
                {
                    if (pet is Bird) return;
                    switch (pet)
                    {
                        case Dog:
                            break;
                        case Cat:
                            throw new NotImplementedException();
                    }
                }
            }
            """, Verify.IdePopulateSwitchStatement
        );

    [Fact]
    public Task Fix_all_with_one_using_for_the_exception() =>
        Verify.FixAll(new AddMissingCasesCodeFix(), """
            class C
            {
                void M(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat or Dog:
                            break;
                    }
                }

                void N(Pet pet)
                {
                    switch (pet)
                    {
                        case Bird:
                            break;
                    }
                }
            }
            """, """
            using System;

            class C
            {
                void M(Pet pet)
                {
                    switch (pet)
                    {
                        case Cat or Dog:
                            break;
                        case Bird:
                            throw new NotImplementedException();
                    }
                }

                void N(Pet pet)
                {
                    switch (pet)
                    {
                        case Bird:
                            break;
                        case Cat:
                            throw new NotImplementedException();
                        case Dog:
                            throw new NotImplementedException();
                    }
                }
            }
            """, Verify.IdePopulateSwitchStatement, usings: false
        );
}
