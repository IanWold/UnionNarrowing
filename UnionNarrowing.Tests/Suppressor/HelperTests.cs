namespace UnionNarrowing.Tests;

/// <summary>
/// Tests written as helpers:
/// a call to a method or property whose body is a single test of its argument reads as that test.
/// </summary>
public class HelperTests
{
    [Fact]
    public Task Generic_helpers() =>
        Verify.Suppressions("""
            static class Ext
            {
                public static bool Is<T>(this Pet p) => p.Value is T;
                public static bool IsEither<T, U>(this Pet p) => p.Is<T>() || p.Is<U>();
            }
            class C
            {
                int One(Pet pet)
                {
                    if (pet.Is<Bird>()) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Nested(Pet pet)
                {
                    if (pet.IsEither<Bird, Dog>()) return 0;
                    return pet /*suppressed*/switch { Cat => 1 };
                }

                int Interface(Pet pet)
                {
                    if (pet.Is<IFlier>()) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int SomethingElse(Pet pet)
                {
                    if (pet.Is<string>()) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );


    [Fact]
    public Task Generic_helpers_over_a_generic_union() =>
        Verify.Suppressions("""
            sealed record NotAsked;
            sealed record Loading;
            sealed record Success<T>(T Data);
            sealed record Failure(string Error);
            union RemoteData<T>(NotAsked, Loading, Success<T>, Failure)
            {
                public bool IsPending => this is NotAsked or Loading;
            }

            sealed record Ok<T>(T Value);
            sealed record Err<E>(E Error);
            union Result<T, E>(Ok<T>, Err<E>);

            static class Ext
            {
                public static bool IsSettled<T>(this RemoteData<T> d) => d is Success<T> or Failure;
                public static bool IsErr<T, E>(this Result<T, E> r) => r is Err<E>;
                public static bool IsOkOf<T>(Result<T, string> r) => r is Ok<T>;
            }

            class C
            {
                int Extension(RemoteData<int> d)
                {
                    if (!d.IsSettled()) return 0;
                    return d /*suppressed*/switch { Success<int> s => s.Data, Failure => -1 };
                }

                int InGenericCode<T>(RemoteData<T> d)
                {
                    if (!d.IsSettled()) return 0;
                    return d /*suppressed*/switch { Success<T> => 1, Failure => -1 };
                }

                int UnionMember(RemoteData<int> d)
                {
                    if (d.IsPending) return 0;
                    return d /*suppressed*/switch { Success<int> s => s.Data, Failure => -1 };
                }

                int TypeArgumentInACaseType(Result<int, string> r)
                {
                    if (r.IsErr()) return 0;
                    return r /*suppressed*/switch { Ok<int> ok => ok.Value };
                }

                int Static(Result<int, string> r)
                {
                    if (Ext.IsOkOf(r)) return 0;
                    return r /*suppressed*/switch { Err<string> => 1 };
                }

                int RulesOutTheWrongCase(Result<int, string> r)
                {
                    if (r.IsErr()) return 0;
                    return r /*warns*/switch { Err<string> => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Helpers_that_are_not_read() =>
        Verify.Suppressions("""
            abstract class Checker
            {
                public virtual bool IsBird(Pet pet) => pet is Bird;
                public bool Logs(Pet pet) { for (var i = 0; i < 2; i++) Console.WriteLine(pet); return pet is Bird; }
            }

            static class Helpers
            {
                public static bool Sometimes(Pet pet, bool flag) => pet is Bird && flag;
                public static bool Reassigns(Pet pet) => (pet = new Cat("Tom")) is Bird;
            }

            class C
            {
                int Virtual(Checker checker, Pet pet)
                {
                    if (checker.IsBird(pet)) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int NotOneExpression(Checker checker, Pet pet)
                {
                    if (checker.Logs(pet)) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int OnlySometimes(Pet pet, bool flag)
                {
                    if (Helpers.Sometimes(pet, flag)) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int WritesItsParameter(Pet pet)
                {
                    if (Helpers.Reassigns(pet)) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // The helper's own parameter is another argument.
                int OtherArgument(Pet pet, Pet other)
                {
                    if (Helpers.Sometimes(other, true)) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Members_of_the_union_itself() =>
        Verify.Suppressions("""
            union Animal(Cat, Dog, Bird)
            {
                public readonly bool IsBird => this is Bird;
                public readonly bool IsPet() => Value is Cat or Dog;
            }

            class C
            {
                int M(Animal animal)
                {
                    if (animal.IsBird) return 0;
                    return animal /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Animal animal)
                {
                    if (!animal.IsPet()) return 0;
                    return animal /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Members_of_the_union_that_are_not_marked_readonly() =>
        Verify.Suppressions("""
            union Animal(Cat, Dog, Bird)
            {
                public bool IsBird => this is Bird;
                public bool IsPet() => Value is Cat or Dog;
            }

            class C
            {
                int M(Animal animal)
                {
                    if (animal.IsBird) return 0;
                    return animal /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Animal animal)
                {
                    if (!animal.IsPet()) return 0;
                    return animal /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );


    [Fact]
    public Task Helpers_with_a_block_body() =>
        Verify.Suppressions("""
            sealed record Ok<T>(T Value);
            sealed record Err<E>(E Error);
            union Result<T, E>(Ok<T>, Err<E>)
            {
                public bool TryGetOk(out T value)
                {
                    if (this is Ok<T> ok)
                    {
                        value = ok.Value;
                        return true;
                    }

                    value = default!;
                    return false;
                }
            }

            static class Pets
            {
                public static bool TryGetCat(this Pet pet, out Cat cat)
                {
                    if (pet is Cat found)
                    {
                        cat = found;
                        return true;
                    }

                    cat = null!;
                    return false;
                }

                public static bool IsGrounded(Pet pet)
                {
                    if (pet is Bird) return false;
                    return true;
                }

                public static bool Unsure(Pet pet, bool flag)
                {
                    if (flag) return true;
                    return pet is not Bird;
                }

                public static void AssertGrounded(Pet pet)
                {
                    if (pet is Bird) throw new ArgumentException("A bird", nameof(pet));
                }

                public static void AssertMaybe(Pet pet, bool strict)
                {
                    if (strict && pet is Bird) throw new ArgumentException("A bird", nameof(pet));
                }

                public static Cat RequireCat(Pet pet)
                {
                    if (pet is not Cat cat) throw new ArgumentException("Not a cat", nameof(pet));
                    return cat;
                }
            }

            class C
            {
                int TryGet(Result<int, string> r)
                {
                    if (!r.TryGetOk(out var value)) return 0;
                    return r /*suppressed*/switch { Ok<int> => value };
                }

                int TryGetElse(Result<int, string> r)
                {
                    if (r.TryGetOk(out var value)) return value;
                    return r /*suppressed*/switch { Err<string> e => e.Error.Length };
                }

                int TryGetExtension(Pet pet)
                {
                    if (pet.TryGetCat(out var cat)) return cat.Name.Length;
                    return pet /*suppressed*/switch { Dog => 1, Bird => 2 };
                }

                int GuardThenReturn(Pet pet)
                {
                    if (!Pets.IsGrounded(pet)) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int GuardThatDoesNotDecide(Pet pet, bool flag)
                {
                    if (!Pets.Unsure(pet, flag)) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Assertion(Pet pet)
                {
                    Pets.AssertGrounded(pet);
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int AssertionThatMightNotThrow(Pet pet, bool strict)
                {
                    Pets.AssertMaybe(pet, strict);
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Requirement(Pet pet)
                {
                    var cat = Pets.RequireCat(pet);
                    return pet /*suppressed*/switch { Cat => cat.Name.Length };
                }
            }
            """
        );


    [Fact]
    public Task Members_that_write_this_through_a_reference() =>
        Verify.Suppressions("""
            union Slot(Cat, Dog, Bird)
            {
                public bool Sneaky()
                {
                    ref Slot self = ref this;
                    self = new Bird("Polly");
                    return true;
                }
            }

            struct Box
            {
                public Pet Pet;
                public void Touch()
                {
                    ref Pet pet = ref Pet;
                    pet = new Bird("Polly");
                }
            }

            static class Generic
            {
                public static void AssertNotBird<T>(T value)
                {
                    if (value is Bird) throw new InvalidOperationException();
                }

                public static bool IsNotBird<T>(T value) => value is not Bird;
            }

            class C
            {
                int Union(Slot slot)
                {
                    if (slot is Bird) return 0;
                    /*because*/slot.Sneaky();
                    return slot /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int Field(Box box)
                {
                    if (box.Pet is Bird) return 0;
                    /*because*/box.Touch();
                    return box.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                // In generic code, `value is Bird` on a T that's a union tests the boxed union, which is never a Bird.
                int GenericAssertion(Pet pet)
                {
                    Generic.AssertNotBird(pet);
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int GenericTest(Pet pet)
                {
                    if (!Generic.IsNotBird(pet)) return 0;
                    return pet /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Static_and_extension_helpers() =>
        Verify.Suppressions("""
            static class PetExtensions
            {
                public static bool IsBird(this Pet pet) => pet is Bird;
                public static bool IsCatOrDog(Pet pet) { return pet is Cat or Dog; }
                public static bool HasBird(this Owner owner) => owner.Pet is Bird;
                public static bool IsNotBird(Pet pet) => !IsBird(pet);

                extension(Pet pet)
                {
                    public bool CanFly => pet is Bird;
                    public bool IsFlier() => pet is IFlier;
                }
            }

            record Owner(Pet Pet);

            class C
            {
                static bool Flies(Pet p) => p is Bird;

                int Extension(Pet pet)
                {
                    if (pet.IsBird()) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Static(Pet pet)
                {
                    if (!PetExtensions.IsCatOrDog(pet)) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Private(Pet pet)
                {
                    if (Flies(pet)) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int ExtensionProperty(Pet pet)
                {
                    if (pet.CanFly) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int ExtensionBlockMethod(Pet pet)
                {
                    if (pet.IsFlier()) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Member(Owner owner)
                {
                    if (owner.HasBird()) return 0;
                    return owner.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Nested(Pet pet)
                {
                    if (!PetExtensions.IsNotBird(pet)) return 0;
                    return pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );
}
