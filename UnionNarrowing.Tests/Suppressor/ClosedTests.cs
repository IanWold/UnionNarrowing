namespace UnionNarrowing.Tests;

/// <summary>
/// Closed class hierarchies get the same treatment as unions.
/// </summary>
public class ClosedTests
{
    private const string Shapes = """

        public closed record Shape;
        public record Circle(double Radius) : Shape;
        public closed record Polygon : Shape;
        public record Square(double Side) : Polygon;
        public record Triangle(double Base, double Height) : Polygon;
        """;

    [Fact]
    public Task Generic_closed_hierarchy() =>
        Verify.Suppressions("""
            public closed record Result<T>;
            public record Ok<T>(T Value) : Result<T>;
            public record Error<T>(string Message) : Result<T>;

            class C
            {
                int M(Result<int> result)
                {
                    if (result is Error<int>) return -1;
                    return result /*suppressed*/switch { Ok<int> ok => ok.Value };
                }

                int N<T>(Result<T> result)
                {
                    if (result is Error<T>) return -1;
                    return result /*suppressed*/switch { Ok<T> => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Guard_then_switch() =>
        Verify.Suppressions("""
            class C
            {
                double M(Shape shape)
                {
                    if (shape is Circle c) return Math.PI * c.Radius * c.Radius;
                    return shape /*suppressed*/switch { Polygon => 0 };
                }
            }
            """ + Shapes
        );


    [Fact]
    public Task Discriminant_property() =>
        Verify.Suppressions("""
            enum ShapeKind { Circle, Square, Triangle }

            closed record Shape
            {
                public abstract ShapeKind Kind { get; }
                public abstract string Label { get; }
            }

            sealed record Circle(double Radius) : Shape
            {
                public override ShapeKind Kind => ShapeKind.Circle;
                public override string Label => "circle";
            }

            sealed record Square(double Side) : Shape
            {
                public override ShapeKind Kind { get { return ShapeKind.Square; } }
                public override string Label => Side > 1 ? "big" : "small";
            }

            sealed record Triangle(double Base) : Shape
            {
                public override ShapeKind Kind => ShapeKind.Triangle;
                public override string Label => "triangle";
            }

            class C
            {
                int Equality(Shape s)
                {
                    if (s.Kind == ShapeKind.Circle) return 0;
                    return s /*suppressed*/switch { Square => 1, Triangle => 2 };
                }

                int Pattern(Shape s)
                {
                    if (s.Kind is ShapeKind.Circle or ShapeKind.Square) return 0;
                    return s /*suppressed*/switch { Triangle => 1 };
                }

                int PropertyPattern(Shape s)
                {
                    if (s is { Kind: ShapeKind.Triangle }) return 0;
                    return s /*suppressed*/switch { Circle => 1, Square => 2 };
                }

                int SwitchStatement(Shape s)
                {
                    switch (s.Kind)
                    {
                        case ShapeKind.Circle:
                            return 0;
                        case ShapeKind.Square:
                            return 1;
                    }

                    return s /*suppressed*/switch { Triangle => 2 };
                }

                int NotEveryCaseHasAConstant(Shape s)
                {
                    if (s.Label == "big") return 0;
                    return s /*warns*/switch { Circle => 1, Triangle => 2 };
                }

                int OtherValue(Shape s)
                {
                    if (s.Kind != ShapeKind.Circle) return 0;
                    return s /*suppressed*/switch { Circle => 1 };
                }

                int WrongCase(Shape s)
                {
                    if (s.Kind == ShapeKind.Circle) return 0;
                    return s /*warns*/switch { Square => 1 };
                }
            }
            """
        );


    [Fact]
    public Task Bool_and_ordered_discriminants() =>
        Verify.Suppressions("""
            enum Code { Ok, Moved = 300, NotFound = 404, Failed = 500 }

            closed record Response
            {
                public abstract bool Success { get; }
                public abstract Code Code { get; }
            }

            sealed record Found(string Body) : Response
            {
                public override bool Success => true;
                public override Code Code => Code.Ok;
            }

            sealed record Redirect(string To) : Response
            {
                public override bool Success => false;
                public override Code Code => Code.Moved;
            }

            sealed record Broken(string Error) : Response
            {
                public override bool Success => false;
                public override Code Code => Code.Failed;
            }

            class C
            {
                int Not(Response r)
                {
                    if (!r.Success) return 0;
                    return r /*suppressed*/switch { Found f => f.Body.Length };
                }

                int Conditional(Response r) => r.Success ? r /*suppressed*/switch { Found f => f.Body.Length } : 0;

                int IsFalse(Response r)
                {
                    if (r.Success is false) return 0;
                    return r /*suppressed*/switch { Found f => f.Body.Length };
                }

                int Ordered(Response r)
                {
                    if (r.Code >= Code.NotFound) return 0;
                    return r /*suppressed*/switch { Found => 1, Redirect => 2 };
                }

                int Flipped(Response r)
                {
                    if (Code.Moved <= r.Code) return 0;
                    return r /*suppressed*/switch { Found => 1 };
                }

                int TooFew(Response r)
                {
                    if (r.Code > Code.Moved) return 0;
                    return r /*warns*/switch { Found => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Helper_on_a_generic_type_that_reads_its_own_member() =>
        Verify.Suppressions("""
            public record Idle;
            public record Pending;
            public record Done;
            public record Failed;
            public union Status(Idle, Pending, Done, Failed);

            public sealed record Query<T>(Status Status, T? Data)
            {
                public bool IsError => Status is Failed;
                public bool IsBusy() => Status is Pending;
            }

            class C
            {
                string Render(Query<string> q)
                {
                    if (q.IsError || q.IsBusy()) return "";
                    return q.Status /*suppressed*/switch { Idle => "idle", Done => q.Data ?? "" };
                }

                string Open<T>(Query<T> q)
                {
                    if (q.IsError) return "";
                    return q.Status /*warns*/switch { Idle => "idle", Done => "done" };
                }
            }
            """
        );

    [Fact]
    public Task Discriminant_set_by_constructors() =>
        Verify.Suppressions("""
            using System.Collections.Generic;
            using System.Linq;

            enum RecordType { A, Aaaa, Mx, Txt }

            closed class Resource
            {
                protected Resource(RecordType type) => Type = type;
                public RecordType Type { get; }
            }

            closed class Address : Resource
            {
                protected Address(RecordType type, string ip) : base(type) => Ip = ip;
                public string Ip { get; }
            }

            sealed class ARecord(string ip) : Address(RecordType.A, ip);
            sealed class AaaaRecord : Address
            {
                public AaaaRecord(string ip) : base(RecordType.Aaaa, ip) { }
                public AaaaRecord() : this("::1") { }
            }
            sealed class MxRecord() : Resource(RecordType.Mx);

            closed class Entry
            {
                protected Entry(RecordType type) => Type = type;
                public RecordType Type { get; }
            }

            sealed class Fixed() : Entry(RecordType.Mx);

            // Its constructors set different types.
            sealed class Varies : Entry
            {
                public Varies() : base(RecordType.Txt) { }
                public Varies(RecordType type) : base(type) { }
            }

            closed class Sometimes
            {
                protected Sometimes(RecordType type, bool set)
                {
                    if (set) Type = type;
                }

                public RecordType Type { get; }
            }

            sealed class Unset() : Sometimes(RecordType.Mx, false);
            sealed class Other() : Sometimes(RecordType.Txt, true);

            class C
            {
                IEnumerable<string> Addresses(IEnumerable<Resource> records) =>
                    records.Where(r => r.Type is RecordType.A or RecordType.Aaaa).Select(r => r /*suppressed*/switch { ARecord a => a.Ip, AaaaRecord a => a.Ip });

                int NotMx(Resource r)
                {
                    if (r.Type == RecordType.Mx) return 0;
                    return r /*suppressed*/switch { ARecord => 1, AaaaRecord => 2 };
                }

                int Copied(Resource r)
                {
                    var type = r.Type;
                    if (type is RecordType.Mx) return 0;
                    return r /*suppressed*/switch { ARecord => 1, AaaaRecord => 2 };
                }

                // An Unset's Type is RecordType.A, the default.
                int Conditional(Sometimes s)
                {
                    if (s.Type == RecordType.Mx) return 0;
                    return s /*warns*/switch { Other => 1 };
                }

                int NotTxt(Entry e)
                {
                    if (e.Type == RecordType.Txt) return 0;
                    return e /*warns*/switch { Fixed => 1 };
                }
            }
            """
        );

    [Fact]
    public Task Discriminant_of_a_union() =>
        Verify.Suppressions("""
            public enum ErrorClass { Domain, Infrastructure }

            public record Declined;
            public record Fraud;
            public record Timeout;
            public record Unavailable;
            public union PaymentError(Declined, Fraud, Timeout, Unavailable)
            {
                public ErrorClass Class => this switch
                {
                    Declined or Fraud => ErrorClass.Domain,
                    Timeout or Unavailable => ErrorClass.Infrastructure,
                };
            }

            class C
            {
                int Status(PaymentError e)
                {
                    if (e.Class == ErrorClass.Infrastructure) return 503;
                    return e /*suppressed*/switch { Declined => 402, Fraud => 403 };
                }

                int Copied(PaymentError e)
                {
                    var c = e.Class;
                    if (c == ErrorClass.Domain) return 400;
                    return e /*suppressed*/switch { Timeout => 504, Unavailable => 503 };
                }

                int Wrong(PaymentError e)
                {
                    if (e.Class == ErrorClass.Infrastructure) return 503;
                    return e /*warns*/switch { Declined => 402 };
                }
            }
            """
        );

    /// <summary>
    /// A getter that can change the union reads one case and leaves it holding another.
    /// </summary>
    [Fact]
    public Task Discriminant_of_a_union_whose_getter_changes_it() =>
        Verify.Suppressions("""
            public enum Kind { Cat, Dog, Bird }

            public union Changing(Cat, Dog, Bird)
            {
                public Kind Kind => this switch
                {
                    Cat when Swap() => Kind.Cat,
                    Cat => Kind.Cat,
                    Dog => Kind.Dog,
                    Bird => Kind.Bird,
                };

                bool Swap()
                {
                    this = new Bird("Polly");
                    return true;
                }
            }

            class C
            {
                int Governing(Changing pet) => pet.Kind switch
                {
                    Kind.Cat => pet /*warns*/switch { Cat => 1 },
                    _ => 0,
                };

                int Section(Changing pet)
                {
                    switch (pet.Kind)
                    {
                        case Kind.Cat:
                            return pet /*warns*/switch { Cat => 1 };
                        default:
                            return 0;
                    }
                }
            }
            """
        );

    [Fact]
    public Task Members_that_are_not_discriminants() =>
        Verify.Suppressions("""
            closed record Shape
            {
                public abstract double Weight { get; }
                public abstract object Tag { get; }
                public abstract double Ratio { get; }
                public abstract int Sides { get; }
            }

            sealed record Circle : Shape
            {
                public override double Weight => 1;
                public override object Tag => 1;
                public override double Ratio => double.NaN;
                public override int Sides => 0;
            }

            sealed record Square : Shape
            {
                public override double Weight => 2;
                public override object Tag => 2;
                public override double Ratio => 1.0;
                public override int Sides => 4;
            }

            class C
            {
                int ConvertedConstant(Shape s)
                {
                    if (s.Weight == 1.0) return s /*warns*/switch { Square => 0 };
                    return 1;
                }

                int Boxed(Shape s)
                {
                    if (s is { Tag: 1L }) return 0;
                    return s /*warns*/switch { Square => 2 };
                }

                int NotANumber(Shape s)
                {
                    if (s.Ratio == double.NaN) return 0;
                    return s /*warns*/switch { Square => 1 };
                }

                int ComparedAsADouble(Shape s)
                {
                    if (s.Sides == 4.0) return s /*warns*/switch { Circle => 1 };
                    return 0;
                }
            }
            """
        );

    [Fact]
    public Task Nested_closed_types_are_expanded() =>
        Verify.Suppressions("""
            class C
            {
                double M(Shape shape)
                {
                    if (shape is Square) return 1;
                    return shape /*suppressed*/switch { Circle => 2, Triangle => 3 };
                }

                double N(Shape shape)
                {
                    if (shape is Polygon) return 1;
                    return shape /*suppressed*/switch { Circle => 2 };
                }
            }
            """ + Shapes
        );

    [Fact]
    public Task Nullable_reference_needs_null_handled() =>
        Verify.Suppressions("""
            class C
            {
                double M(Shape? shape)
                {
                    if (shape is Circle) return 1;
                    return shape /*warns*/switch { Polygon => 2 };
                }

                double N(Shape? shape)
                {
                    if (shape is Circle or null) return 1;
                    return shape /*suppressed*/switch { Polygon => 2 };
                }
            }
            """ + Shapes
        );

    [Fact]
    public Task Switch_over_this() =>
        Verify.Suppressions("""
            public closed record Expr
            {
                public int Evaluate()
                {
                    if (this is Literal literal) return literal.Value;
                    return this /*suppressed*/switch { Sum sum => sum.Left.Evaluate() + sum.Right.Evaluate() };
                }
            }
            public record Literal(int Value) : Expr;
            public record Sum(Expr Left, Expr Right) : Expr;
            """
        );

    [Fact]
    public Task Discriminants_constructors_may_not_set_to_what_they_pass() =>
        Verify.Suppressions("""
            public enum RT { A, Mx, Txt }

            public closed class Early
            {
                protected Early(RT type, bool skip) { if (skip) return; Type = type; }
                public RT Type { get; }
            }
            public sealed class EarlyMx() : Early(RT.Mx, true);
            public sealed class EarlyTxt() : Early(RT.Txt, false);

            public closed class Incremented
            {
                protected Incremented(RT type) { Type = type; Type++; }
                public RT Type { get; }
            }
            public sealed class IncrementedA() : Incremented(RT.A);
            public sealed class IncrementedMx() : Incremented(RT.Mx);

            public closed class Reassigned
            {
                protected Reassigned(RT type) { if (type == RT.Txt) type = RT.A; Type = type; }
                public RT Type { get; }
            }
            public sealed class ReassignedA() : Reassigned(RT.A);
            public sealed class ReassignedTxt() : Reassigned(RT.Txt);

            class C
            {
                // An EarlyMx returns before setting Type, which stays RT.A.
                int Early(Early e)
                {
                    if (e.Type == RT.Mx) return 0;
                    return e /*warns*/switch { EarlyTxt => 1 };
                }

                int Incremented(Incremented d)
                {
                    if (d.Type == RT.Mx) return 0;
                    return d /*warns*/switch { IncrementedA => 1 };
                }

                int Reassigned(Reassigned r)
                {
                    if (r.Type == RT.Txt) return 0;
                    return r /*warns*/switch { ReassignedA => 1 };
                }

                int Tuple(Early e, Pet p) => (e, p) /*warns*/switch { ({ Type: RT.Mx }, _) => 1, (EarlyTxt, _) => 2 };
            }
            """
        );

    /// <summary>
    /// Leaves whose constructors set the discriminant to something that isn't a constant, or that something other than their constructors can see unset or set again.
    /// </summary>
    [Fact]
    public Task Discriminants_construction_leaves_unknown() =>
        Verify.Suppressions("""
            public enum RT { A, Mx, Txt }

            public closed class Record
            {
                protected Record(RT type) => Type = type;
                public RT Type { get; }
            }
            public sealed class ARecord() : Record(RT.A);
            public sealed class MxRecord() : Record(RT.Mx);
            public sealed class UnknownRecord(RT type) : Record(type);
            public sealed class Passed : Record { public Passed(RT raw) : base(raw) { } }

            public static class Wire { public static RT Read() => RT.A; }
            public closed class Frame
            {
                protected Frame(bool known, RT type) { Type = known ? type : Wire.Read(); }
                public RT Type { get; }
            }
            public sealed class Data() : Frame(true, RT.A);
            public sealed class Ping() : Frame(true, RT.Mx);
            public sealed class Read() : Frame(false, RT.Mx);

            public closed class Hooked
            {
                protected Hooked(RT type) { Hook(); Type = type; }
                public RT Type { get; }
                protected virtual void Hook() { }
            }
            public sealed class HookedA() : Hooked(RT.A);
            public sealed class HookedMx() : Hooked(RT.Mx)
            {
                public int Seen;
                protected override void Hook() => Seen = this.Type == RT.A ? 1 : 0;
            }

            public closed class Split
            {
                protected Split() { Type = RT.A; }
                protected Split(RT type) { Type = type; }
                public RT Type { get; }
            }
            public sealed partial class SplitA : Split { public SplitA() { } }
            public sealed partial class SplitMx : Split { public partial SplitMx(); }
            public sealed partial class SplitMx { public partial SplitMx() : base(RT.Mx) { } }

            public readonly record struct Code(int Value) { public static implicit operator RT(Code? code) => code is { } known ? (RT)known.Value : RT.Mx; }
            public closed class Converted
            {
                protected Converted(Code? code) { Type = code; }
                public RT Type { get; }
            }
            public sealed class ConvertedA() : Converted(new Code(0));
            public sealed class ConvertedMx() : Converted(null);

            public closed record Copied
            {
                protected Copied(RT type) => Type = type;
                protected Copied(Copied original) => Type = RT.Txt;
                public RT Type { get; }
            }
            public sealed record CopiedA() : Copied(RT.A);
            public sealed record CopiedMx() : Copied(RT.Mx);

            class C
            {
                int Unknown(Record r)
                {
                    if (r.Type != RT.A) return 0;
                    return r /*warns*/switch { ARecord => 1 };
                }

                int UnknownByLeaf(Record r) => r.Type == RT.A ? r /*warns*/switch { ARecord => 1 } : 0;

                int FromACall(Frame f)
                {
                    if (f.Type != RT.Mx) return 0;
                    return f /*warns*/switch { Ping => 1 };
                }

                int SeenUnset(Hooked h)
                {
                    if (h.Type != RT.A) return 0;
                    return h /*warns*/switch { HookedA => 1 };
                }

                int PartialConstructor(Split s)
                {
                    if (s.Type != RT.A) return 0;
                    return s /*suppressed*/switch { SplitA => 1 };
                }

                int PartialConstructorsBase(Split s)
                {
                    if (s.Type != RT.Mx) return 0;
                    return s /*suppressed*/switch { SplitMx => 1 };
                }

                int UserDefinedConversion(Converted c)
                {
                    if (c.Type == RT.Mx) return 0;
                    return c /*warns*/switch { ConvertedA => 1 };
                }

                int CopyConstructor(Copied c)
                {
                    if (c.Type == RT.Mx) return 0;
                    return c /*warns*/switch { CopiedA => 1 };
                }

                int KnownForTheOthers(Record r)
                {
                    if (r is UnknownRecord or Passed) return 0;
                    if (r.Type != RT.A) return 1;
                    return r /*suppressed*/switch { ARecord => 2 };
                }
            }
            """
        );

    [Fact]
    public Task Discriminant_copied_before_the_value_changes() =>
        Verify.Suppressions("""
            public enum Kind { Circle, Square, Triangle }
            public closed record Shape { public abstract Kind Kind { get; } }
            public sealed record Circle : Shape { public override Kind Kind => Kind.Circle; }
            public sealed record Square : Shape { public override Kind Kind => Kind.Square; }
            public sealed record Triangle : Shape { public override Kind Kind => Kind.Triangle; }

            class Holder
            {
                private readonly Shape _s;
                public readonly int Sides;

                public Holder(Shape a, Shape b)
                {
                    _s = a;
                    var k = _s.Kind;
                    _s = b;
                    if (k == Kind.Circle) return;
                    Sides = _s /*warns*/switch { Square => 4, Triangle => 3 };
                }
            }
            """
        );
}
