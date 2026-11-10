namespace UnionNarrowing.Tests;

/// <summary>
/// Which tests rule a case out:
/// the conditions of <c>if (...) &lt;exit&gt;</c> guards, and what they do and don't say.
/// </summary>
public class GuardTests
{
    [Fact]
    public Task As_and_a_null_check() =>
        Verify.Suppressions("""
            class C
            {
                int Inline(Shape shape)
                {
                    if (shape as Circle != null) return 0;
                    return shape /*suppressed*/switch { Square => 1, Triangle => 2 };
                }

                int Pattern(Shape shape)
                {
                    if ((shape as Circle) is not null) return 0;
                    return shape /*suppressed*/switch { Square => 1, Triangle => 2 };
                }

                int InALocal(Shape shape)
                {
                    var circle = shape as Circle;
                    if (circle != null) return 0;
                    return shape /*suppressed*/switch { Square => 1, Triangle => 2 };
                }

                int OnAUnionsValue(Pet pet)
                {
                    if (pet.Value as Bird != null) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // `== null` rules out the others.
                int EqualsNull(Shape shape)
                {
                    if (shape as Circle == null) return 0;
                    return shape /*warns*/switch { Square => 1, Triangle => 2 };
                }

                int ChangedAfter(Shape shape, Shape other)
                {
                    var circle = shape as Circle;
                    /*because*/shape = other;
                    if (circle != null) return 0;
                    return shape /*warns*/switch { Square => 1, Triangle => 2 };
                }
            }
            """ + Snippets.Shapes
        );

    [Fact]
    public Task Bool_assigned_once_after_its_declaration() =>
        Verify.Suppressions("""
            class C
            {
                int Assigned(Pet pet)
                {
                    bool isBird;
                    isBird = pet is Bird;
                    if (isBird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int AssignedTwice(Pet pet, bool f)
                {
                    bool isBird;
                    if (f) isBird = pet is Bird;
                    else isBird = false;
                    if (isBird) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int ValueChangesAfter(Pet pet, Pet other)
                {
                    bool isBird;
                    isBird = pet is Bird;
                    /*because*/pet = other;
                    if (isBird) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Boolean_flags() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    var isBird = pet is Bird;
                    if (isBird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet pet)
                {
                    var isPet = pet is Cat or Dog;
                    if (!isPet) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // pet changes after the flag is set, so the flag says nothing about it now.
                int O(Pet pet, Func<Pet> next)
                {
                    var isBird = pet is Bird;
                    /*because*/pet = next();
                    if (isBird) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // The flag itself is reassigned.
                int P(Pet pet, bool other)
                {
                    var isBird = pet is Bird;
                    /*because*/isBird = other;
                    if (isBird) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Bools_compared_with_true_or_false() =>
        Verify.Suppressions("""
            class C
            {
                int EqualsFalse(Pet pet)
                {
                    var notBird = pet is not Bird;
                    if (notBird == false) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int NotEqualsFalse(Pet pet)
                {
                    if ((pet is Bird) != false) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int TheOtherWay(Pet pet)
                {
                    var notBird = pet is not Bird;
                    if (notBird == true) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );


    [Fact]
    public Task Bools_matched_with_true_or_false() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if ((pet is Bird) is true) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet pet)
                {
                    if ((pet is Bird or Dog) is not false) return 0;
                    return pet /*suppressed*/switch { Cat => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Case_split_over_a_bool() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, bool flag)
                {
                    if (pet is Bird && flag) return 0;
                    if (pet is Bird && !flag) return 1;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet pet, bool a, bool b)
                {
                    if (pet is Bird && a) return 0;
                    if (pet is Bird && !a && b) return 1;
                    if (pet is Bird && !b) return 2;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // flag could be anything when it's false.
                int O(Pet pet, bool flag)
                {
                    if (pet is Bird && flag) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // flag changes in between.
                int P(Pet pet, bool flag)
                {
                    if (pet is Bird && flag) return 0;
                    flag = !flag;
                    if (pet is Bird && flag) return 1;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Conditional_and_does_not_rule_anything_out() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, bool flag)
                {
                    if (pet is Bird && flag) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Conditional_operator_as_a_condition() =>
        Verify.Suppressions("""
            class C
            {
                int BothBranches(Pet pet, bool f)
                {
                    if (f ? pet is Bird : pet is Bird or Dog) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int ByTheCondition(Pet pet)
                {
                    if (pet is Bird ? true : false) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int OneBranch(Pet pet, bool f)
                {
                    if (f ? pet is Bird : pet is Dog) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_on_a_different_variable() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet, Pet other)
                {
                    if (other is Bird) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_on_a_subtype_of_the_case_type_does_not_cover_it() =>
        Verify.Suppressions("""
            class Animal { }
            sealed class Parrot : Animal { }
            union Zoo(Animal, Cat);

            class C
            {
                int M(Zoo zoo)
                {
                    if (zoo is Parrot) return 0;
                    return zoo /*warns*/switch { Cat => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_on_the_union_Value() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet.Value is Bird) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet pet)
                {
                    if (pet.Value is not (Cat or Dog)) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // On the union, `{ }` is always true; on its Value it rules out null.
                int O(MaybeCat cat)
                {
                    if (cat.Value is not { } || cat is Dog) return 0;
                    return cat /*suppressed*/switch { Cat => 1 };
                }
            }

            union MaybeCat(Cat?, Dog);
            """
        );

    [Fact]
    public Task Guard_that_excludes_a_type_the_arms_already_cover() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Cat) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_with_a_property_subpattern_only_covers_some_values() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird { Name: "Polly" }) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Logical_operators_that_evaluate_both_sides() =>
        Verify.Suppressions("""
            class C
            {
                int Or(Pet pet)
                {
                    if (pet is Bird | pet is Dog) return 0;
                    return pet /*suppressed*/switch { Cat => 1 };
                }

                int And(Pet pet)
                {
                    if (pet is not Bird & pet is not Dog) return pet /*suppressed*/switch { Cat => 1 };
                    return 0;
                }

                int WithAFlag(Pet pet, bool flag)
                {
                    if (pet is Bird & flag) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Negated_guard_on_a_generic_case_type() =>
        Verify.Suppressions("""
            record None;
            union Option<T>(T, None) where T : notnull;

            class C
            {
                // T could itself be None, so `not None` doesn't rule T out.
                int M<T>(Option<T> option) where T : notnull
                {
                    if (option is not None) return 0;
                    return option /*warns*/switch { None => 1 };
                }
            }
            """
        );


    [Fact]
    public Task Guard_in_generic_code_on_a_constructed_case_type() =>
        Verify.Suppressions("""
            sealed record Some<T>(T Value);
            sealed record None;
            union Option<T>(Some<T>, None);

            class C
            {
                // With T = int, a Some<int> gets past the guard.
                int M<T>(Option<T> option)
                {
                    if (option is not Some<int>) return -1;
                    return option /*warns*/switch { None => 0 };
                }

                int Matching<T>(Option<T> option)
                {
                    if (option is not Some<T>) return -1;
                    return option /*suppressed*/switch { Some<T> => 0 };
                }
            }
            """
        );

    [Fact]
    public Task Negated_guard_on_a_type_an_unsealed_case_type_could_derive_into() =>
        Verify.Suppressions("""
            class Rock { }
            sealed class SealedRock { }
            union Thing(Rock, Bird);
            union SealedThing(SealedRock, Bird);

            class C
            {
                // A class deriving from Rock could implement IFlier, so getting past the guard doesn't rule Rock out.
                int M(Thing thing)
                {
                    if (thing is not IFlier) return 0;
                    return thing /*warns*/switch { Bird => 1 };
                }

                // A SealedRock can never be an IFlier.
                int N(SealedThing thing)
                {
                    if (thing is not IFlier) return 0;
                    return thing /*suppressed*/switch { Bird => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Negated_guard_on_unrelated_and_value_types() =>
        Verify.Suppressions("""
            class Animal { }
            union Zoo(Animal, Cat);
            union Number(int, string);

            class C
            {
                // Cat doesn't derive from Animal (or the other way round), so no Animal is ever a Cat.
                int M(Zoo zoo)
                {
                    if (zoo is not Cat) return 0;
                    return zoo /*suppressed*/switch { Cat => 1 };
                }

                int N(Number n)
                {
                    if (n is not string) return 0;
                    return n /*suppressed*/switch { string s => s.Length };
                }
            }
            """
        );

    [Fact]
    public Task Null_conditional_access_in_a_test() =>
        Verify.Suppressions("""
            record Owner(Pet Pet);
            record Household(Owner? Owner);

            class C
            {
                int M(Owner? owner)
                {
                    if (owner?.Pet is Bird) return 0;
                    if (owner is null) return -1;
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Owner owner)
                {
                    if (owner?.Pet is not (Cat or Dog)) return 0;
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int O(Household household)
                {
                    if (household.Owner?.Pet is Bird) return 0;
                    return household.Owner!.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int P(Household? household)
                {
                    if (household?.Owner?.Pet is Bird) return 0;
                    return household!.Owner!.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // The chain itself can also be null...
                int Q(Owner? owner)
                {
                    if (owner?.Pet is Bird) return 0;
                    return (owner?.Pet) /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // ...so it needs a null arm.
                int WithANullArm(Owner? owner)
                {
                    if (owner?.Pet is Bird) return 0;
                    return owner?.Pet /*suppressed*/switch { Cat => 1, Dog => 2, null => 3 };
                }
            }
            """
        );

    [Fact]
    public Task Parenthesized_conditions() =>
        Verify.Suppressions("""
            class C
            {
                int Single(Pet pet) => (pet is Bird) || pet /*suppressed*/switch { Cat => true, Dog => false } ? 1 : 0;

                bool Tuple(Pet a, Pet b) =>
                    (a is Bird && b is Bird) || (a, b) /*suppressed*/switch { (Cat, _) => true, (Dog, _) => false, (Bird, Cat) => true, (Bird, Dog) => false };
            }
            """
        );

    [Fact]
    public Task Property_and_positional_patterns_on_what_the_value_is_a_member_of() =>
        Verify.Suppressions("""
            record Owner(Pet Pet, Pet Other);
            record Household(Owner Owner);

            class C
            {
                int Property(Owner owner)
                {
                    if (owner is { Pet: Bird }) return 0;
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Negated(Owner owner)
                {
                    if (owner is not { Pet: Cat or Dog }) return 0;
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int EitherMember(Owner owner)
                {
                    if (owner is { Pet: Bird } or { Other: Bird }) return 0;
                    return owner.Other /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Positional(Owner owner)
                {
                    if (owner is (Bird, _)) return 0;
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Nested(Household household)
                {
                    if (household is { Owner: { Pet: Bird } } || household is { Owner.Other: Bird }) return 0;
                    return household.Owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 }
                        + household.Owner.Other /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // o is owner.
                int Branch(Owner owner) => owner is { Pet: not Bird } o ? o.Pet /*suppressed*/switch { Cat => 1, Dog => 2 } : 0;

                int SameVariable(Owner owner) => owner is { Pet: not Bird } ? owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 } : 0;

                int Tuple((Pet Pet, int N) t)
                {
                    if (t is (Bird, _) or { Pet: Dog }) return 0;
                    return t.Pet /*suppressed*/switch { Cat => 1 };
                }

                // Only a Bird whose Other is a Cat is ruled out.
                int Partly(Owner owner)
                {
                    if (owner is { Pet: Bird, Other: Cat }) return 0;
                    return owner.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // ...but a Pet that isn't a Cat or Dog doesn't get past `is not { Pet: Cat or Dog, Other: _ }`.
                int RestMatchesAnything(Owner owner)
                {
                    if (owner is not { Pet: Cat or Dog, Other: _ }) return 0;
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Property_pattern_on_the_union_Value() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is { Value: Bird }) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet pet)
                {
                    if (pet is not { Value: Cat or Dog }) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int O(Pet? maybe)
                {
                    if (maybe is null || maybe is { Value: Bird }) return 0;
                    return maybe /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // `{ }` on a Pet? also needs a value, so `{ Value: _ }` doesn't hold for null.
                int P(Pet? maybe)
                {
                    if (maybe is not { Value: _ }) return 0;
                    return maybe /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );


    [Fact]
    public Task Patterns_that_check_more_than_they_seem_to() =>
        Verify.Suppressions("""
            union TupleOrText(Tuple<int, int>, string);

            sealed record Fish
            {
                public bool Equals(Fish? other) => true;
                public override int GetHashCode() => 0;
            }

            union Pond(Fish, Dog);

            class C
            {
                // `()` on an object also checks that it's an ITuple of length 0.
                int EmptyPositional(TupleOrText t)
                {
                    if (t.Value is ()) return 0;
                    return t /*warns*/switch { string => 1 };
                }

                // On a Pet?, `{ Value: ... }` is the union's Value, inside a Pet? that has one.
                int ValueOfANullableUnion(Pet? maybe)
                {
                    if (maybe is { Value: null } or { Value: Bird }) return 0;
                    return maybe /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // A record's `==` calls its Equals, which can be one of its own.
                int RecordWithItsOwnEquals(Pond pond)
                {
                    if (pond.Value as Fish != null) return 0;
                    return pond /*warns*/switch { Dog => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Several_guards_add_up() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet is Bird) return 0;
                    Console.WriteLine("not a bird");
                    if (pet is Dog) return 1;
                    return pet /*suppressed*/switch { Cat => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Switch_expression_as_a_condition() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet pet)
                {
                    if (pet switch { Bird => true, _ => false }) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet pet, bool flag)
                {
                    if (pet switch { Bird => true, Dog => flag, _ => false }) return 0;
                    return pet /*warns*/switch { Cat => 1 };
                }
            }
            """
        );

    /// <summary>
    /// The plain rows of the README's "Tests that rule a case out" table:
    /// past <c>if (test) return 0;</c>, the switch only needs arms for what's left.
    /// The rows with a catch have tests of their own below.
    /// </summary>
    [Theory]
    [InlineData("pet is Bird", "Cat => 1, Dog => 2")]
    [InlineData("pet is Bird b", "Cat => 1, Dog => 2")]
    [InlineData("pet is Bird { }", "Cat => 1, Dog => 2")]
    [InlineData("pet is Bird or Dog", "Cat => 1")]
    [InlineData("pet is Bird || pet is Dog", "Cat => 1")]
    [InlineData("pet is not Cat", "Cat => 1")]
    [InlineData("!(pet is Cat)", "Cat => 1")]
    [InlineData("pet is not (Cat or Dog)", "Cat => 1, Dog => 2")]
    [InlineData("pet is IFlier", "Cat => 1, Dog => 2")]
    [InlineData("pet is Bird && pet is IFlier", "Cat => 1, Dog => 2")]
    [InlineData("pet is IFlier and Bird", "Cat => 1, Dog => 2")]
    [InlineData("flag || pet is Bird", "Cat => 1, Dog => 2")]
    public Task Test_that_rules_cases_out(string test, string arms) =>
        Verify.Suppressions($$"""
            class C
            {
                int M(Pet pet, bool flag)
                {
                    if ({{test}}) return 0;
                    return pet /*suppressed*/switch { {{arms}} };
                }
            }
            """
        );
}
