namespace UnionNarrowing.Tests;

/// <summary>
/// What a value was last assigned says which cases it can be in:
/// a union made from a Cat is a Cat.
/// </summary>
public class AssignmentTests
{
    [Fact]
    public Task A_branch_that_throws_gives_no_value() =>
        Verify.Suppressions("""
            using System.Diagnostics.CodeAnalysis;
            class C
            {
                [DoesNotReturn] static Pet Fail() => throw new InvalidOperationException();
                static T Unreachable<T>() => throw new InvalidOperationException();
                static Pet Sometimes(bool f) => f ? throw new InvalidOperationException() : new Bird("Polly");

                int Conditional(Pet a, bool f)
                {
                    if (a is Bird) return 0;
                    Pet pet = f ? a : throw new InvalidOperationException();
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Helper(Pet a, bool f)
                {
                    if (a is Bird) return 0;
                    Pet pet = f ? Fail() : a;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int GenericHelper(Pet a, bool f)
                {
                    if (a is Bird) return 0;
                    Pet pet = f ? a : Unreachable<Pet>();
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int AsTheInput(Pet a, bool f)
                {
                    if (a is Bird) return 0;
                    return (f ? a : throw new InvalidOperationException()) /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // A method that only sometimes throws gives a value the rest of the time.
                int OnlySometimes(Pet a, bool f)
                {
                    if (a is Bird) return 0;
                    Pet pet = f ? a : Sometimes(f);
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Assigned_from_a_case() =>
        Verify.Suppressions("""
            class C
            {
                int Declaration()
                {
                    Pet pet = new Cat("Tom");
                    return pet /*suppressed*/switch { Cat => 1 };
                }

                int Assignment(Pet pet, Cat cat)
                {
                    pet = cat;
                    return pet /*suppressed*/switch { Cat => 1 };
                }

                int Either(bool flag)
                {
                    Pet pet = flag ? new Cat("Tom") : new Dog("Rex");
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Constructed(Cat cat)
                {
                    var pet = new Pet(cat);
                    return pet /*suppressed*/switch { Cat => 1 };
                }

                // A Cat? could be null, which the switch doesn't handle.
                int MaybeNull(Cat? cat)
                {
                    Pet pet = cat!;
                    return pet /*suppressed*/switch { Cat => 1 };
                }

                int Interface(IFlier flier)
                {
                    Pet pet = (Bird)flier;
                    return pet /*suppressed*/switch { Bird => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Assigned_in_a_switch_statement() =>
        Verify.Suppressions("""
            class C
            {
                int EverySection(int kind)
                {
                    Pet pet;
                    switch (kind)
                    {
                        case 0: pet = new Cat("Tom"); break;
                        case 1: pet = new Dog("Rex"); break;
                        default: return 0;
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // Without a default, it can come out as it went in.
                int NoDefault(int kind)
                {
                    Pet pet = new Cat("Tom");
                    switch (kind)
                    {
                        case 0: pet = new Dog("Rex"); break;
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int NoDefaultFromABird(int kind)
                {
                    Pet pet = new Bird("Polly");
                    switch (kind)
                    {
                        case 0: pet = new Dog("Rex"); break;
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int GuardInASection(int kind, Func<Pet> next)
                {
                    Pet pet;
                    switch (kind)
                    {
                        case 0:
                            pet = next();
                            if (pet is Bird) return 0;
                            break;
                        default: return 0;
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // A section that breaks without assigning it.
                int SectionLeavesIt(int kind, Pet start)
                {
                    Pet pet = start;
                    switch (kind)
                    {
                        case 0: pet = new Cat("Tom"); break;
                        case 1: break;
                        default: return 0;
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // A section that breaks out before it assigns.
                int BreaksEarly(int kind, bool early, Pet start)
                {
                    Pet pet = start;
                    switch (kind)
                    {
                        case 0:
                            if (early) break;
                            pet = new Cat("Tom");
                            break;
                        default: return 0;
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Assigned_in_a_try() =>
        Verify.Suppressions("""
            class C
            {
                int GuardInTheTry(Func<Pet> load)
                {
                    Pet pet;
                    try
                    {
                        pet = load();
                        if (pet is Bird) return 0;
                    }
                    catch (InvalidOperationException)
                    {
                        return -1;
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int CatchAssignsToo(Func<Pet> load)
                {
                    Pet pet;
                    try
                    {
                        pet = load();
                        if (pet is Bird) return 0;
                    }
                    catch (InvalidOperationException)
                    {
                        pet = new Cat("Tom");
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int FinallyDoesSomethingElse(Func<Pet> load, Action log)
                {
                    Pet pet;
                    try
                    {
                        pet = load();
                        if (pet is Bird) return 0;
                    }
                    finally
                    {
                        log();
                    }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // A catch that falls through could start before the guard.
                int CatchFallsThrough(Func<Pet> load)
                {
                    Pet pet = new Bird("Polly");
                    try
                    {
                        pet = load();
                        if (pet is Bird) return 0;
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // A finally that assigns it has the last word.
                int FinallyAssigns(Func<Pet> load)
                {
                    Pet pet;
                    try
                    {
                        pet = load();
                        if (pet is Bird) return 0;
                    }
                    finally
                    {
                        pet = load();
                    }
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Assigned_in_some_branches() =>
        Verify.Suppressions("""
            class C
            {
                // A Bird is replaced with a Cat.
                int Normalized(Pet pet)
                {
                    if (pet is Bird) pet = new Cat("Tom");
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int InBothBranches(Pet pet, bool flag)
                {
                    if (flag) { pet = new Cat("Tom"); }
                    else { Console.WriteLine(); pet = new Dog("Rex"); }
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int WithAGuardBefore(Pet pet, bool flag)
                {
                    if (pet is Bird) return 0;
                    if (flag) pet = new Dog("Rex");
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Conditional(Pet pet)
                {
                    pet = pet is Bird ? new Cat("Tom") : pet;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // A Bird could be assigned.
                int NotNormalized(Pet pet, Pet other)
                {
                    if (pet is Bird) pet = other;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // The other branch keeps whatever pet was.
                int OnlyOneBranch(Pet pet, bool flag)
                {
                    if (flag) pet = new Cat("Tom");
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Assigned_in_the_condition_that_tests_it() =>
        Verify.Suppressions("""
            class C
            {
                int If(Func<Pet> next)
                {
                    Pet pet;
                    if ((pet = next()) is Bird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int InTheBranch(Func<Pet> next)
                {
                    Pet pet;
                    if ((pet = next()) is not Bird)
                        return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                    return 0;
                }

                int RightOfAnd(Func<Pet> next)
                {
                    Pet pet;
                    return (pet = next()) is not Bird && pet /*suppressed*/switch { Cat => true, Dog => false } ? 1 : 0;
                }

                // A test before the assignment is of what it held before.
                int TestedBefore(Pet pet, Func<Pet> next)
                {
                    if (pet is Bird || (pet = next()) is Dog) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int AssignedTwice(Func<Pet> next)
                {
                    Pet pet;
                    if ((pet = next()) is Bird || (pet = next()) is Dog) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int AssignedAgainAfter(Func<Pet> next)
                {
                    Pet pet;
                    if ((pet = next()) is Bird) return 0;
                    /*because*/pet = next();
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Assigned_with_coalescing() =>
        Verify.Suppressions("""
            class C
            {
                int KeepsWhatWasKnown(Shape? shape)
                {
                    if (shape is Square) return 0;
                    shape ??= new Circle(1);
                    return shape /*suppressed*/switch { Circle => 1, Triangle => 2 };
                }

                int NullableUnion(Pet? maybe)
                {
                    if (maybe is Bird) return 0;
                    maybe ??= new Cat("Tom");
                    return maybe /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int FallbackIsWhatWasRuledOut(Shape? shape)
                {
                    if (shape is Square) return 0;
                    shape ??= new Square(1);
                    return shape /*warns*/switch { Circle => 1, Triangle => 2 };
                }
            }
            """ + Snippets.Shapes
        );

    [Fact]
    public Task Chains_of_what_was_assigned() =>
        Verify.Suppressions("""
            class C
            {
                static Pet Forever() => Forever();

                // A method that only returns a call to itself doesn't loop the analysis.
                int Recursive() => Forever() /*warns*/switch { Cat => 1 };

                // A filtered element, then a switch that replaces Dogs.
                string M(IEnumerable<Pet> pets)
                {
                    var pet = pets.First(p => p is not Bird);
                    pet = pet switch { Dog => new Cat("Tom"), var other => other };
                    return pet /*suppressed*/switch { Cat c => c.Name };
                }
            }
            """
        );

    [Fact]
    public Task Closed_hierarchy_assigned_from_a_derived_type() =>
        Verify.Suppressions("""
            public closed record Shape;
            public record Circle : Shape;
            public record Square : Shape;
            public record Triangle : Shape;

            class C
            {
                int M(bool flag)
                {
                    Shape shape = flag ? new Circle() : new Square();
                    return shape /*suppressed*/switch { Circle => 1, Square => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Coalesced_with_a_throw() =>
        Verify.Suppressions("""
            class C
            {
                int Followed(Shape? found)
                {
                    if (found is Circle) return 0;
                    var shape = found ?? throw new ArgumentNullException(nameof(found));
                    return shape /*suppressed*/switch { Square => 1, Triangle => 2 };
                }

                int FirstOrDefault(List<Shape> shapes)
                {
                    var shape = shapes.FirstOrDefault(s => s is not Circle) ?? throw new InvalidOperationException();
                    return shape /*suppressed*/switch { Square => 1, Triangle => 2 };
                }

                int AsTheInput(Pet? maybe)
                {
                    if (maybe is Bird) return 0;
                    return (maybe ?? throw new InvalidOperationException()) /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // A fallback is a value too.
                int Fallback(Shape? found, Shape other)
                {
                    if (found is Circle) return 0;
                    var shape = found ?? other;
                    return shape /*warns*/switch { Square => 1, Triangle => 2 };
                }
            }
            """ + Snippets.Shapes
        );

    [Fact]
    public Task Members_that_only_ever_hold_their_initializer() =>
        Verify.Suppressions("""
            static class Defaults
            {
                public static readonly Pet Favourite = new Cat("Tom");
                public static Pet Current { get; } = new Dog("Rex");
            }

            class Holder
            {
                private readonly Pet _pet = new Cat("Tom");
                private readonly Pet _reset = new Cat("Tom");

                private readonly Pet _byOut = new Cat("Tom");
                private readonly Pet _byThis = new Cat("Tom");

                static void Set(out Pet pet) => pet = new Bird("Polly");

                public Holder(bool flag)
                {
                    if (flag) _reset = new Bird("Polly");
                    Set(out this._byOut);
                    (this._byThis, var n) = (new Bird("Polly"), 1);
                }

                int M() => _pet /*suppressed*/switch { Cat => 1 };

                // A constructor can assign it, directly or otherwise.
                int N() => _reset /*warns*/switch { Cat => 1 };
                int O() => _byOut /*warns*/switch { Cat => 1 };
                int P() => _byThis /*warns*/switch { Cat => 1 };
            }

            class C
            {
                int M() => Defaults.Favourite /*suppressed*/switch { Cat => 1 };

                int N() => Defaults.Current /*suppressed*/switch { Dog => 1 };
            }
            """
        );

    [Fact]
    public Task New_objects_and_what_they_were_given() =>
        Verify.Suppressions("""
            record Owner(Pet Pet);
            class Holder { public required Pet Pet { get; init; } }

            class C
            {
                int Record(Cat cat) => new Owner(cat).Pet /*suppressed*/switch { Cat => 1 };

                int Local(Cat cat)
                {
                    var owner = new Owner(cat);
                    return owner.Pet /*suppressed*/switch { Cat => 1 };
                }

                int Initializer(Cat cat)
                {
                    var holder = new Holder { Pet = cat };
                    return holder.Pet /*suppressed*/switch { Cat => 1 };
                }

                int With(Owner owner, Cat cat)
                {
                    var other = owner with { Pet = cat };
                    return other.Pet /*suppressed*/switch { Cat => 1 };
                }

                int WithSomethingElse(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    var other = owner with { };
                    return other.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Member(Pet pet)
                {
                    var t = (pet, 1);
                    t.Item1 = new Cat("Tom");
                    return t.Item1 /*suppressed*/switch { Cat => 1 };
                }
            }
            """
        );


    [Fact]
    public Task New_objects_whose_initializers_or_copies_run_code() =>
        Verify.Suppressions("""
            sealed class Settings
            {
                public Pet State { get; init; }
                public int Level { get => 0; init => State = new Bird("Polly"); }
                public int Plain { get; init; }
            }

            record Snapshot(Pet State, int Version)
            {
                protected Snapshot(Snapshot original)
                {
                    State = new Bird("Polly");
                    Version = original.Version;
                }
            }

            record Plain(Pet State, int Version)
            {
                public int Level { get => 0; init => State = new Bird("Polly"); }
            }

            class C
            {
                int LaterInitializerSetsIt()
                {
                    var settings = new Settings { State = new Cat("Tom"), Level = 3 };
                    return settings.State /*warns*/switch { Cat => 1 };
                }

                int LaterInitializerOfAnAutoProperty()
                {
                    var settings = new Settings { State = new Cat("Tom"), Plain = 3 };
                    return settings.State /*suppressed*/switch { Cat => 1 };
                }

                int CopyConstructorOfYourOwn(Snapshot snapshot)
                {
                    if (snapshot.State is Bird) return 0;
                    var next = snapshot with { Version = 2 };
                    return next.State /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int WithRunsAnInitAccessor(Plain plain)
                {
                    if (plain.State is Bird) return 0;
                    var next = plain with { Level = 2 };
                    return next.State /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int GeneratedCopy(Plain plain)
                {
                    if (plain.State is Bird) return 0;
                    var next = plain with { Version = 2 };
                    return next.State /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Out_values_a_helper_always_sets() =>
        Verify.Suppressions("""
            class C
            {
                static bool TryCat(out Pet pet)
                {
                    pet = new Cat("Tom");
                    return true;
                }

                static bool CatOrDog(bool flag, out Pet pet)
                {
                    if (flag) { pet = new Cat("Tom"); return true; }
                    pet = new Dog("Rex");
                    return false;
                }

                static void Anything(Pet from, out Pet pet) => pet = from;

                int M()
                {
                    TryCat(out var pet);
                    return pet /*suppressed*/switch { Cat => 1 };
                }

                int N(bool flag)
                {
                    if (!CatOrDog(flag, out var pet)) Console.WriteLine("a dog");
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int O(Pet from)
                {
                    Anything(from, out var pet);
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Switch_expression_as_the_value_assigned() =>
        Verify.Suppressions("""
            class C
            {
                int Normalized(Pet pet)
                {
                    pet = pet switch { Bird => new Cat("Tom"), var other => other };
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Throwing(Pet pet)
                {
                    var normal = pet switch { Bird => throw new InvalidOperationException(), _ => pet };
                    return normal /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // The other arm can give a Bird.
                int Either(Pet pet, Pet other)
                {
                    var chosen = pet switch { Cat => other, _ => pet };
                    return chosen /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task What_a_method_returns() =>
        Verify.Suppressions("""
            class C
            {
                static Pet MakeCat() => new Cat("Tom");

                static Pet CatOrDog(bool flag)
                {
                    if (flag) return new Cat("Tom");
                    return new Dog("Rex");
                }

                static Pet Any(Pet pet) => pet;

                virtual protected Pet Overridable() => new Cat("Tom");

                int M() => MakeCat() /*suppressed*/switch { Cat => 1 };

                int N(bool flag) => CatOrDog(flag) /*suppressed*/switch { Cat => 1, Dog => 2 };

                int O(Pet pet) => Any(pet) /*warns*/switch { Cat => 1, Dog => 2 };

                int P() => Overridable() /*warns*/switch { Cat => 1 };

                int Q()
                {
                    var pet = MakeCat();
                    return pet /*suppressed*/switch { Cat => 1 };
                }
            }
            """
        );


    [Fact]
    public Task What_a_generic_method_returns() =>
        Verify.Suppressions("""
            using System;
            using System.Threading.Tasks;
            sealed record Some<T>(T Value);
            sealed record None;
            union Option<T>(Some<T>, None);
            sealed record Ok<T>(T Value);
            sealed record Err<E>(E Error);
            union Result<T, E>(Ok<T>, Err<E>);

            static class Opt
            {
                public static Option<T> Some<T>(T value) => new Some<T>(value);
                public static Option<T> Nothing<T>() => new None();
                public static Result<U, E> Map<T, U, E>(this Result<T, E> r, Func<T, U> f) =>
                    r switch { Ok<T> ok => new Ok<U>(f(ok.Value)), Err<E> err => err };
                public static async Task<Option<T>> SomeLater<T>(T value) { await Task.Yield(); return new Some<T>(value); }
                public static void Out<T>(T value, out Option<T> option) => option = new Some<T>(value);
            }

            static class Factory<T>
            {
                public static Option<T> Make(T value) => new Some<T>(value);
                public static readonly Option<T> Empty = new None();
            }

            class C
            {
                int ReturnsTheCaseItWasGiven() => Opt.Some(1) /*suppressed*/switch { Some<int> s => s.Value };

                int ReturnsAnotherCase() => Opt.Some(1) /*warns*/switch { None => 0 };

                int ReturnsNone() => Opt.Nothing<int>() /*warns*/switch { Some<int> => 0 };

                int MapGivesEitherCase(Result<int, string> r) => r.Map(x => x + 1) /*warns*/switch { Err<string> => 0 };

                int OfAGenericClass() => Factory<int>.Make(1) /*warns*/switch { None => 0 };

                int InitializerOfAGenericClass() => Factory<int>.Empty /*warns*/switch { Some<int> => 0 };

                async Task<int> Awaited() => await Opt.SomeLater(1) /*warns*/switch { None => 0 };

                int OutArgument()
                {
                    Opt.Out(1, out var option);
                    return option /*warns*/switch { None => 0 };
                }
            }
            """
        );

    [Fact]
    public Task What_an_async_method_returns() =>
        Verify.Suppressions("""
            using System.Threading.Tasks;
            class C
            {
                static async Task<Pet> MakeCat() { await Task.Yield(); return new Cat("Tom"); }
                static async Task<Pet> MakeEither(bool f) { await Task.Yield(); return f ? new Cat("Tom") : new Bird("Polly"); }
                static Task<Pet> NotAsync() => Task.FromResult<Pet>(new Cat("Tom"));

                async Task<int> Awaited()
                {
                    var pet = await MakeCat();
                    var configured = await MakeCat().ConfigureAwait(false);
                    return pet /*suppressed*/switch { Cat => 1 } + configured /*suppressed*/switch { Cat => 1 };
                }

                async Task<int> Either(bool f)
                {
                    var pet = await MakeEither(f);
                    return pet /*warns*/switch { Cat => 1 };
                }

                // What a task holds isn't followed.
                async Task<int> FromATask()
                {
                    var pet = await NotAsync();
                    return pet /*warns*/switch { Cat => 1 };
                }
            }
            """
        );
}
