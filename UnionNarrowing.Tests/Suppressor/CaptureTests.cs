namespace UnionNarrowing.Tests;

/// <summary>
/// Captured variables: what a test outside a lambda, query or local function says inside it, and lambdas and local functions that write the variable.
/// </summary>
public class CaptureTests
{
    [Fact]
    public Task Captured_but_never_written() =>
        Verify.Suppressions("""
            class C
            {
                // The lambda reads pet but can't change it.
                int M(Pet pet)
                {
                    Func<string> describe = () => pet.ToString();
                    if (pet is Bird) return 0;
                    Console.WriteLine(describe());
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_inside_a_local_function_over_a_captured_value_that_never_changes() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    return Local();

                    int Local()
                    {
                        if (pet is Bird) return 0;
                        return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                }
            }
            """
        );

    [Fact]
    public Task Guard_outside_a_lambda_on_a_value_that_can_change() =>
        Verify.Suppressions("""
            class C
            {
                // Written after the lambda is created, before it runs.
                Func<int> M(Pet pet)
                {
                    if (pet is Bird) return () => 0;
                    Func<int> later = () => pet /*warns*/switch { Cat => 1, Dog => 2 };
                    /*because*/pet = new Bird("Polly");
                    return later;
                }

                // Jumping back runs the declaration again for the same variable, which the earlier lambda sees.
                List<Func<int>> O(Func<Pet> next)
                {
                    var later = new List<Func<int>>();
                Again:
                    var pet = next();
                    if (pet is Bird) return later;
                    later.Add(() => pet /*warns*/switch { Cat => 1, Dog => 2 });
                    if (later.Count < 3) goto Again;
                    return later;
                }
            }
            """
        );

    [Fact]
    public Task Guard_outside_a_lambda_on_a_value_that_never_changes() =>
        Verify.Suppressions("""
            class C
            {
                // The lambda runs after it's created, and nothing ever assigns pet, so it's still not a Bird.
                Func<int> M(Pet pet)
                {
                    if (pet is Bird) return () => 0;
                    return () => pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                IEnumerable<int> N(Pet pet, int[] counts)
                {
                    if (pet is Bird) return [];
                    return counts.Select(n => pet /*suppressed*/switch { Cat => n, Dog => -n });
                }

                IEnumerable<int> O(Pet pet, int[] counts)
                {
                    if (pet is Bird) return [];
                    return from n in counts select pet /*suppressed*/switch { Cat => n, Dog => -n };
                }
            }
            """
        );


    [Fact]
    public Task Guard_outside_a_lambda_made_after_the_last_write() =>
        Verify.Suppressions("""
            class C
            {
                Func<int> AfterTheWrites(Pet pet, bool reset)
                {
                    if (reset) pet = new Cat("Tom");
                    if (pet is Bird) return () => 0;
                    return () => pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                Func<int> WrittenAfter(Pet pet, Pet other)
                {
                    if (pet is Bird) return () => 0;
                    Func<int> legs = () => pet /*warns*/switch { Cat => 1, Dog => 2 };
                    /*because*/pet = other;
                    return legs;
                }

                List<Func<int>> InALoopThatWrites(Pet pet, Pet[] others)
                {
                    var all = new List<Func<int>>();
                    foreach (var other in others)
                    {
                        if (pet is not Bird) all.Add(() => pet /*warns*/switch { Cat => 1, Dog => 2 });
                        /*because*/pet = other;
                    }

                    return all;
                }
            }
            """
        );


    [Fact]
    public Task Lambda_made_in_the_assignment_that_writes_the_variable() =>
        Verify.Suppressions("""
            class C
            {
                static Func<int>? Saved;

                static Pet Make(Func<int> f)
                {
                    Saved = f;
                    return new Bird("Polly");
                }

                int Assigned(Pet pet)
                {
                    if (pet is Bird) return 0;
                    pet = Make(() => pet /*warns*/switch { Cat => 1, Dog => 2 });
                    return Saved!();
                }

                int Coalesced(Pet? pet)
                {
                    if (pet is null or Bird) return 0;
                    pet ??= Make(() => pet.Value /*warns*/switch { Cat => 1, Dog => 2 });
                    return Saved!();
                }
            }
            """
        );

    [Fact]
    public Task Labels_that_can_not_run_the_declaration_again() =>
        Verify.Suppressions("""
            class C
            {
                static bool Flaky() => false;

                // The goto runs before the declaration does.
                List<Func<int>> M(Func<Pet> next)
                {
                    var later = new List<Func<int>>();
                    var tries = 0;
                Retry:
                    tries++;
                    if (tries < 3 && Flaky()) goto Retry;
                    var pet = next();
                    if (pet is Bird) return later;
                    later.Add(() => pet /*suppressed*/switch { Cat => 1, Dog => 2 });
                    return later;
                }

                // The label is after the declaration, so jumping to it doesn't run the declaration again.
                List<Func<int>> N(Func<Pet> next)
                {
                    var later = new List<Func<int>>();
                    var pet = next();
                    if (pet is Bird) return later;
                    later.Add(() => pet /*suppressed*/switch { Cat => 1, Dog => 2 });
                More:
                    later.Add(() => later.Count);
                    if (later.Count < 3) goto More;
                    return later;
                }
            }
            """
        );

    [Fact]
    public Task Lambda_in_another_top_level_statement_that_writes_the_variable() =>
        Verify.Suppressions("""
            Pet pet = new Cat("Tom");
            Action reset = () => pet = new Bird("Polly");
            {
                if (pet is Bird) return;
                reset();
                Console.WriteLine(pet /*warns*/switch { Cat => 1, Dog => 2 });
            }
            """, topLevelStatements: true
        );

    [Fact]
    public Task Lambda_that_writes_the_variable() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    Action reset = () => pet = new Bird("Polly");
                    if (pet is Bird) return 0;
                    reset();
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Local_function_that_captures_the_value() =>
        Verify.Suppressions("""
            class C
            {
                int AfterTheGuard(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return Local();

                    int Local() => pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // pet changes between the calls, but each call comes after a guard.
                int EveryCall(Pet pet, Func<Pet> next)
                {
                    if (pet is Bird) return 0;
                    var total = Local();
                    pet = next();
                    if (pet is Bird) return total;
                    return total + Local();

                    int Local() => pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int CalledBeforeTheGuard(Pet pet)
                {
                    var first = Local();
                    if (pet is Bird) return 0;
                    return first + Local();

                    int Local() => pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int TurnedIntoADelegate(Pet pet)
                {
                    if (pet is Bird) return 0;
                    Func<int> f = Local;
                    return f();

                    int Local() => pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // pet never changes, so it's the same whenever the rest of the body runs.
                async System.Threading.Tasks.Task<int> AsyncOverAValueThatNeverChanges(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return await Local();

                    async System.Threading.Tasks.Task<int> Local()
                    {
                        await System.Threading.Tasks.Task.Yield();
                        return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                }

                // The rest of the body runs later, after pet has changed.
                async System.Threading.Tasks.Task<int> Async(Pet pet, Func<Pet> next)
                {
                    if (pet is Bird) return 0;
                    var task = Local();
                    /*because*/pet = next();
                    return await task;

                    async System.Threading.Tasks.Task<int> Local()
                    {
                        await System.Threading.Tasks.Task.Yield();
                        return pet /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                }

                IEnumerable<int> Iterator(Pet pet, Func<Pet> next)
                {
                    if (pet is Bird) return [];
                    var items = Local();
                    /*because*/pet = next();
                    return items;

                    IEnumerable<int> Local()
                    {
                        yield return pet /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                }

                // The local function writes it.
                int Writes(Pet pet)
                {
                    if (pet is Bird) return 0;
                    return Local();

                    int Local()
                    {
                        var result = pet /*warns*/switch { Cat => 1, Dog => 2 };
                        pet = new Bird("Polly");
                        return result;
                    }
                }
            }
            """
        );

    [Fact]
    public Task Local_function_that_writes_the_variable() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    Reset();
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };

                    void Reset() => pet = new Bird("Polly");
                }
            }
            """
        );

    [Fact]
    public Task Parameter_of_an_enclosing_function_written_by_a_sibling_lambda() =>
        Verify.Suppressions("""
            class C
            {
                static int Run(Action reset, Func<Action, int> body) => body(reset);

                int M(Pet pet) => Run(
                    () => pet = new Bird("Polly"),
                    reset =>
                    {
                        if (pet is Bird) return 0;
                        reset();
                        return pet /*warns*/switch { Cat => 1, Dog => 2 };
                    });
            }
            """
        );

    [Fact]
    public Task Query_that_captures_the_variable_runs_later() =>
        Verify.Suppressions("""
            class C
            {
                IEnumerable<int> M(Pet pet)
                {
                    if (pet is Bird) return [];
                    var query = from i in new[] { 1, 2 } select pet /*warns*/switch { Cat => i, Dog => -i };
                    /*because*/pet = new Bird("Polly");
                    return query; // the switch runs when this is enumerated, on a Bird
                }
            }
            """
        );
}
