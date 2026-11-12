namespace UnionNarrowing.Tests;

/// <summary>
/// Unions whose cases are unions, and unions inside a case or inside the object switched on.
/// </summary>
public class NestedTests
{
    private const string Thing = """

        public record Rock;
        public union Thing(Pet, Rock);
        """;

    [Fact]
    public Task Nested_patterns_in_a_tuple_switch() =>
        Verify.Suppressions("""
            record Owner(Pet Pet);

            class C
            {
                int M(Thing t, bool flag)
                {
                    if (t is Rock) return 0;
                    if (t is Pet and Bird) return 1;
                    return (t, flag) /*suppressed*/switch { (Pet and (Cat or Dog), _) => 2 };
                }

                int N(Owner a, Owner b)
                {
                    if (a.Pet is Bird || b.Pet is Bird) return 0;
                    return (a, b) /*suppressed*/switch { ({ Pet: Cat or Dog }, { Pet: Cat or Dog }) => 1 };
                }

                // b's Pet could be a Bird.
                int O(Owner a, Owner b)
                {
                    if (a.Pet is Bird) return 0;
                    return (a, b) /*warns*/switch { ({ Pet: Cat or Dog }, { Pet: Cat or Dog }) => 1 };
                }

                int EitherTuplePattern(Owner a, Owner b)
                {
                    if (a.Pet is Bird) return 0;
                    return (a, b) /*suppressed*/switch { ({ Pet: Cat }, _) or ({ Pet: Dog }, _) => 1 };
                }

                // A Dog whose owner's partner has a Dog or a Bird is left.
                int EitherTuplePatternPartly(Owner a, Owner b)
                {
                    if (a.Pet is Bird) return 0;
                    return (a, b) /*warns*/switch { ({ Pet: Cat }, _) or ({ Pet: Dog }, { Pet: Cat }) => 1 };
                }
            }
            """ + Thing
        );

    /// <summary>
    /// In `Pet and Bird`, Bird tests the Pet inside, not the Thing.
    /// </summary>
    [Fact]
    public Task Pattern_on_the_union_inside_says_nothing_about_the_outer_one() =>
        Verify.Suppressions("""
            class C
            {
                int M(Thing t)
                {
                    if (t is Pet and Bird) return t /*warns*/switch { Rock => 0 };
                    return 1;
                }
            }
            """ + Thing
        );

    [Fact]
    public Task Union_member_of_a_case() =>
        Verify.Suppressions("""
            class C
            {
                int M(Young y)
                {
                    if (y is Kitten { Toy: Ball }) return 0;
                    return y /*suppressed*/switch { Kitten { Toy: Mouse } => 1, Dog => 2 };
                }

                int N(Young y)
                {
                    if (y is Kitten(Ball)) return 0;
                    return y /*suppressed*/switch { Kitten(Mouse) => 1, Dog => 2 };
                }

                int O(Young y)
                {
                    if (y is Kitten { Toy: Ball }) return 0;
                    return y /*warns*/switch { Kitten { Toy: Mouse } => 1 };
                }

                // The guard is about a Kitten in another Young.
                int P(Young y, Young other)
                {
                    if (other is Kitten { Toy: Ball }) return 0;
                    return y /*warns*/switch { Kitten { Toy: Mouse } => 1, Dog => 2 };
                }
            }
            """ + Snippets.Toys
        );

    [Fact]
    public Task Union_member_of_a_closed_hierarchy_case() =>
        Verify.Suppressions("""
            public record Ball;
            public record Mouse;
            public union Toy(Ball, Mouse);
            public closed record Animal;
            public record Kitten(Toy Toy) : Animal;
            public record Puppy : Animal;

            class C
            {
                int M(Animal animal)
                {
                    if (animal is Kitten { Toy: Ball }) return 0;
                    return animal /*suppressed*/switch { Kitten { Toy: Mouse } => 1, Puppy => 2 };
                }

                int N(Animal animal) => animal /*warns*/switch { Kitten { Toy: Mouse } => 1, Puppy => 2 };
            }
            """
        );


    [Fact]
    public Task Closed_hierarchy_member_of_a_case() =>
        Verify.Suppressions("""
            public closed record Expr;
            public sealed record Lit(int Value) : Expr;
            public sealed record Var(string Name) : Expr;
            public sealed record Bin(Expr Left, Expr Right) : Expr;

            public closed record DomainError;
            public sealed record Invalid : DomainError;
            public sealed record Missing : DomainError;
            public sealed record Forbidden : DomainError;
            public sealed record Ok<T>(T Value);
            public sealed record Err<E>(E Error);
            public union Result<T, E>(Ok<T>, Err<E>);

            class C
            {
                int Guarded(Expr e)
                {
                    if (e is Bin { Left: Lit }) return 0;
                    return e /*suppressed*/switch { Bin { Left: Var or Bin } => 1, Lit => 2, Var => 3 };
                }

                int GuardOnTheMember(Bin b)
                {
                    if (b.Left is Lit) return 0;
                    return b /*suppressed*/switch { { Left: Var } => 1, { Left: Bin } => 2 };
                }

                int EveryLeaf(Result<int, DomainError> r)
                {
                    if (r is Ok<int>) return 0;
                    return r /*suppressed*/switch { Err<DomainError> { Error: Invalid } => 1, Err<DomainError> { Error: Missing } => 2, Err<DomainError> { Error: Forbidden } => 3 };
                }

                int MissesALeaf(Result<int, DomainError> r)
                {
                    if (r is Ok<int>) return 0;
                    return r /*warns*/switch { Err<DomainError> { Error: Invalid } => 1, Err<DomainError> { Error: Missing } => 2 };
                }

                int GuardsTogether(Result<int, DomainError> r)
                {
                    if (r is Err<DomainError> { Error: Invalid }) return 0;
                    if (r is Err<DomainError> { Error: Missing }) return 0;
                    if (r is Err<DomainError> { Error: Forbidden }) return 0;
                    return r /*suppressed*/switch { Ok<int> ok => ok.Value };
                }

                int TwoPlacesOfOneValue(Bin b)
                {
                    if (b is { Left: Lit, Right: Lit }) return 0;
                    return b /*suppressed*/switch { { Left: Var or Bin } => 1, { Right: Var or Bin } => 2 };
                }

                int GuardsTogetherMissOne(Result<int, DomainError> r)
                {
                    if (r is Err<DomainError> { Error: Invalid }) return 0;
                    if (r is Err<DomainError> { Error: Missing }) return 0;
                    return r /*warns*/switch { Ok<int> ok => ok.Value };
                }

                int GuardsTogetherThenAWrite(Result<int, DomainError> r, Result<int, DomainError> other)
                {
                    if (r is Err<DomainError> { Error: Invalid }) return 0;
                    if (r is Err<DomainError> { Error: Missing }) return 0;
                    if (r is Err<DomainError> { Error: Forbidden }) return 0;
                    /*because*/r = other;
                    return r /*warns*/switch { Ok<int> ok => ok.Value };
                }

                int TwoPlacesOneHandled(Bin b)
                {
                    if (b is { Left: Lit, Right: Lit }) return 0;
                    return b /*warns*/switch { { Left: Var or Bin } => 1 };
                }

                int TwoPlacesThenAWrite(Bin b, Bin other)
                {
                    if (b is { Left: Lit, Right: Lit }) return 0;
                    /*because*/b = other;
                    return b /*warns*/switch { { Left: Var or Bin } => 1, { Right: Var or Bin } => 2 };
                }

                int EitherPlace(Bin b)
                {
                    if (b is { Left: Lit } or { Right: Lit }) return 0;
                    return b /*suppressed*/switch { { Left: Var or Bin, Right: Var or Bin } => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Union_member_of_the_object_switched_on() =>
        Verify.Suppressions("""
            record Owner(Pet Pet, int Age);

            class C
            {
                int M(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    return owner /*suppressed*/switch { { Pet: Cat } => 1, { Pet: Dog } => 2 };
                }

                int N(Owner owner)
                {
                    if (owner is { Pet: Bird }) return 0;
                    return owner /*suppressed*/switch { (Cat, _) => 1, (Dog, var age) => age };
                }

                // Could be null.
                int O(Owner? owner)
                {
                    if (owner?.Pet is Bird) return 0;
                    return owner /*warns*/switch { { Pet: Cat } => 1, { Pet: Dog } => 2 };
                }

                int P(Owner owner)
                {
                    if (owner.Pet is Bird) return 0;
                    return owner /*warns*/switch { { Pet: Cat } => 1, { Pet: Dog, Age: > 1 } => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Union_whose_case_is_a_union() =>
        Verify.Suppressions("""
            class C
            {
                int M(Thing t)
                {
                    if (t is Rock) return 0;
                    if (t is Pet and Bird) return 1;
                    return t /*suppressed*/switch { Pet and (Cat or Dog) => 2 };
                }

                int N(Thing t)
                {
                    if (t is Pet and Bird) return 1;
                    return t /*suppressed*/switch { Pet and (Cat or Dog) => 2, Rock => 3 };
                }

                // A Bird in a Thing gets past.
                int O(Thing t)
                {
                    if (t is Rock) return 0;
                    return t /*warns*/switch { Pet and (Cat or Dog) => 2 };
                }

                // Only some Birds are ruled out.
                int P(Thing t, bool flag)
                {
                    if (t is Pet and Bird && flag) return 1;
                    return t /*warns*/switch { Pet and (Cat or Dog) => 2, Rock => 3 };
                }
            }
            """ + Thing
        );

    [Fact]
    public Task Arms_that_split_a_case_on_a_bool_or_a_member_that_can_be_null() =>
        Verify.Suppressions("""
            public record Text(string S);
            public sealed record Marker(bool Checked);
            public sealed record Close(string? Reason);
            public sealed record Data(bool EndStream, int? Window);
            public union Event(Text, Marker, Close, Data);

            class C
            {
                int Positional(Event e)
                {
                    if (e is Text) return 0;
                    return e /*suppressed*/switch { Marker(true) => 1, Marker(false) => 2, Close => 3, Data => 4 };
                }

                int NullOrNot(Event e)
                {
                    if (e is Text or Marker or Data) return 0;
                    return e /*suppressed*/switch { Close { Reason: null } => 1, Close { Reason: { } reason } => reason.Length };
                }

                int Both(Event e)
                {
                    if (e is not Data) return 0;
                    return e /*suppressed*/switch
                    {
                        Data { EndStream: true } => 1,
                        Data { EndStream: false, Window: null } => 2,
                        Data { EndStream: false, Window: not null } => 3,
                    };
                }

                // One half is missing.
                int Half(Event e)
                {
                    if (e is Text or Close or Data) return 0;
                    return e /*warns*/switch { Marker(true) => 1 };
                }

                int NotNullOnly(Event e)
                {
                    if (e is Text or Marker or Data) return 0;
                    return e /*warns*/switch { Close { Reason: { } reason } => reason.Length };
                }

                // A string that's never null has no null half to split off.
                int Value(Event e)
                {
                    if (e is not Text) return 0;
                    return e /*warns*/switch { Text { S: "" } => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Statement_whose_sections_split_a_case_on_a_bool() =>
        Verify.Suppressions("""
            public record Text(string S);
            public sealed record Marker(bool Checked);
            public union Event(Text, Marker);

            class C
            {
                void M(Event e)
                {
                    /*suppressed*/switch (e) { case Text: break; case Marker(true): break; case Marker(false): break; }
                }

                void N(Event e)
                {
                    /*warns*/switch (e) { case Text: break; case Marker(true): break; }
                }
            }
            """, diagnosticId: "IDE0010"
        );

    [Fact]
    public Task Guards_that_cover_a_member_of_a_member() =>
        Verify.Suppressions("""
            public record Fenced;
            public record Indented;
            public union Fence(Fenced, Indented);
            public closed record Tag;
            public sealed record CodeBlock(Fence Kind) : Tag;
            public sealed record Heading(int Level) : Tag;
            public record Start(Tag Tag);
            public record Text(string S);
            public union Event(Start, Text);

            class C
            {
                // Both kinds of code block are ruled out, so no Start holds a CodeBlock.
                int M(Event e)
                {
                    if (e is Start { Tag: CodeBlock { Kind: Fenced } }) return 0;
                    if (e is Start { Tag: CodeBlock { Kind: Indented } }) return 0;
                    return e /*suppressed*/switch { Start { Tag: not CodeBlock } => 1, Text => 2 };
                }

                int OnlyOne(Event e)
                {
                    if (e is Start { Tag: CodeBlock { Kind: Fenced } }) return 0;
                    return e /*warns*/switch { Start { Tag: not CodeBlock } => 1, Text => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Discriminant_named_through_a_member() =>
        Verify.Suppressions("""
            enum Kind { Data, Ping, Close }

            closed record Frame { public abstract Kind Kind { get; } }
            sealed record DataFrame(byte[] Bytes) : Frame { public override Kind Kind => Kind.Data; }
            sealed record PingFrame : Frame { public override Kind Kind => Kind.Ping; }
            sealed record CloseFrame : Frame { public override Kind Kind => Kind.Close; }
            record Envelope(Frame Frame);

            class C
            {
                int Extended(Envelope e)
                {
                    if (e is { Frame.Kind: Kind.Data }) return 0;
                    return e.Frame /*suppressed*/switch { PingFrame => 1, CloseFrame => 2 };
                }

                int Nested(Envelope e)
                {
                    if (e is { Frame: { Kind: Kind.Data } }) return 0;
                    return e.Frame /*suppressed*/switch { PingFrame => 1, CloseFrame => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Deep_members() =>
        Verify.Suppressions("""
            public record Post;
            public record Draft;
            public union Comment(Post, Draft);
            public record Thread(Comment Comment);
            public record Episode(Thread Thread);
            public record Season(Episode Episode);
            public record Archive;
            public union Site(Season, Archive);

            class C
            {
                // Three members deep, under two cases.
                int M(Site s)
                {
                    if (s is Season { Episode.Thread.Comment: Draft }) return 0;
                    return s /*suppressed*/switch { Season { Episode.Thread.Comment: Post } => 1, Archive => 2 };
                }
            }
            """
        );

    [Fact]
    public Task List_patterns() =>
        Verify.Suppressions("""
            using System.Collections.Generic;
            using System.Collections.Immutable;

            class C
            {
                // What the variable is `and`-ed with holds wherever it's assigned.
                int Head(List<Pet> pets) => pets is [(Cat or Dog) and var head, ..] ? head /*suppressed*/switch { Cat => 1, Dog => 2 } : 0;

                int Last(List<Pet> pets) => pets is [.., not Bird and var last] ? last /*suppressed*/switch { Cat => 1, Dog => 2 } : 0;

                int Unconstrained(List<Pet> pets) => pets is [var head, ..] ? head /*warns*/switch { Cat => 1, Dog => 2 } : 0;

                // An ImmutableArray's elements are followed by index.
                int First(ImmutableArray<Pet> pets)
                {
                    if (pets is not [Cat or Dog, ..]) return 0;
                    return pets[0] /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                int Second(ImmutableArray<Pet> pets)
                {
                    if (pets is not [_, not Bird, ..]) return 0;
                    return pets[1] /*suppressed*/switch { Cat => 1, Dog => 2 };
                }

                // [Bird] is false for [Bird, Cat].
                int Exact(ImmutableArray<Pet> pets)
                {
                    if (pets is [Bird]) return 0;
                    return pets[0] /*warns*/switch { Cat => 1, Dog => 2 };
                }

                int ExactlyOne(ImmutableArray<Pet> pets)
                {
                    if (pets is not [Cat or Dog]) return 0;
                    return pets[0] /*suppressed*/switch { Cat => 1, Dog => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Member_of_a_case_taken_out_of_the_union() =>
        Verify.Suppressions("""
            public record Validation;
            public record Pricing;
            public record Remote;
            public union PlaceOrderError(Validation, Pricing, Remote);
            public sealed record Ok<T>(T Value);
            public sealed record Err<E>(E Error);
            public union Result<T, E>(Ok<T>, Err<E>);

            class C
            {
                bool IsTransient(Result<int, PlaceOrderError> r)
                {
                    if (r is Err<PlaceOrderError> { Error: Validation or Pricing }) return false;
                    if (r is not Err<PlaceOrderError> err) return false;
                    return err.Error /*suppressed*/switch { Remote => true };
                }

                bool Positive(Result<int, PlaceOrderError> r)
                {
                    if (r is Err<PlaceOrderError> { Error: Validation }) return false;
                    if (r is Err<PlaceOrderError> err) return err.Error /*suppressed*/switch { Pricing => true, Remote => false };
                    return false;
                }

                bool NotAllRuledOut(Result<int, PlaceOrderError> r)
                {
                    if (r is Err<PlaceOrderError> { Error: Validation }) return false;
                    if (r is not Err<PlaceOrderError> err) return false;
                    return err.Error /*warns*/switch { Remote => true };
                }
            }
            """
        );

    /// <summary>
    /// Under <c>Val and</c>, a pattern on a union that holds the union <c>Val</c> reads what the Val holds: its designations are what that holds, not cases of the outer union.
    /// </summary>
    [Fact]
    public Task Member_of_a_case_of_a_union_the_union_holds() =>
        Verify.Suppressions("""
            using System.Collections.Generic;
            using System.Collections.Immutable;
            using System.Linq;

            public sealed record Str(string V);
            public sealed record Int(long V);
            public sealed record Arr(Val First, ImmutableArray<Val> Rest);
            public union Val(Str, Int, Arr);
            public sealed record None;
            public union Item(None, Val);

            class C
            {
                string Designation(Item item) => item switch
                {
                    Val and Arr a => a.First /*warns*/switch { Int => "int" },
                    _ => "",
                };

                string Guard(Item item)
                {
                    if (item is Val and Arr a) return a.First /*warns*/switch { Int => "int" };
                    return "";
                }

                string Member(Item item) => item switch
                {
                    Val and Arr { First: var first } => first /*warns*/switch { Int => "int" },
                    _ => "",
                };

                IEnumerable<string> Elements(Item item) => item switch
                {
                    Val and Arr a => a.Rest.Select(v => v /*warns*/switch { Int => "int" }),
                    _ => [],
                };

                string Whole(Item item) => item switch
                {
                    Val and var v => v /*warns*/switch { Int => "int" },
                    _ => "",
                };
            }
            """
        );

    /// <summary>
    /// A record's generated Deconstruct gives what the positional property holds, but one written by hand can give something else the next time: a positional read and a property read of it are separate.
    /// </summary>
    [Fact]
    public Task Positional_and_property_reads_of_a_member_written_by_hand() =>
        Verify.Suppressions("""
            public record Text(string S);
            public sealed record Marker(bool Checked) { public bool Checked { get; set; } = Checked; }
            public sealed record Flaky(bool On) { int _n; public bool On => _n++ % 2 == 1; }
            public union Event(Text, Marker);
            public union Other(Text, Flaky);

            class C
            {
                static bool Flip(Event e) { if (e.Value is Marker m) m.Checked = true; return false; }

                int Settable(Event e)
                {
                    if (e is Text) return 0;
                    return e /*warns*/switch { Marker(true) => 1, Marker(false) when Flip(e) => 9, Marker { Checked: false } => 2 };
                }

                int Computed(Other o, Pet p) => (o, p) /*warns*/switch { (Text, _) => 0, (Flaky(true), _) => 1, (Flaky { On: false }, _) => 2 };
            }
            """
        );

    /// <summary>
    /// A record struct's positional properties can be set, by a getter of the struct itself too, between the positional read and the property read.
    /// A readonly record struct's can't.
    /// </summary>
    [Fact]
    public Task Positional_and_property_reads_of_a_record_struct() =>
        Verify.Suppressions("""
            public record struct Mark(bool Checked)
            {
                public int Touch { get { Checked = !Checked; return 0; } }
            }
            public readonly record struct Fixed(bool Checked);
            public sealed record Holder(Mark M);
            public sealed record Steady(Fixed F);
            public record Text(string S);
            public union Event(Text, Holder);
            public union Settled(Text, Steady);

            class C
            {
                int Guarded(Event e)
                {
                    if (e is Text) return 0;
                    return e /*warns*/switch { Holder { M: Mark(true) } => 1, Holder { M: { Touch: var t, Checked: false } } => 2 };
                }

                int Combinations(Holder h, Pet p) => (h, p) /*warns*/switch { ({ M: Mark(true) }, _) => 1, ({ M: { Touch: var t, Checked: false } }, _) => 2 };

                int Readonly(Settled s)
                {
                    if (s is Text) return 0;
                    return s /*suppressed*/switch { Steady { F: Fixed(true) } => 1, Steady { F: { Checked: false } } => 2 };
                }
            }
            """
        );
}
