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
}
