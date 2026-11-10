namespace UnionNarrowing.Tests;

/// <summary>
/// Where a guard before the switch can be, and how it has to exit, for every path to the switch to pass it.
/// </summary>
public class ControlFlowTests
{
    [Fact]
    public Task All_kinds_of_statements_in_between() =>
        Verify.Suppressions("""
            class C
            {
                readonly object _gate = new();

                async System.Threading.Tasks.Task<int> M(Pet pet, int n)
                {
                    if (pet is Bird) return 0;
                    ;
                    int local = 1;
                    const int constant = 2;
                    using var stream = new System.IO.MemoryStream();
                    lock (_gate) { local++; }
                    checked { local += constant; }
                    unchecked { local -= 1; }
                    try { local++; } catch (Exception) { local--; } finally { local++; }
                    switch (n) { case 1: local++; break; default: break; }
                    while (local < 0) { local++; }
                    do { local++; } while (local < 0);
                    foreach (var c in "ab") { local++; }
                    await System.Threading.Tasks.Task.Yield();
                    int Twice(int x) => x * 2;
                    local = Twice(local);
                    return local + pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Both_branches_rule_the_same_case_out() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, bool flag)
                {
                    if (flag)
                    {
                        if (pet is Bird) return 0;
                    }
                    else
                    {
                        if (pet is Bird) throw new InvalidOperationException();
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet pet, bool flag)
                {
                    if (flag)
                    {
                        if (pet is Bird) return 0;
                    }
                    else
                    {
                        Console.WriteLine("didn't check");
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Continue_and_break_exit() =>
        Verify.Suppressions("""
            class C
            {
                int M(List<Pet> pets)
                {
                    var total = 0;
                    foreach (var pet in pets)
                    {
                        if (pet is Bird) continue;
                        if (pet is Dog) break;
                        total += pet /*suppressed*/switch { Cat => 1 };
                    }
                    return total;
                }
            }
            """
        );

    [Fact]
    public Task Else_if_chain() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    else if (pet is Dog) return 1;
                    else Console.WriteLine("a cat");
                    return pet /*suppressed*/switch { Cat => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Goto_past_the_switch_exits() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    var result = 0;
                    if (pet is Bird) goto Done;
                    result = pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                Done:
                    return result;
                }
            }
            """
        );

    [Fact]
    public Task Guard_and_switch_inside_a_lambda() =>
        Verify.Suppressions("""
            class C
            {
                Func<Pet, int> F() => pet =>
                {
                    if (pet is Bird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                };
            }
            """
        );

    [Fact]
    public Task Guard_and_switch_inside_a_local_function() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet outer)
                {
                    return Local(outer);

                    static int Local(Pet pet)
                    {
                        if (pet is Bird) return 0;
                        return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                }
            }
            """
        );

    [Fact]
    public Task Guard_before_try_switch_in_catch() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    try
                    {
                        return int.Parse("x");
                    }
                    catch (FormatException)
                    {
                        return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }
                }
            }
            """
        );

    [Fact]
    public Task Guard_helpers_that_do_not_return() =>
        Verify.Suppressions("""
            using System.Diagnostics.CodeAnalysis;

            static class Guard
            {
                public static void IsTrue([DoesNotReturnIf(false)] bool condition) { if (!condition) throw new Exception(); }
                public static void IsFalse([DoesNotReturnIf(true)] bool condition, string message) { if (condition) throw new Exception(message); }
            }

            class C
            {
                int M(Pet pet)
                {
                    Guard.IsTrue(pet is not Bird);
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet pet)
                {
                    Guard.IsFalse(pet is Bird, "no birds");
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // Debug.Assert is compiled out of release builds.
                int O(Pet pet)
                {
                    System.Diagnostics.Debug.Assert(pet is not Bird);
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_in_a_nested_block() =>
        Verify.Suppressions("""
            class C
            {
                readonly object _gate = new();

                int M(Pet pet)
                {
                    {
                        if (pet is Bird) return 0;
                    }
                    lock (_gate)
                    {
                        if (pet is Dog) return 1;
                    }
                    return pet /*suppressed*/switch { Cat => 2 };
                }

                int Using(Pet pet, IDisposable resource)
                {
                    using (resource)
                    {
                        if (pet is Bird) return 0;
                    }
                    checked
                    {
                        if (pet is Dog) return 1;
                    }
                    return pet /*suppressed*/switch { Cat => 2 };
                }

                unsafe int Fixed(Pet pet, int[] numbers)
                {
                    fixed (int* first = numbers)
                    {
                        if (pet is Bird) return 0;
                    }
                    unchecked
                    {
                        if (pet is Dog) return 1;
                    }
                    return pet /*suppressed*/switch { Cat => 2 };
                }

                int Unsafe(Pet pet)
                {
                    unsafe
                    {
                        if (pet is Bird) return 0;
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """, allowUnsafe: true
        );

    [Fact]
    public Task Guard_in_a_try_block() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    try
                    {
                        if (pet is Bird) return 0;
                    }
                    finally
                    {
                        Console.WriteLine("done");
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // The catch can run before the guard did, and falls through.
                int N(Pet pet)
                {
                    try
                    {
                        Console.WriteLine("checking");
                        if (pet is Bird) return 0;
                    }
                    catch (Exception)
                    {
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int O(Pet pet)
                {
                    try
                    {
                        Console.WriteLine("checking");
                        if (pet is Bird) return 0;
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_in_another_switch_section() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, int n)
                {
                    switch (n)
                    {
                        case 1:
                            if (pet is Bird) goto case 2;
                            return 1;
                        case 2:
                            return pet /*warns*/switch { Cat => 1, Dog => 2 };
                        default:
                            return 0;
                    }
                }
            }
            """
        );

    [Fact]
    public Task Guard_in_try_switch_in_finally() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    var result = 0;
                    try
                    {
                        if (pet is Bird) return 0;
                    }
                    finally
                    {
                        // Runs after the guard's return, when pet is a Bird.
                        result = pet /*warns*/switch { Cat => 1, Dog => 2 };
                    }
                    return result;
                }
            }
            """
        );

    [Fact]
    public Task Guard_that_calls_a_does_not_return_method() =>
        Verify.Suppressions("""
            static class Throw
            {
                [System.Diagnostics.CodeAnalysis.DoesNotReturn]
                public static void Bird() => throw new InvalidOperationException();

                [System.Diagnostics.CodeAnalysis.DoesNotReturn, System.Diagnostics.Conditional("DEBUG")]
                public static void BirdInDebug() => throw new InvalidOperationException();
            }

            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) Throw.Bird();
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet pet)
                {
                    if (pet is Bird)
                    {
                        Console.WriteLine("bird");
                        Throw.Bird();
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // Compiled out of release builds, so it doesn't always stop a Bird.
                int O(Pet pet)
                {
                    if (pet is Bird) Throw.BirdInDebug();
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_that_calls_a_method_that_always_throws() =>
        Verify.Suppressions("""
            class C
            {
                static void Fail() => throw new InvalidOperationException();

                static void FailLoudly(string message)
                {
                    Console.Error.WriteLine(message);
                    throw new InvalidOperationException(message);
                }

                static void MightFail(bool really)
                {
                    if (really) throw new InvalidOperationException();
                }

                static async System.Threading.Tasks.Task FailLater() => throw new InvalidOperationException();

                int M(Pet pet)
                {
                    if (pet is Bird) Fail();
                    if (pet is Dog) FailLoudly("dog");
                    return pet /*suppressed*/switch { Cat => 1 };
                }

                int N(Pet pet)
                {
                    if (pet is Bird) MightFail(true);
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // An async method returns a faulted task rather than throwing.
                int O(Pet pet)
                {
                    if (pet is Bird) _ = FailLater();
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_that_declares_the_variable() =>
        Verify.Suppressions("""
            class C
            {
                static bool TryGet(out Pet pet)
                {
                    pet = new Cat("Tom");
                    return true;
                }

                int M(object value)
                {
                    if (value is not Pet pet || pet is Bird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N()
                {
                    if (!TryGet(out var pet) || pet is Bird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );


    [Fact]
    public Task Condition_that_declares_the_variable_then_tests_it() =>
        Verify.Suppressions("""
            class C
            {
                static bool TryGet(out Pet pet)
                {
                    pet = new Cat("Tom");
                    return true;
                }

                static Pet Next() => new Cat("Tom");

                int OutVariable()
                {
                    if (TryGet(out var pet) && pet is not Bird) return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return 0;
                }

                int TryGetValue(Dictionary<string, Pet> pets)
                {
                    if (pets.TryGetValue("tom", out var pet) && pet is not Bird) return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return 0;
                }

                int DeclaredEarlier()
                {
                    Pet pet;
                    if (TryGet(out pet) && pet is not Bird) return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return 0;
                }

                int Pattern(object value)
                {
                    if (value is Pet pet && pet is not Bird) return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return 0;
                }

                int VarPattern() =>
                    Next() is var pet && pet is not Bird ? pet /*suppressed*/switch { Cat => 1, Dog => 2 } : 0;

                int SamePattern() =>
                    Next() is not Bird and var pet ? pet /*suppressed*/switch { Cat => 1, Dog => 2 } : 0;

                int Loop(Queue<Pet> queue)
                {
                    var total = 0;
                    while (queue.TryDequeue(out var pet) && pet is not Bird)
                    {
                        total += pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    }

                    return total;
                }

                int TestBeforeTheDeclaration(Pet other)
                {
                    if (other is not Bird && TryGet(out var pet)) return pet /*warns*/switch { Cat => 1, Dog => 2 };
                    return 0;
                }

                int FalseBranch()
                {
                    if (!TryGet(out var pet) || pet is Bird) { }
                    else return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return 0;
                }
            }
            """
        );

    [Fact]
    public Task Guard_that_does_not_exit() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) Console.WriteLine("bird");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_that_only_sometimes_exits() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, bool flag)
                {
                    if (pet is Bird)
                    {
                        if (flag) return 0;
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_with_an_else() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    else Console.WriteLine("not a bird");
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet pet)
                {
                    if (pet is not Bird) Console.WriteLine("not a bird");
                    else return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Iterator() =>
        Verify.Suppressions("""
            class C
            {
                IEnumerable<int> M(Pet pet)
                {
                    if (pet is Bird) yield break;
                    yield return 1;
                    yield return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Label_between_the_guard_and_the_switch() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    var total = 0;
                    if (pet is Bird) return 0;
                Again:
                    total += pet /*warns*/switch { Cat => 1, Dog => 2 };
                    if (total < 10)
                    {
                        /*because*/pet = new Bird("Polly");
                        goto Again;
                    }
                    return total;
                }
            }
            """
        );

    [Fact]
    public Task Label_only_jumped_back_to() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    var tries = 0;
                Again:
                    tries += pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    if (tries < 3) goto Again;
                    return tries;
                }

                // A goto from before the guard lands after it.
                int N(Pet pet, bool skip)
                {
                    var tries = 0;
                    if (skip) goto Later;
                    if (pet is Bird) return 0;
                Again:
                    tries++;
                Later:
                    tries += pet /*warns*/switch { Cat => 1, Dog => 2 };
                    if (tries < 3) goto Again;
                    return tries;
                }
            }
            """
        );

    [Fact]
    public Task Label_reached_by_gotos_from_earlier_on() =>
        Verify.Suppressions("""
            class C
            {
                // Both ways in come after the guard.
                int AfterTheGuard(Pet pet, bool skip)
                {
                    if (pet is Bird) return 0;
                    if (skip) goto Later;
                    Console.WriteLine("not skipped");
                Later:
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // The goto only runs for a Cat; falling in, it's not a Bird.
                int EachWayIn(Pet pet)
                {
                    if (pet is Cat) goto Known;
                    if (pet is Bird) return 0;
                Known:
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // The finally runs on the way out of the try, and writes pet.
                int ThroughAFinally(Pet pet, bool skip)
                {
                    if (pet is Bird) return 0;
                    try
                    {
                        if (skip) goto Later;
                    }
                    finally
                    {
                        pet = new Bird("Polly");
                    }
                    return 1;
                Later:
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );


    [Fact]
    public Task Label_reached_only_by_gotos_out_of_a_loop() =>
        Verify.Suppressions("""
            class C
            {
                int Found(Pet[] pets)
                {
                    Pet found = new Cat("Tom");
                    foreach (var pet in pets)
                    {
                        found = pet;
                        if (found is Bird) goto Done;
                    }

                    return 0;
                Done:
                    return found /*suppressed*/switch { Bird b => b.Name.Length };
                }

                int FallsInToo(Pet[] pets)
                {
                    Pet found = new Cat("Tom");
                    foreach (var pet in pets)
                    {
                        found = pet;
                        if (found is Bird) goto Done;
                    }

                Done:
                    return found /*warns*/switch { Bird b => b.Name.Length };
                }
            }
            """
        );

    [Fact]
    public Task Labeled_guard() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    var total = 0;
                Again:
                    if (pet is Bird) return total;
                    total += pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    if (total < 10) goto Again;
                    return total;
                }
            }
            """
        );


    [Fact]
    public Task Labeled_guard_that_writes_the_variable() =>
        Verify.Suppressions("""
            class C
            {
                static Pet Next() => new Cat("Tom");

                int Retry(Pet pet)
                {
                    var tries = 0;
                Retry:
                    if (pet is Bird)
                    {
                        if (++tries > 3) return 0;
                        pet = Next();
                        goto Retry;
                    }

                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Unused(Pet pet)
                {
                Unused:
                    if (pet is Bird)
                    {
                        pet = Next();
                        return 0;
                    }

                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int JumpedBackTo(Pet pet, bool again)
                {
                Top:
                    if (pet is Bird)
                    {
                        pet = Next();
                        return 0;
                    }

                    var legs = pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    pet = new Bird("Polly");
                    if (again) goto Top;
                    return legs;
                }

                int FallsThroughAfterWriting(Pet pet, Pet other)
                {
                Top:
                    if (pet is Bird)
                    {
                        pet = other;
                    }

                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Labeled_statement_containing_the_switch() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    var total = 0;
                    if (pet is Bird) goto Again;
                Again:
                    total += pet /*warns*/switch { Cat => 1, Dog => 2 };
                    return total;
                }
            }
            """
        );

    [Fact]
    public Task Statement_between_the_guard_and_its_block_end_writes() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    {
                        if (pet is Bird) return 0;
                        /*because*/pet = new Bird("Polly");
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Statements_that_throw_for_some_cases() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    var name = pet switch { Bird => throw new InvalidOperationException(), Cat c => c.Name, Dog d => d.Name };
                    Console.WriteLine(name);
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet pet)
                {
                    _ = pet is Bird ? throw new InvalidOperationException() : 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int O(Pet pet)
                {
                    Console.WriteLine(pet is Cat or Dog ? "ok" : throw new InvalidOperationException());
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // Only evaluated sometimes.
                int P(Pet pet, bool flag)
                {
                    _ = flag && (pet is Bird ? throw new InvalidOperationException() : true);
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // Compiled out of release builds, arguments and all.
                int Q(Pet pet)
                {
                    System.Diagnostics.Debug.WriteLine(pet is Bird ? throw new InvalidOperationException() : "ok");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Switch_in_a_bare_nested_block() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    {
                        if (pet is Dog) return 1;
                        {
                            return pet /*suppressed*/switch { Cat => 2 };
                        }
                    }
                }
            }
            """
        );

    [Fact]
    public Task Switch_nested_inside_later_statements() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, bool flag)
                {
                    if (pet is Bird) return 0;
                    if (flag)
                    {
                        using (new System.IO.MemoryStream())
                        {
                            return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                        }
                    }
                    return -1;
                }
            }
            """
        );

    [Fact]
    public Task Switch_statement_as_a_guard() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    switch (pet)
                    {
                        case Bird:
                            return 0;
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // Falling out of the Dog section (with break) means it was a Dog, so only Bird is ruled out on both ways.
                int N(Pet pet)
                {
                    switch (pet)
                    {
                        case Bird:
                            return 0;
                        case Dog:
                            Console.WriteLine("dog");
                            break;
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // A default section that breaks lets anything out.
                int O(Pet pet)
                {
                    switch (pet)
                    {
                        case Bird:
                            return 0;
                        default:
                            break;
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int P(Pet pet)
                {
                    switch (pet)
                    {
                        case Bird:
                            break;
                        default:
                            return 0;
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Throw_and_block_bodies_exit() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) throw new InvalidOperationException();
                    if (pet is Dog)
                    {
                        Console.WriteLine("dog");
                        return 0;
                    }
                    return pet /*suppressed*/switch { Cat => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Throw_helpers_in_statements_sure_to_throw() =>
        Verify.Suppressions("""
            using System.Diagnostics.CodeAnalysis;
            class C
            {
                [DoesNotReturn] static int Fail() => throw new InvalidOperationException();
                static T Unreachable<T>() => throw new InvalidOperationException();
                static int Sometimes(bool f) => f ? throw new InvalidOperationException() : 0;

                int Arm(Pet pet)
                {
                    var n = pet switch { Bird => Fail(), Cat => 1, Dog => 2 };
                    return n + pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Branch(Pet pet)
                {
                    _ = pet is Bird ? Unreachable<int>() : 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int NotAlways(Pet pet, bool f)
                {
                    var n = pet switch { Bird => Sometimes(f), Cat => 1, Dog => 2 };
                    return n + pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Top_level_statements() =>
        Verify.Suppressions("""
            Pet pet = new Cat("Tom");
            if (pet is Bird) return;
            Console.WriteLine(pet /*suppressed*/switch { Cat => 1, Dog => 2 });
            """, topLevelStatements: true
        );

    [Fact]
    public Task Top_level_statements_inside_a_block() =>
        Verify.Suppressions("""
            Pet pet = new Cat("Tom");
            if (pet is Bird) return;
            {
                Console.WriteLine(pet /*suppressed*/switch { Cat => 1, Dog => 2 });
            }
            """, topLevelStatements: true
        );
}
