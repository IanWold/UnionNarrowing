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
    public Task Helpers_that_are_not_read() =>
        Verify.Suppressions("""
            abstract class Checker
            {
                public virtual bool IsBird(Pet pet) => pet is Bird;
                public bool Logs(Pet pet) { Console.WriteLine(pet); return pet is Bird; }
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
