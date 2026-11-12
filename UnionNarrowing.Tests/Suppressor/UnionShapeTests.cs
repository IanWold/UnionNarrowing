namespace UnionNarrowing.Tests;

/// <summary>
/// Which kinds of union (and non-union) the suppressor reasons about.
/// </summary>
public class UnionShapeTests
{
    [Fact]
    public Task Bool_and_enum_case_types_are_their_values() =>
        Verify.Suppressions("""
            class C
            {
                int Bool(Setting s)
                {
                    if (s is string) return 0;
                    return s /*suppressed*/switch { true => 1, false => 2 };
                }

                int OneValueRuledOut(Setting s)
                {
                    if (s is true or string) return 0;
                    return s /*suppressed*/switch { false => 1 };
                }

                int ValueMissing(Setting s)
                {
                    if (s is string) return 0;
                    return s /*warns*/switch { true => 1 };
                }

                int Enum(Token t)
                {
                    if (t is string) return 0;
                    return t /*suppressed*/switch { Keyword.If => 1, Keyword.Else => 2 };
                }

                int Relational(Token t)
                {
                    if (t is string || t is > Keyword.If) return 0;
                    return t /*suppressed*/switch { Keyword.If => 1 };
                }

                // Two enums' values don't mix, even where they're the same numbers.
                int TwoEnums(Mixed m)
                {
                    if (m is Size) return 0;
                    return m /*warns*/switch { Keyword.If => 1, Size.Large => 2 };
                }
            }
            public union Setting(bool, string);
            public enum Keyword { If, Else }
            public enum Size { Small, Large }
            public union Token(Keyword, string);
            public union Mixed(Keyword, Size);
            """, disabledWarnings: ["CS8524"]
        );

    [Fact]
    public Task Case_type_that_can_be_null_needs_null_handled() =>
        Verify.Suppressions("""
            record None;
            union Option<T>(T, None);
            union MaybeCat(Cat?, Dog);

            class C
            {
                // Unconstrained T can be null, and so can Cat?. Without the guard the compiler would want null
                // handled (CS8655), so suppressing CS8509 here would hide that.
                int M<T>(Option<T> option)
                {
                    if (option is None) return 0;
                    return option /*warns*/switch { T => 1 };
                }

                int N(MaybeCat cat)
                {
                    if (cat is Dog) return 0;
                    return cat /*warns*/switch { Cat => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Case_type_that_can_be_null_with_null_handled() =>
        Verify.Suppressions("""
            record None;
            union Option<T>(T, None);
            union MaybeCat(Cat?, Dog);

            class C
            {
                int M<T>(Option<T> option)
                {
                    if (option is None) return 0;
                    return option /*suppressed*/switch { T => 1, null => 2 };
                }

                int N(MaybeCat cat)
                {
                    if (cat is Dog or null) return 0;
                    return cat /*suppressed*/switch { Cat => 1 };
                }

                int O(MaybeCat cat)
                {
                    if (cat is null) return 0;
                    if (cat is Dog) return 1;
                    return cat /*suppressed*/switch { Cat => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Class_unions_that_can_change() =>
        Verify.Suppressions("""
            [System.Runtime.CompilerServices.Union]
            sealed class Mutable : System.Runtime.CompilerServices.IUnion
            {
                private object? _value;
                public Mutable(Cat cat) => _value = cat;
                public Mutable(Dog dog) => _value = dog;
                public object? Value => _value;
                public void Swap(Dog dog) => _value = dog;
            }

            [System.Runtime.CompilerServices.Union]
            sealed class Settable : System.Runtime.CompilerServices.IUnion
            {
                public Settable(Cat cat) => Value = cat;
                public Settable(Dog dog) => Value = dog;
                public object? Value { get; set; }
            }

            // A derived class could add state that Value reads.
            [System.Runtime.CompilerServices.Union]
            class Unsealed : System.Runtime.CompilerServices.IUnion
            {
                public Unsealed(Cat cat) => Value = cat;
                public Unsealed(Dog dog) => Value = dog;
                public object? Value { get; }
            }

            class C
            {
                int M(Mutable pet)
                {
                    if (pet is Dog) return 0;
                    return pet /*warns*/switch { Cat => 1 };
                }

                int N(Unsealed pet)
                {
                    if (pet is Dog) return 0;
                    return pet /*warns*/switch { Cat => 1 };
                }

                int O(Settable pet)
                {
                    if (pet is Dog) return 0;
                    return pet /*warns*/switch { Cat => 1 };
                }
            }
            """
        );


    [Fact]
    public Task Custom_unions_whose_storage_can_change() =>
        Verify.Suppressions("""
            [System.Runtime.CompilerServices.Union]
            sealed class Slot : System.Runtime.CompilerServices.IUnion
            {
                private readonly object?[] _slot;
                public Slot(Cat cat) => _slot = [cat];
                public Slot(Dog dog) => _slot = [dog];
                public object? Value => _slot[0];
                public void Swap(Dog dog) => _slot[0] = dog;
            }

            [System.Runtime.CompilerServices.Union]
            readonly struct Boxed : System.Runtime.CompilerServices.IUnion
            {
                private readonly System.Runtime.CompilerServices.StrongBox<object?> _box;
                public Boxed(Cat cat) => _box = new(cat);
                public Boxed(Dog dog) => _box = new(dog);
                public object? Value => _box.Value;
                public void Swap(Dog dog) => _box.Value = dog;
            }

            class C
            {
                int Class(Slot slot)
                {
                    if (slot is Dog) return 0;
                    slot.Swap(new Dog("Rex"));
                    return slot /*warns*/switch { Cat => 1 };
                }

                int Struct(Boxed boxed)
                {
                    if (boxed is Dog) return 0;
                    var copy = boxed;
                    boxed.Swap(new Dog("Rex"));
                    return copy /*warns*/switch { Cat => 1 };
                }
            }
            """
        );


    [Fact]
    public Task Custom_union_whose_value_is_static() =>
        Verify.Suppressions("""
            [System.Runtime.CompilerServices.Union]
            readonly struct Shared : System.Runtime.CompilerServices.IUnion
            {
                private static object? s_value;
                public Shared(Cat cat) => s_value = cat;
                public Shared(Dog dog) => s_value = dog;
                public object? Value => s_value;
            }

            class C
            {
                int M(Shared shared)
                {
                    if (shared is Dog) return 0;
                    var other = new Shared(new Dog("Rex"));
                    return shared /*warns*/switch { Cat => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Closed_case_types_are_their_leaf_types() =>
        Verify.Suppressions("""
            class C
            {
                int Narrowed(Result r)
                {
                    if (r is Ok) return 0;
                    return r /*suppressed*/switch { NotFound => 1, Timeout => 2 };
                }

                int ByTheCaseType(Result r)
                {
                    if (r is Error) return 0;
                    return r /*suppressed*/switch { Ok => 1 };
                }

                int OneLeafRuledOut(Result r)
                {
                    if (r is NotFound) return 0;
                    return r /*suppressed*/switch { Ok => 1, Timeout => 2 };
                }

                int LeafMissing(Result r)
                {
                    if (r is Ok) return 0;
                    return r /*warns*/switch { NotFound => 1 };
                }
            }
            """ + Snippets.Result
        );

    [Fact]
    public Task Custom_struct_union() =>
        Verify.Suppressions("""
            [System.Runtime.CompilerServices.Union]
            readonly struct CatOrDog : System.Runtime.CompilerServices.IUnion
            {
                public CatOrDog(Cat cat) => Value = cat;
                public CatOrDog(Dog dog) => Value = dog;
                public object? Value { get; }
            }

            class C
            {
                int M(CatOrDog pet)
                {
                    if (pet is Dog) return 0;
                    return pet /*suppressed*/switch { Cat => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Custom_struct_union_with_a_nullable_case_type() =>
        Verify.Suppressions("""
            [System.Runtime.CompilerServices.Union]
            readonly struct CatOrDog : System.Runtime.CompilerServices.IUnion
            {
                public CatOrDog(Cat? cat) => Value = cat;
                public CatOrDog(Dog dog) => Value = dog;
                private CatOrDog(object? value) => Value = value; // not a creation member, doesn't add a nullable case
                public object? Value { get; }
            }

            class C
            {
                int M(CatOrDog pet)
                {
                    if (pet is Dog) return 0;
                    return pet /*warns*/switch { Cat => 1 };
                }

                int N(CatOrDog pet)
                {
                    if (pet is Dog or null) return 0;
                    return pet /*suppressed*/switch { Cat => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Custom_struct_union_written_between() =>
        Verify.Suppressions("""
            [System.Runtime.CompilerServices.Union]
            struct CatOrDog : System.Runtime.CompilerServices.IUnion
            {
                public CatOrDog(Cat cat) => Value = cat;
                public CatOrDog(Dog dog) => Value = dog;
                public object? Value { get; set; }
            }

            // Value reads a field anyone can write.
            [System.Runtime.CompilerServices.Union]
            struct Exposed : System.Runtime.CompilerServices.IUnion
            {
                public object? Raw;
                public Exposed(Cat cat) => Raw = cat;
                public Exposed(Dog dog) => Raw = dog;
                public readonly object? Value => Raw;
            }

            class C
            {
                int Setter(CatOrDog pet)
                {
                    if (pet is Dog) return 0;
                    /*because*/pet.Value = new Dog("Rex");
                    return pet /*warns*/switch { Cat => 1 };
                }

                int Field(Exposed pet)
                {
                    if (pet is Dog) return 0;
                    /*because*/pet.Raw = new Dog("Rex");
                    return pet /*warns*/switch { Cat => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Custom_union_with_the_non_boxing_access_pattern() =>
        Verify.Suppressions("""
            using System.Diagnostics.CodeAnalysis;
            [System.Runtime.CompilerServices.Union]
            readonly struct CatOrDog : System.Runtime.CompilerServices.IUnion
            {
                public CatOrDog(Cat cat) => Value = cat;
                public CatOrDog(Dog dog) => Value = dog;
                public object? Value { get; }
                public bool HasValue => Value is not null;
                public bool TryGetValue([NotNullWhen(true)] out Cat? value) { value = Value as Cat; return value is not null; }
                public bool TryGetValue([NotNullWhen(true)] out Dog? value) { value = Value as Dog; return value is not null; }
            }
            class C
            {
                int TryGet(CatOrDog pet)
                {
                    if (pet.TryGetValue(out Dog? dog)) return dog.Name.Length;
                    return pet /*suppressed*/switch { Cat => 1 };
                }

                int Partly(CatOrDog pet)
                {
                    if (pet.TryGetValue(out Dog? dog) && dog.Name == "Rex") return 0;
                    return pet /*warns*/switch { Cat => 1 };
                }
            }
            """
        );

    // With CS8524 on, an enum case type has values no member has too, which only a test for the enum type covers.
    [Fact]
    public Task Enum_case_types_have_other_values() =>
        Verify.Suppressions("""
            class C
            {
                int Members(Token t)
                {
                    if (t is string) return 0;
                    return t /*warns*/switch { Keyword.If => 1, Keyword.Else => 2 };
                }

                int TheEnum(Token t)
                {
                    if (t is string) return 0;
                    return t /*suppressed*/switch { Keyword.If => 1, Keyword => 2 };
                }
            }
            public enum Keyword { If, Else }
            public union Token(Keyword, string);
            """
        );

    [Fact]
    public Task Generic_union_with_a_non_null_case_type() =>
        Verify.Suppressions("""
            record None;
            union Option<T>(T, None) where T : notnull;

            class C
            {
                int M<T>(Option<T> option) where T : notnull
                {
                    if (option is None) return 0;
                    return option /*suppressed*/switch { T => 1 };
                }

                int N(Option<string> option)
                {
                    if (option is None) return 0;
                    return option /*suppressed*/switch { string s => s.Length };
                }
            }
            """
        );

    [Fact]
    public Task Immutable_class_union() =>
        Verify.Suppressions("""
            [System.Runtime.CompilerServices.Union]
            sealed class CatOrDog : System.Runtime.CompilerServices.IUnion
            {
                private readonly object? _value;
                public CatOrDog(Cat cat) => _value = cat;
                public CatOrDog(Dog dog) => _value = dog;
                public object? Value => _value;
            }

            class C
            {
                int M(CatOrDog pet)
                {
                    if (pet is Dog) return 0;
                    return pet /*suppressed*/switch { Cat => 1 };
                }

                // The reference could be null.
                int N(CatOrDog? pet)
                {
                    if (pet is Dog) return 0;
                    return pet /*warns*/switch { Cat => 1 };
                }

                int O(CatOrDog? pet)
                {
                    if (pet is Dog or null) return 0;
                    return pet /*suppressed*/switch { Cat => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Immutable_class_union_from_another_assembly() =>
        Verify.Suppressions("""
            class C
            {
                int M(External.CatOrDog pet)
                {
                    if (pet is External.Dog) return 0;
                    return pet /*suppressed*/switch { External.Cat => 1 };
                }

                int N(External.Mutable pet)
                {
                    if (pet is External.Dog) return 0;
                    return pet /*warns*/switch { External.Cat => 1 };
                }
            }
            """,
            references: [Verify.CompileToMetadata("""
                namespace External;
                public record Cat;
                public record Dog;

                [System.Runtime.CompilerServices.Union]
                public sealed class CatOrDog : System.Runtime.CompilerServices.IUnion
                {
                    public CatOrDog(Cat cat) => Value = cat;
                    public CatOrDog(Dog dog) => Value = dog;
                    public object? Value { get; }
                }

                [System.Runtime.CompilerServices.Union]
                public sealed class Mutable : System.Runtime.CompilerServices.IUnion
                {
                    public Mutable(Cat cat) => Value = cat;
                    public Mutable(Dog dog) => Value = dog;
                    public object? Value { get; private set; }
                }
                """
            )]);

    [Fact]
    public Task Not_a_union() =>
        Verify.Suppressions("""
            class C
            {
                int M(object value)
                {
                    if (value is string) return 0;
                    return value /*warns*/switch { int => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Null_does_not_matter_where_nullable_warnings_are_off() =>
        Verify.Suppressions("""
            union MaybeCat(Cat?, Dog);

            class C
            {
            #nullable disable
                int M(MaybeCat cat)
                {
                    if (cat is Dog) return 0;
                    return cat /*suppressed*/switch { Cat => 1 };
                }
            #nullable restore
            }
            """
        );

    [Fact]
    public Task Nullable_type_argument_makes_a_case_type_nullable() =>
        Verify.Suppressions("""
            record None;
            union Option<T>(T, None);

            class C
            {
                int M(Option<string?> option)
                {
                    if (option is None) return 0;
                    return option /*warns*/switch { string s => s.Length };
                }

                int N(Option<string> option)
                {
                    if (option is None) return 0;
                    return option /*suppressed*/switch { string s => s.Length };
                }
            }
            """
        );

    [Fact]
    public Task Nullable_union() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet? maybe)
                {
                    if (maybe is null || maybe is Bird) return 0;
                    return maybe /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet? maybe)
                {
                    if (maybe is Bird) return 0;
                    return maybe /*suppressed*/switch { Cat => 1, Dog => 2, null => 3 };
                }

                // Could be null.
                int O(Pet? maybe)
                {
                    if (maybe is Bird) return 0;
                    return maybe /*warns*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Nullable_union_through_Value_and_HasValue() =>
        Verify.Suppressions("""
            class C
            {
                int M(Pet? maybe)
                {
                    if (maybe is null or Bird) return 0;
                    return maybe.Value /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int N(Pet? maybe)
                {
                    if (!maybe.HasValue || maybe.Value is Bird) return 0;
                    return maybe /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // HasValue doesn't say anything about a null inside the union.
                int O(MaybeCat? maybe)
                {
                    if (!maybe.HasValue || maybe.Value is Dog) return 0;
                    return maybe /*warns*/switch { Cat => 1 };
                }
            }

            union MaybeCat(Cat?, Dog);
            """
        );

    [Fact]
    public Task Union_declared_in_a_referenced_assembly() =>
        Verify.Suppressions("""
            class C
            {
                int M(External.Shape shape)
                {
                    if (shape is External.Circle) return 0;
                    return shape /*suppressed*/switch { External.Square => 1, External.Triangle => 2 };
                }
            }
            """,
            references: [Verify.CompileToMetadata("""
                namespace External;
                public record Circle;
                public record Square;
                public record Triangle;
                public union Shape(Circle, Square, Triangle);
                """
            )]);

    [Fact]
    public Task Value_type_case_type_ruled_out_through_an_interface() =>
        Verify.Suppressions("""
            union IntOrCat(int, Cat);

            class C
            {
                int M(IntOrCat value)
                {
                    if (value is IComparable) return 0;
                    return value /*suppressed*/switch { Cat => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Types_the_runtime_relates_without_a_conversion() =>
        Verify.Suppressions("""
            using System.Collections.Generic;
            using System.Runtime.InteropServices;

            union Payload(int[], string);
            union Days(DayOfWeek[], string);
            union Number(int, string);
            sealed class Ghost : IDynamicInterfaceCastable
            {
                public bool IsInterfaceImplemented(RuntimeTypeHandle type, bool throwIfNotImplemented) => true;
                public RuntimeTypeHandle GetInterfaceImplementation(RuntimeTypeHandle type) => default;
            }
            union Spooky(Ghost, Cat);

            static class Generic
            {
                public static bool Is<T>(this Number n) => n.Value is T;
            }

            class C
            {
                // The runtime lets an int[] be a uint[] and an IList<uint>, and a DayOfWeek[] an int[].
                int UnsignedArray(Payload p)
                {
                    if (p.Value is uint[]) return p /*warns*/switch { string s => s.Length };
                    return 0;
                }

                int ListOfUnsigned(Payload p)
                {
                    if (p.Value is IList<uint>) return p /*warns*/switch { string s => s.Length };
                    return 0;
                }

                int EnumArray(Days d)
                {
                    if (d.Value is int[]) return d /*warns*/switch { string s => s.Length };
                    return 0;
                }

                int NotAnArray(Payload p)
                {
                    if (p is string) return p /*suppressed*/switch { string s => s.Length };
                    return 0;
                }

                // `value is T` with T an int? is true for a boxed int.
                int NullableTypeArgument(Number n)
                {
                    if (n.Is<int?>()) return n /*warns*/switch { string s => s.Length };
                    return 0;
                }

                int TypeArgument(Number n)
                {
                    if (n.Is<int>()) return 0;
                    return n /*suppressed*/switch { string s => s.Length };
                }

                // Ghost can be cast to any interface.
                int DynamicInterface(Spooky s)
                {
                    if (s.Value is IComparable) return s /*warns*/switch { Cat => 1 };
                    return 0;
                }
            }
            """
        );

    [Fact]
    public Task Value_type_case_types() =>
        Verify.Suppressions("""
            union Number(int, long, string);

            class C
            {
                int M(Number n)
                {
                    if (n is int or long) return 0;
                    return n /*suppressed*/switch { string s => s.Length };
                }

                int N(Number n)
                {
                    // `long` doesn't cover `int`: a boxed int is not a long.
                    if (n is long) return 0;
                    return n /*warns*/switch { string s => s.Length };
                }
            }
            """
        );

    [Fact]
    public Task Static_members_of_different_constructions_of_a_generic_type() =>
        Verify.Suppressions("""
            static class Cache<TKey>
            {
                public static readonly Pet Pet = typeof(TKey) == typeof(int) ? new Cat("Tom") : new Bird("Polly");
            }

            class C
            {
                int M()
                {
                    if (Cache<int>.Pet is Bird) return 0;
                    return Cache<string>.Pet /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int N()
                {
                    if (Cache<int>.Pet is Bird) return 0;
                    return Cache<int>.Pet /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );
}
