namespace UnionNarrowing.Tests;

/// <summary>
/// Loops: what a loop's condition says in its body and after it, and facts from before a loop that contains the switch.
/// </summary>
public class LoopTests
{
    private const string Next = """

        static class Pets
        {
            // Could be anything, as far as the analysis can tell.
            public static Pet Next() => Random.Shared.Next(3) switch { 0 => new Cat("Tom"), 1 => new Dog("Rex"), _ => new Bird("Polly") };

            public static bool Reset(out Pet pet)
            {
                pet = new Bird("Polly");
                return true;
            }
        }
        """;

    [Fact]
    public Task After_a_loop_left_by_a_break() =>
        Verify.Suppressions("""
            class C
            {
                int WhileTrue(Func<Pet> next)
                {
                    Pet pet;
                    while (true)
                    {
                        pet = next();
                        if (pet is not Bird) break;
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int ForEver(Func<Pet> next)
                {
                    Pet pet;
                    for (;;)
                    {
                        pet = next();
                        if (pet is Cat or Dog) break;
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // Ended by its condition or a break, with something known either way.
                int ConditionAndBreak(Pet pet, Func<Pet> next)
                {
                    while (pet is not Cat)
                    {
                        if (pet is Dog) break;
                        pet = next();
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // Inner loops and switches have breaks of their own, in a loop its condition ends...
                int InnerBreaksBesideTheCondition(Pet pet, int n, Func<Pet> next)
                {
                    while (pet is Bird)
                    {
                        switch (n)
                        {
                            case 1:
                                break;
                        }

                        foreach (var c in "ab")
                        {
                            if (c == 'b') break;
                        }

                        pet = next();
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // ...and in one only a break ends.
                int InnerBreaks(Func<Pet> next, int[] counts)
                {
                    Pet pet;
                    while (true)
                    {
                        foreach (var count in counts) { if (count > 0) break; }
                        switch (counts.Length) { case 0: break; }
                        pet = next();
                        if (pet is not Bird) break;
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // Another break, where nothing is known.
                int AnotherBreak(Func<Pet> next, bool stop)
                {
                    Pet pet;
                    while (true)
                    {
                        pet = next();
                        if (stop) break;
                        if (pet is not Bird) break;
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // The condition rules out Bird, but the break leaves while it's still one.
                int BreakWhereTheConditionStillHolds(Pet pet, int limit, Func<Pet> next)
                {
                    while (pet is Bird)
                    {
                        if (--limit == 0) break;
                        pet = next();
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // The loop can also end when its condition is false.
                int ConditionCanEndIt(Pet pet, Func<Pet> next, Func<bool> more)
                {
                    while (more())
                    {
                        if (pet is not Bird) break;
                        pet = next();
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // A finally on the way out runs after the break.
                int FinallyAfterTheBreak(Func<Pet> next)
                {
                    Pet pet = next();
                    while (true)
                    {
                        try
                        {
                            if (pet is not Bird) break;
                        }
                        finally
                        {
                            pet = next();
                        }
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task After_a_loop_nothing_from_before_it_holds() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Dog) return 0;
                    while (pet is Bird) pet = Pets.Next();
                    // The loop could have turned pet into a Dog.
                    return pet /*warns*/switch { Cat => 1 };
                }
            }
            """ + Next
        );

    [Fact]
    public Task After_a_loop_that_writes_the_variable() =>
        Verify.Suppressions("""
            class C
            {
                int While(Pet pet)
                {
                    while (pet is Bird) pet = Pets.Next();
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Do(Pet pet)
                {
                    do
                    {
                        pet = Pets.Next();
                    }
                    while (pet is Bird);
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int For(Pet pet)
                {
                    for (var tries = 0; pet is Bird or Dog; tries++)
                    {
                        pet = Pets.Next();
                    }
                    return pet /*suppressed*/switch { Cat => 1 };
                }
            }
            """ + Next
        );


    [Fact]
    public Task Local_whose_every_write_rules_a_case_out() =>
        Verify.Suppressions("""
            class C
            {
                int StateMachine(int steps)
                {
                    Pet pet = new Cat("Tom");
                    var total = 0;
                    for (var i = 0; i < steps; i++)
                    {
                        total += pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                        pet = pet switch { Cat => new Dog("Rex"), _ => new Cat("Tom") };
                    }

                    return total;
                }

                int Fold(IEnumerable<Pet> pets)
                {
                    Pet last = new Cat("Tom");
                    foreach (var pet in pets)
                    {
                        if (pet is Bird) continue;
                        last = pet;
                    }

                    return last /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int OneWriteCouldBeABird(int steps, Pet other)
                {
                    Pet pet = new Cat("Tom");
                    for (var i = 0; i < steps; i++)
                    {
                        pet = i % 2 == 0 ? new Dog("Rex") : other;
                    }

                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int WrittenByAnOutArgument(int steps)
                {
                    Pet pet = new Cat("Tom");
                    for (var i = 0; i < steps; i++)
                    {
                        Pets.Reset(out pet);
                    }

                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """ + Next
        );

    [Fact]
    public Task Foreach_iteration_variables() =>
        Verify.Suppressions("""
            class C
            {
                int Deconstructed(List<(Pet, Pet)> pairs)
                {
                    var total = 0;
                    foreach (var (a, b) in pairs)
                    {
                        if (a is Bird || b is Bird) continue;
                        total += (a, b) /*suppressed*/switch { (Cat, _) => 1, (Dog, Cat or Dog) => 2 };
                    }
                    return total;
                }

                async System.Threading.Tasks.Task<int> Async(IAsyncEnumerable<Pet> pets)
                {
                    var total = 0;
                    await foreach (var pet in pets)
                    {
                        if (pet is not (Cat or Dog)) continue;
                        total += pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                    return total;
                }

                // Each lambda gets the variable of its own iteration, which nothing can change.
                List<Func<int>> Captured(Pet[] pets)
                {
                    var later = new List<Func<int>>();
                    foreach (var pet in pets)
                    {
                        if (pet is Bird) continue;
                        later.Add(() => pet /*suppressed*/switch { Cat => 1, Dog => 2 });
                    }
                    return later;
                }
            }
            """
        );

    [Fact]
    public Task Guard_inside_the_loop_body_runs_every_iteration() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, int n)
                {
                    var total = 0;
                    for (var i = 0; i < n; i++)
                    {
                        if (pet is Bird) continue;
                        total += pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                        pet = new Bird("Polly");
                    }
                    return total;
                }
            }
            """
        );

    [Fact]
    public Task In_the_body_of_a_do_loop() =>
        Verify.Suppressions("""
            class C
            {
                // The body runs once before the condition is checked, and nothing is known on the way in.
                int NothingOnTheWayIn(Pet pet)
                {
                    var total = 0;
                    do
                    {
                        total += pet /*warns*/switch { Cat => 1, Dog => 2 };
                        pet = Pets.Next();
                    }
                    while (pet is not Bird);
                    return total;
                }

                // Not a Bird on the way in, and not one whenever the condition sends it round again.
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    var total = 0;
                    do
                    {
                        total += pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                        pet = Pets.Next();
                    }
                    while (pet is not Bird);
                    return total;
                }

                // Only the condition rules out Dog, so the first time through it could be one.
                int N(Pet pet)
                {
                    if (pet is Bird) return 0;
                    var total = 0;
                    do
                    {
                        total += pet /*warns*/switch { Cat => 1 };
                        pet = Pets.Next();
                    }
                    while (pet is Cat);
                    return total;
                }

                // Writes before the switch in the body.
                int O(Pet pet)
                {
                    if (pet is Bird) return 0;
                    var total = 0;
                    do
                    {
                        pet = Pets.Next();
                        total += pet /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                    while (pet is not Bird);
                    return total;
                }
            }
            """ + Next
        );

    [Fact]
    public Task In_the_body_of_a_loop() =>
        Verify.Suppressions("""
            class C
            {
                int While(Pet pet)
                {
                    var total = 0;
                    while (pet is not Bird)
                    {
                        total += pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                        pet = Pets.Next();
                    }
                    return total;
                }

                int For(Pet pet)
                {
                    var total = 0;
                    for (var i = 0; pet is not Bird && i < 10; i++, pet = Pets.Next())
                    {
                        if (i % 2 == 0)
                            total += pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                    return total;
                }
            }
            """ + Next
        );

    [Fact]
    public Task In_the_body_of_a_loop_that_writes_before_the_switch() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    var total = 0;
                    while (pet is not Bird)
                    {
                        /*because*/pet = Pets.Next();
                        total += pet /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                    return total;
                }

                // Not a block, and the write comes before the switch.
                int N(Pet pet)
                {
                    var total = 0;
                    while (pet is not Bird) total += Pets.Reset(out pet) ? pet /*warns*/switch { Cat => 1, Dog => 2 } : 0;
                    return total;
                }
            }
            """ + Next
        );

    [Fact]
    public Task Loop_body_that_is_an_assignment_from_the_switch() =>
        Verify.Suppressions("""
            class C
            {
                // The assignment only writes once the switch is done, and the condition is checked again before the next one.
                Pet M(Pet pet)
                {
                    while (pet is not Bird) pet = pet /*suppressed*/switch { Cat => new Dog("Rex"), Dog => new Bird("Polly") };
                    return pet;
                }
            }
            """ + Next
        );

    [Fact]
    public Task Loop_whose_condition_assigns_what_it_tests() =>
        Verify.Suppressions("""
            class C
            {
                int After(Func<Pet> next)
                {
                    Pet pet;
                    while ((pet = next()) is Bird) { }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int InTheBody(Func<Pet> next)
                {
                    Pet pet;
                    var total = 0;
                    while ((pet = next()) is not Bird)
                        total += pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return total;
                }

                int BodyWritesFirst(Func<Pet> next, Pet other)
                {
                    Pet pet;
                    var total = 0;
                    while ((pet = next()) is not Bird)
                    {
                        /*because*/pet = other;
                        total += pet /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                    return total;
                }
            }
            """
        );

    [Fact]
    public Task Switch_in_a_loop_that_does_not_write_the_variable() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, int n)
                {
                    if (pet is Bird) return 0;
                    var total = 0;
                    for (var i = 0; i < n; i++)
                    {
                        total += pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                    return total;
                }
            }
            """
        );

    [Fact]
    public Task Switch_in_a_loop_that_writes_the_variable_after_the_switch() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, int n)
                {
                    if (pet is Bird) return 0;
                    var total = 0;
                    for (var i = 0; i < n; i++)
                    {
                        total += pet /*warns*/switch { Cat => 1, Dog => 2 };
                        /*because*/pet = new Bird("Polly");
                    }
                    return total;
                }
            }
            """
        );

    [Fact]
    public Task Switch_in_a_loop_whose_incrementor_writes_the_variable() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, int n)
                {
                    if (pet is Bird) return 0;
                    var total = 0;
                    for (var i = 0; i < n; i++, pet = new Bird("Polly"))
                    {
                        total += pet /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                    return total;
                }
            }
            """
        );

    [Fact]
    public Task Accumulator_whose_arms_pass_the_state_on() =>
        Verify.Suppressions("""
            public record Waiting;
            public record Running(int Step);
            public record Failed(string Error);
            public union Job(Waiting, Running, Failed);

            class C
            {
                // It starts Waiting, and never becomes Failed: each step only gives back what it had.
                int Run(int steps)
                {
                    Job job = new Waiting();
                    for (var i = 0; i < steps; i++)
                    {
                        job = job switch { Waiting => new Running(0), Running r => new Running(r.Step + 1), Failed f => f };
                    }

                    return job /*suppressed*/switch { Waiting => 0, Running r => r.Step };
                }

                int Other(int steps)
                {
                    Job job = new Waiting();
                    for (var i = 0; i < steps; i++)
                    {
                        job = job switch { Waiting => new Running(0), Running r => r.Step > 3 ? new Failed("") : r, var other => other };
                    }

                    return job /*warns*/switch { Waiting => 0, Running r => r.Step };
                }
            }
            """
        );
}
