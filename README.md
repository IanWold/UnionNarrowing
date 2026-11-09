<div align="center">

<h1>
UnionNarrowing
</h1>

[![UnionNarrowing NuGet version](https://img.shields.io/nuget/v/UnionNarrowing?style=for-the-badge&logo=nuget&label=Nuget)](https://www.nuget.org/packages/UnionNarrowing)

Roslyn diagnostic fixes to enhance unions in C# 15+

</div>

---

C# 15 checks that a switch over a union handles every case, but it only looks at the switch itself. If code before the switch has already ruled a case out, the compiler still asks for it. UnionNarrowing follows what earlier code proves about a union (or a closed hierarchy) and suppresses the exhaustiveness warnings that this narrowing shows to be wrong. It suppresses nothing else.

It also comes with analyzers and code fixes for the pitfalls that come with unions: throwing defaults that hide cases, default union values that hold no case at all, and boxed unions that never match their cases.

- [Install](#install)
- [Why](#why)
- [Quick tour](#quick-tour)
- [Reference](#reference)
  - [Diagnostics](#diagnostics)
  - [Suppressions: CS8509, CS8846, CS8655, CS8847, IDE0072, IDE0010](#suppressions)
  - [Analyzers](#analyzers)
  - [Code fixes](#code-fixes)
- [Building from source](#building-from-source)

## Install

```sh
dotnet add package UnionNarrowing
```

Or add it to the project file yourself:

```xml
<ItemGroup>
  <PackageReference Include="UnionNarrowing" Version="x.y.z" PrivateAssets="all" />
</ItemGroup>
```

The package contains only analyzers and is marked as a development dependency. It adds nothing to your build output, and it doesn't become a dependency of your own package.

**Requirements:** C# 15, which means the .NET 11 SDK, RC 2 (`11.0.100-rc.2`) or later. The analyzers are built against that compiler's version of Roslyn, so older compilers can't load them. They run in `dotnet build`, Visual Studio and Rider.

## Why

Here's a union:

```csharp
public record Cat(string Name);
public record Dog(string Name);
public record Bird(string Name);

public union Pet(Cat, Dog, Bird);
```

And here's a switch that the compiler rejects, even though it can't miss a case:

```csharp
int Legs(Pet pet)
{
    if (pet is Bird)
        return 2;

    return pet switch // warning CS8509: the switch expression is not exhaustive
    {
        Cat => 4,
        Dog => 4,
    };
}
```

No `pet` that reaches the switch can be a `Bird`, but the warning stays. The usual ways to silence it all give up the check that made the union worth using:

| Workaround | What it costs |
|---|---|
| `_ => throw new UnreachableException()` | Any case added to `Pet` later ends up here at run time, and nothing warns you. |
| A `Bird => throw ...` arm | Dead code, which has to be kept in step with the guard by hand. |
| `#pragma warning disable CS8509` | Also hides the next missing case that's real. |

UnionNarrowing is a [diagnostic suppressor](https://learn.microsoft.com/dotnet/api/microsoft.codeanalysis.diagnostics.diagnosticsuppressor). It removes CS8509 from this switch because it can prove that the earlier code rules out every case the switch skips. Anything it can't follow stays as a warning. If you add `Fish` to `Pet`, the warning comes back on this switch, because nothing rules `Fish` out.

## Quick tour

Everything below compiles without warnings once the package is installed. Each example warns again as soon as a case shows up that the code doesn't account for.

### Guards in their usual shapes

Exits like `return`, `throw`, `continue`, `break`, `goto`, `yield break`, or a call to a `[DoesNotReturn]` helper all count. So do enclosing branches, operators and outer switch arms:

```csharp
foreach (var pet in pets)
{
    if (pet is Bird) continue;
    total += pet switch { Cat => 1, Dog => 2 };
}

bool barks = pet is not Bird && pet switch { Cat => false, Dog => true };

int Rank(Pet pet) => pet switch
{
    Bird => 0,
    _ => pet switch { Cat => 1, Dog => 2 },   // only cats and dogs get here
};

if (pet is Bird) return "bird";
else if (pet is Dog) return "dog";
return pet switch { Cat c => c.Name };
```

Tests can look through members and helpers, and an assignment says what a value holds:

```csharp
if (owner is { Pet: Bird }) return 0;              // a property pattern on the owner
return owner.Pet switch { Cat => 1, Dog => 2 };

if (pet.IsBird()) return 0;                         // static bool IsBird(this Pet p) => p is Bird;
return pet switch { Cat => 1, Dog => 2 };

if (pet is Bird) pet = new Cat("Tom");              // a Bird is replaced with a Cat
return pet switch { Cat => 1, Dog => 2 };
```

### Loops and state machines

A loop condition narrows the loop body and the code after the loop. Assigning a switch's result back to the variable it switches on doesn't count as changing that variable before the switch:

```csharp
while (pet is Bird) pet = Next();
return pet switch { Cat => 1, Dog => 2 };

while (true) { pet = Next(); if (pet is not Bird) break; }
return pet switch { Cat => 1, Dog => 2 };

Pet Step(Pet pet)
{
    if (pet is Bird) return pet;
    pet = pet switch { Cat => new Dog("Rex"), Dog => new Bird("Polly") };
    return pet;
}
```

### Members, tuples, LINQ and helper methods

Narrowing isn't limited to plain locals:

- Readonly fields, get-only properties (including record properties, and getters that just return a readonly field), tuple elements and the fields of struct locals are followed.
- Tuple switches are checked one element at a time, and can mix in bools, enums and other types.
- Arms can look inside: at the union a case holds, or a union-typed member.
- A `Where` filter narrows the lambdas later in the chain, and the elements that come out of it.
- A local function or private helper takes on what its callers ruled out, and so does an internal class's constructor.
- A readonly member keeps what every constructor checked before setting it.

```csharp
record Owner(Pet Pet);

if (owner.Pet is Bird) return 0;
return owner.Pet switch { Cat => 1, Dog => 2 };

if (a is Bird || b is Bird) return false;
return (a, b) switch
{
    (Cat, Cat) or (Dog, Dog) => true,
    (Cat, Dog) or (Dog, Cat) => false,
};

if (pet is Bird) return 0;
return (pet, flag) switch { (Cat, true) => 1, (Cat, false) => 2, (Dog, _) => 3 };

if (owner.Pet is Bird) return 0;
return owner switch { { Pet: Cat } => 1, { Pet: Dog } => 2 };

pets.Where(p => p is not Bird).Select(p => p switch { Cat => 1, Dog => 2 });
pets.First(p => p is not Bird) switch { Cat => 1, Dog => 2 };

public int Describe(Pet pet) => pet is Bird ? 0 : Count(pet);
private static int Count(Pet p) => p switch { Cat => 1, Dog => 2 };

public Owner(Pet pet) { if (pet is Bird) throw new ArgumentException(); _pet = pet; }
public int Legs() => _pet switch { Cat => 4, Dog => 4 };
```

### Throwing defaults that hide cases (UN0002)

A default that only throws tells the compiler "this can't happen", so the compiler stops checking for missing cases. UN0002 does that check instead, taking narrowing into account. It only reports a default that actually catches a case:

```csharp
int Legs(Pet pet) => pet switch
{
    Cat => 4,
    _ => throw new InvalidOperationException(), // UN0002: The throwing discard arm catches Dog, Bird,
};                                              //          which the switch doesn't otherwise handle
```

The compiler never checks switch *statements* for exhaustiveness. They need a throwing `default:` for definite return even when they handle every case, so UN0002 is the only check they get.

### Default unions that hold no case (UN0003, UN0006, UN0007, UN0010)

The default value of a struct union has a null `Value`, which matches none of its case types. The compiler treats `pet switch { Cat => 1, Dog => 2, Bird => 3 }` as exhaustive, but that switch throws `SwitchExpressionException` on `default(Pet)`. These analyzers flag the places where a default union can get in:

```csharp
Pet a = default;                     // UN0003: default holds none of Pet's cases
var pets = new Pet[count];           // UN0006: fills the array with default(Pet)
var first = list.FirstOrDefault();   // UN0010: gives default(Pet) when the sequence is empty

class Owner
{
    readonly Pet _pet;               // UN0007: isn't assigned by every constructor
    public Owner() { }
}
```

### Boxed unions (UN0014, UN0015, UN0019)

A union is a struct. Boxing it, to `object` or a generic `T`, boxes the union, not what it holds, so a boxed `Pet` is never a `Cat`. And a union doesn't override `ToString`, so formatting one prints its type name:

```csharp
(object)pet is Cat                   // UN0014: never true
pet.Equals(cat)                      // UN0014: never true
((object)pet) switch { Cat => 1 }    // UN0014: Cat never matches
$"pet: {pet}"                        // UN0015: shows "Pet"; format pet.Value instead
record Owner(Pet Pet);               // UN0019: ToString prints "Owner { Pet = Pet }"
```

### Code fixes that respect narrowing

- **Add missing arms** (CS8509, CS8846, CS8655, CS8847) adds a `Case => throw new NotImplementedException()` arm for each case that's actually missing. It skips cases that earlier code ruled out, and adds `null` where the union can hold null. For a tuple switch, it adds one arm per missing combination, like `(Dog, _)`, and where the arms look inside the input, the missing nested patterns, like `Kitten { Toy: Ball }`.
- **Add missing cases** (IDE0010) does the same for a switch statement.
- **Name the cases** (UN0002) turns `_ => throw ...` into `Dog or Bird => throw ...`, so a case added later is reported.
- **Remove the arm** (UN0004) deletes a switch arm or case label that can't match, because earlier code already ruled out its case. The IDE shows these as faded.
- **Switch on the union** (UN0005) rewrites `pet.Value switch { ... }` to `pet switch { ... }`, so the compiler checks exhaustiveness.
- **Add a throwing default** (CS0161, CS1643, CS0165) for a switch statement, or a run of `if`s ending a method, that handles every case but still lets control fall out of its end, as far as the compiler can tell.

```csharp
if (pet is Bird) return 0;
return pet switch { Cat => 1 };
// Add missing arms:
return pet switch { Cat => 1, Dog => throw new NotImplementedException() };
```

### Fewer bad IDE suggestions

The IDE's "Populate switch" suggestions (IDE0072, IDE0010) don't understand unions, and their fix adds a throwing default, which is exactly what hides new cases. IDE0072 is suppressed on every switch expression over a union or closed hierarchy (CS8509 already reports what's missing), and IDE0010 on a switch statement that already handles every case that's left. Where a switch statement does miss a case, **Add missing cases** is offered alongside the IDE's fix.

### Following mutable state (opt-in)

Fields that aren't readonly, settable properties, `this` in a union's own members, and array elements could be changed by another thread between a test and a switch, so they aren't followed by default. To follow them anyway, as long as nothing that could run other code comes in between:

```ini
[*.cs]
unionnarrowing.follow_mutable_state = true
```

## Reference

This section lists exactly what the package does, for anyone who needs to check a specific behavior. Examples use the `Pet` union from [Why](#why), plus an `IFlier` interface that only `Bird` implements. For worked examples, see the tests in [UnionNarrowing.Tests](UnionNarrowing.Tests/), where every switch is marked `/*suppressed*/` or `/*warns*/`.

### Diagnostics

| ID | Kind | Category | Default | What it does | Fix |
|---|---|---|---|---|---|
| UN0001 | Suppression | | | Suppresses CS8509 on a switch expression whose unhandled cases earlier code ruled out | |
| UN0011 | Suppression | | | Suppresses CS8846, the same warning when an arm with a `when` clause might cover what's missing | |
| UN0012 | Suppression | | | Suppresses CS8655 (null not handled) when earlier code ruled out null | |
| UN0013 | Suppression | | | Suppresses CS8847, the same warning when an arm with a `when` clause might cover null | |
| UN0008 | Suppression | | | Suppresses IDE0072 (populate switch expression) on a switch over a union or closed hierarchy | |
| UN0009 | Suppression | | | Suppresses IDE0010 (populate switch statement) on a switch that handles every remaining case | |
| [UN0002](#un0002-cases-fall-through-to-a-throwing-default) | Analyzer | Reliability | Info | A throwing default (or final `else`, or a `throw` after the switch) catches cases the switch doesn't otherwise handle | [Name the cases](#name-the-cases-un0002) |
| [UN0003](#un0003-default-union-holds-no-case) | Analyzer | Reliability | Info | A default union value, which holds no case | |
| [UN0004](#un0004-switch-arm-case-or-test-cant-match) | Analyzer | Style | Info | An arm, case label or type test can't match, because earlier code ruled out its cases | Remove it |
| [UN0005](#un0005-switch-on-or-test-the-union-not-its-value) | Analyzer | Usage | Info | A switch or test on `union.Value` that could be on the union itself | Switch on the union |
| [UN0006](#un0006-array-of-unions-holds-elements-with-no-case) | Analyzer | Reliability | Info | An array whose elements are, or are reset to, default unions | |
| [UN0007](#un0007-union-member-can-be-left-holding-no-case) | Analyzer | Reliability | Info | A union member that some constructor leaves as default | [Make it required](#make-it-required-un0007) |
| [UN0010](#un0010-union-can-come-back-as-default) | Analyzer | Reliability | Info | A call that can fall back to a default union | |
| [UN0014](#un0014-boxed-union-never-matches-a-case) | Analyzer | Usage | Warning | A type test, cast, `Equals` or `GetType()` on a boxed union, as though it were what it holds | [Look at what it holds](#look-at-what-it-holds-un0014) |
| [UN0015](#un0015-union-formatted-as-text-shows-its-type-name) | Analyzer | Usage | Info | A union formatted as text, which shows its type name | [Format its Value](#format-its-value-un0015) |
| [UN0016](#un0016-throwing-default-only-catches-cases-added-later) | Analyzer | Reliability | Off | A throwing default on a switch that handles every case | Remove it |
| [UN0018](#un0018-default-struct-holds-a-union-with-no-case) | Analyzer | Reliability | Info | The default of a struct or tuple with a union inside, which holds no case | |
| [UN0019](#un0019-records-tostring-shows-a-union-members-type-name) | Analyzer | Usage | Info | A record whose generated `ToString` prints a union member as its type name | |
| CS8509, CS8846, CS8655, CS8847 | Code fix | | | | [Add missing arms](#add-missing-arms-cs8509-cs8846-cs8655-cs8847) |
| IDE0010 | Code fix | | | | [Add missing cases](#add-missing-cases-ide0010) |
| CS0161, CS1643, CS0165 | Code fix | | | | [Add a throwing default](#add-a-throwing-default-cs0161-cs1643-cs0165) |

The analyzers report at Info, so they show up as suggestions in the editor, except UN0014, which is a warning because the code it reports never does what it looks like, and UN0016, which is off. To see them in builds, to make them fail a build, or to turn UN0016 on, set their severity in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.UN0002.severity = warning
dotnet_diagnostic.UN0003.severity = warning
```

### Suppressions

CS8509 on a switch expression is suppressed when every case is either handled by an arm or ruled out by earlier code, and at least one case needed the earlier code. For a tuple switch, the same applies to every combination of cases. CS8846 is handled the same way, and it can also count an arm whose `when` clause always holds for a case (see [Arms that handle a case](#arms-that-handle-a-case)).

CS8655 and CS8847 are the same warnings for null alone: every case is handled, but the compiler's nullable flow state says the input could be null. They're suppressed when earlier code rules null out in a way the flow state doesn't follow, like `shapes.Where(s => s is not null).Select(s => s switch { ... })` (see [Null](#null)).

IDE0072 is suppressed on any switch expression over a union or closed hierarchy, complete or not: where a case is missing, CS8509 already says so, with Add missing arms as its fix. IDE0010 is suppressed on any switch statement that handles every case left after narrowing, whether or not narrowing was needed. The IDE doesn't understand unions at all.

The package only suppresses what it can prove. If any piece of the reasoning below doesn't apply, the warning stays.

#### What can be switched on

- `union` declarations, generic or not, from source or from a referenced assembly.
- Nullable unions, like `Pet?`. Null is then a case too (see [Null](#null)).
- Custom struct unions: a `[Union]` struct that implements `IUnion`.
- Custom class unions that can't change once constructed. The class has to be sealed and derive directly from `object`. Its instance fields all have to be readonly, it can have no settable properties other than `init`, and it can have no field-like events. For a class from a referenced assembly, its fields are checked in metadata.
- Closed hierarchies: `closed` classes and records, including generic ones. A closed type nested inside another closed type is expanded to its own subtypes. `this` inside a closed type can be switched on too.
- A union's case types are counted the way the compiler counts them. A case type that's a closed hierarchy is its leaf types, so with `union Result(Ok, Error)` and a closed `Error`, past `if (r is Ok) return 0;`, `r switch { NotFound => 1, Timeout => 2 }` is complete. A `bool` case type is `true` and `false`, and an enum case type is its members (plus the values no member has, unless CS8524 is turned off). Two enum case types' values don't mix, even where they're the same numbers.
- A `bool` or an enum, narrowed the same way: `if (c == Color.Red) return 0;` leaves `Color.Green` and `Color.Blue`. For an enum, this only helps where CS8524 is turned off (by `NoWarn` or `.editorconfig`): otherwise the switch also has to handle values no member has, which only a discard does, and suppressing CS8509 would hide CS8524.
- A tuple, like `(a, b) switch`, with at least one element of a type above. Its other elements can be anything: a `bool` or enum has its values as its cases, and any other type has a single case that only a pattern matching every value covers, like `_` or `int`. There can be at most 4,096 combinations of cases. Each element is followed separately, and some guards rule out combinations (see [Tuples](#tuples)). The tuple can be a literal, or a variable of a tuple type (`var t = (a, b); ... t switch`).
- Any of these inside something that can be followed piece by piece: `((Pet)pet) switch`, `(flag ? a : b) switch` and `(maybe ?? fallback) switch` (ruled out where every branch that doesn't throw rules it out), `owner?.Pet switch` (as `owner.Pet`, but it can be null), a call to a method of your own, `new Owner(cat).Pet`, or a LINQ element like `pets.First(p => p is not Bird)` (see [What a value was assigned](#what-a-value-was-assigned)).
- An object of any type whose arms look at unions inside it, like `owner switch { { Pet: Cat } => 1, { Pet: Dog } => 2 }` (see [Nested patterns](#nested-patterns)).

These are not reasoned about, so their warnings stay:

- Class unions that could change: unsealed ones (a derived class could add state that `Value` reads), or ones with a mutable field, a settable property or a field-like event.
- Any other type, such as `object`, unless its arms look at a union inside it.
- A tuple with no union or closed type in it, such as `(flag, n)`.

#### Which values are followed

- **Locals and by-value parameters.** This includes `foreach` variables (plain, deconstructed and `await foreach`), and variables declared by the guard itself: `if (value is not Pet pet || pet is Bird) return;` and `if (!TryGet(out var pet) || pet is Bird) return;`.
- **Primary constructor parameters of a class**, as long as nothing ever assigns them.
- **Members that can't change once the object is constructed**, and chains of them (`household.Owner.Pet`), starting from a followed local or parameter, from `this` in a class, or from a static member:
  - readonly fields, instance or static
  - get-only and `init` auto-properties
  - properties whose getter just returns `field`, with no setter other than `init`
  - positional properties of records and `readonly record struct`s
  - properties whose getter just returns one of these from `this` (`public Pet Pet => _pet;`, `get { return _pet; }`), up to four getters deep
  - anonymous types' properties, including what a query's `let` makes
  - properties like these from referenced assemblies (checked in metadata)
  - a `KeyValuePair`'s `Key` and `Value`, and a `System.Tuple`'s `Item1`, `Item2`, ...
- **Fields of a struct**, including a tuple's elements (by name or as `Item1`) and a record struct's positional properties, wherever the struct itself is followed: `t.Item1`, `r.Pet` for `var r = GetPair();`, `box.Inner.Pet`. They only change when the struct does, so writing any part of the struct (a field, a setter, a method that isn't `readonly`) counts as writing them.
- **Every element of an immutable collection** (`ImmutableArray`, `ImmutableList`, `ImmutableHashSet`, `ImmutableSortedSet`, `ImmutableQueue`, `ImmutableStack`, `FrozenSet`) held in a followed local or parameter: past `if (pets.Any(p => p is Bird)) return;` or `if (!pets.All(p => p is not Bird)) return;`, no element is a Bird, in a `foreach` over it, a LINQ lambda over it, or an element taken from it (`pets.First()`).
- **A nullable union's value.** `maybe.Value` on a `Pet?` is followed as `maybe`.
- **An `ImmutableArray`'s elements**, indexed by a constant or a followed variable: `pets[0]`, `pets[i]` (as long as `i` doesn't change in between).
- **`this`** in a class, for switching over a closed hierarchy.
- **Copies.** After `var local = pet;` or `other = pet;`, what's known about `pet` carries over to the copy. This covers chains of up to 4 copies, copies of a member's owner (`var same = owner; ... same.Pet`), and copies made by:
  - `var` patterns: `var other => ...`, `pet is var copy`, `case var other:`
  - typed patterns that bind the same object: `owner is Owner o`, `owner is { Pet: not Bird } o`, `shape is Polygon p` (not a case taken out of a union, like `pet is Cat c`)
  - designations inside subpatterns, which bind the member they're on: `{ Pet: not Bird and var p }` binds `owner.Pet`, `(not Bird and var x, _)` on a tuple literal binds its element, and `Kitten { Toy: var toy }` binds what the Kitten holds
  - deconstruction: `var (x, n) = (pet, 1);`, `var (x, n) = owner;` for a record (through its generated Deconstruct), `(Pet x, int n) = t;`, and a `KeyValuePair` or `System.Tuple` (through theirs)

  What was known when the copy was made always carries over. Facts established about the source later also carry over, as long as nothing has written it since the copy.

These are not followed (but see [Following mutable state](#following-mutable-state)):

- fields that aren't readonly, except a struct's
- settable, virtual, abstract and override properties, except a struct's auto-properties, and computed properties that do anything but return a followed member
- elements of arrays and other collections, except an `ImmutableArray`'s (and what's known about every element of an immutable collection)
- `ref`, `in` and `out` parameters, and ref locals
- `this` in a struct, and primary constructor parameters of a struct (`this = ...` replaces them)
- readonly members inside `init` accessors and member initializers, where they can still be assigned. Inside a constructor's own body, the members of what it's constructing (from `this`, or static ones in a static constructor) are followed, since only that body can assign them, and it does so visibly. That stops at a lambda (which could run after a later assignment), and if the constructor takes a reference to one of them.

#### What a value was assigned

Where the analysis stops, going back, at the statement that last assigned the value, what it was assigned says which cases it can be in:

- A union made from a case (`Pet pet = new Cat("Tom");`, `pet = cat;`, `new Pet(cat)`) is only that case. A closed type made from a derived type (`Shape shape = new Circle();`) is only that. A `Pet?` made from a `Pet` is what the `Pet` is, and has a value.
- `flag ? new Cat("Tom") : new Dog("Rex")` is either, and `a ?? b` is what both sides are. A branch that throws gives no value: `flag ? a : throw ...` is what `a` is, and `a ?? throw ...` is `a`, and not null. A call to a method that never returns counts as a throw here: a `[DoesNotReturn]` method, or a throw helper like `static T Fail<T>() => throw ...;`.
- A followed value (`pet = other;`) is whatever is ruled out for it there.
- A call to a method declared in this compilation that can't be overridden is what all its `return`s are (`static Pet MakeCat() => new Cat("Tom");`). So is `await` on a call to an async method like that (`await MakeCatAsync()`, with or without `.ConfigureAwait(false)`).
- A member of a new object is what it was set to: `new Owner(cat)` for a record's positional property, `new Owner { Pet = cat }`, `owner with { Pet = cat }` (and `with` leaves the rest as they were).
- A LINQ element that passed the chain's filters: `First`, `Last`, `Single` (with or without a predicate), `ElementAt`, `MaxBy` and `MinBy`, and `List<T>.Find` and `Array.Find`. The `OrDefault` forms and `Find` can also give a default union, which isn't a case the switch has to handle.
- A switch expression is what every arm that doesn't throw gives, each read in its arm: `pet = pet switch { Bird => new Cat("Tom"), var other => other };` leaves no Bird.
- An `out` argument to a method declared in this compilation that can't be overridden is what every assignment the method makes to that parameter is: past `TryCat(out var pet);`, with `pet = new Cat("Tom");` the method's only assignment, `pet` is a Cat.
- A readonly field or get-only auto-property that nothing in its type assigns (not even a constructor) always holds its initializer's value: `static readonly Pet Favourite = new Cat("Tom");` is a Cat wherever it's read.
- An `if` that assigns it in some branches: every branch that falls through has to rule the case out, and one that doesn't assign it gets what held before the `if`. So `if (pet is Bird) pet = new Cat("Tom");` rules out Bird.
- A switch statement that assigns it: what holds at every `break` out of it, and, without a `default:`, what held before it. So `switch (kind) { case 0: pet = new Cat("Tom"); break; case 1: pet = new Dog("Rex"); break; default: return 0; }` leaves a Cat or a Dog.
- A `try` that assigns it: what holds at the end of the `try` block and of every `catch` that falls through, then the `finally`. A `catch` that doesn't assign it only knows what held before the `try`, and only if the `try` block doesn't assign it either, since it can start anywhere in the block. So `try { pet = Load(); if (pet is Bird) return 0; } catch { return -1; }` rules out Bird.
- `x ??= e`: what it held before (and that it wasn't null), or what `e` is. Past `if (shape is Square) return 0; shape ??= new Circle();`, shape is no Square.
- A condition that assigns it before testing it: past `if ((pet = Next()) is Bird) return 0;` or `while ((pet = Next()) is Bird) { }`, pet is no Bird, and in `if ((pet = Next()) is not Bird) { ... }` or `while ((pet = Next()) is not Bird) { ... }` it isn't one either. The assignment has to be the condition's only write to it, with nothing in the condition reading it before.
- A readonly field or get-only auto-property of a class that every constructor sets once, in a statement of its own body (or leaves to the one it calls with `: this(...)`), and that nothing else assigns: what every constructor sets it to. So with `public Owner(Pet pet) { if (pet is Bird) throw ...; _pet = pet; }` as the only constructor, `_pet` is never a Bird, in any member. (Not null, though: code a constructor calls before it sets the member reads the default. And not in a struct, which can be made without a constructor.)

#### Tests that rule a case out

Each row shows what's ruled out past `if (<test>) return;`. When the switch is inside the `if`, the opposite cases are ruled out.

| Test | Rules out |
|---|---|
| `pet is Bird`, `pet is Bird b`, `pet is Bird { }` | `Bird` |
| `pet is Bird or Dog`, `pet is Bird \|\| pet is Dog` | `Bird`, `Dog` |
| `pet is not Cat`, `!(pet is Cat)` | `Dog`, `Bird` |
| `pet is not (Cat or Dog)` | `Bird` |
| `pet is IFlier`, an interface `Bird` implements | `Bird` |
| `pet is Bird && pet is IFlier`, `pet is IFlier and Bird` | `Bird` |
| `flag \|\| pet is Bird` | `Bird` |
| `pet is Bird \| pet is Dog`, `pet is not Bird & pet is not Dog` (the logical operators that evaluate both sides) | `Bird`, `Dog` |
| `(pet is Bird) == true`, `isBird != false` | `Bird` |
| `flag ? pet is Bird : pet is Bird or Dog`, `pet is Bird ? true : false` | `Bird` |
| `shape as Circle != null`, `(shape as Circle) is not null`, `circle != null` for `var circle = shape as Circle;`, `pet.Value as Bird != null` | `Circle` (or `Bird`) |
| `pet.Value is Bird`, `pet.Value is not (Cat or Dog)`, `pet is { Value: Bird }` | as for `pet` itself |
| `owner?.Pet is Bird`, `household?.Owner?.Pet is not (Cat or Dog)` | as for `owner.Pet`, wherever that's read (`owner` can't be null there) |
| `pet is null`, `pet is Bird or null` | null (and `Bird`) |
| `!maybe.HasValue`, `maybe == null`, for a `Pet?` | null |
| `pet switch { Bird => true, _ => false }` used as a condition | `Bird` |
| `(a, b) is (Bird, _)` | `Bird` for `a` |
| `owner is { Pet: Bird }`, `owner is (Bird, _)`, `household is { Owner.Pet: Bird }` | `Bird` for `owner.Pet` (or `household.Owner.Pet`) |
| `pet.IsBird()`, `Flies(pet)`, `pet.CanFly`, `animal.IsBird`, `pet.Is<Bird>()` | whatever the helper's body rules out (see below) |
| `u.TryGetValue(out Bird? b)`, `!u.HasValue`, on a custom union with the non-boxing access pattern | `Bird` (or null) |
| `n is int or long`, for `union Number(int, long, string)` | `int`, `long` |
| `value is IComparable`, for `union IntOrCat(int, Cat)` | `int` |
| `flag`, `!flag`, `flag == true`, for a `bool` | `true` (or `false`) |
| `c == Color.Red`, `c is Color.Red`, `c > Color.Green`, for an enum | the members it holds for |
| `s is true`, `t is Keyword.If`, `t is > Keyword.If`, for a union with a `bool` or enum case type | the values it holds for |

A **helper** is a method, local function, property getter, extension method or C# 14 extension member whose body is a single `bool` expression (`=> p is Bird`, or `{ return p is Bird; }`), declared in this compilation, that can't be overridden. A call to one reads as its body, with the argument in place of the parameter (or of `this`, for a union's own members), and the call's type arguments in place of the helper's type parameters (`static bool Is<T>(this Pet p) => p.Value is T;`). Helpers can call helpers, up to three deep. A union member that isn't marked `readonly` still counts as not changing the union if its body only reads `this`.

A `bool` local stands for the condition it was set to, when the value it tests never changes in the function: set where it's declared (`var isBird = pet is Bird;`), or by its only assignment (`bool isBird; ... isBird = pet is Bird;`). So does a local set to `x as T`, in a null check.

A custom union's `TryGetValue(out T value)` and `HasValue` are what the compiler uses to match `u is T` and `u is null` when the union has them, so they're read as those tests.

A test on what the value is a member of (`owner is { Pet: Bird }`) rules out cases for the member wherever it's read, since what it's read from can't be null there.

These rule nothing out, or less than you might expect:

- `pet is Bird && flag` (or `pet is Bird & flag`) rules nothing out on its own, because `flag` might be false. With `pet is Bird && !flag` too, together they rule out `Bird`: a case ruled out for every value of up to two `bool`s that guards test is ruled out.

- `pet is Bird { Name: "Polly" }` and `(a, b) is (Bird, Cat)` match only some Birds, so `Bird` isn't ruled out.
- `zoo is Parrot` says nothing about `Animal`, even if `Parrot` derives from `Animal`.
- `n is long` doesn't cover `int`, because a boxed `int` is not a `long`.
- `pet switch { Bird => true, Dog => flag, _ => false }` rules out only what every arm agrees on.
- A negated test (`is not T`) rules out a case only when no value of that case could ever be a `T`. That holds for a sealed class, struct or enum that isn't a `T`, or a type with no conversion to `T` at all. An unsealed class could have a subclass that is a `T`, and a type parameter could be anything:
  - `thing is not IFlier` doesn't rule out an unsealed `Rock`, but does rule out a `sealed class SealedRock`.
  - `option is not None` doesn't rule out `T` in `union Option<T>(T, None)`.
- A test against a union type itself isn't interpreted, unless that union is one of the case types (see [Nested patterns](#nested-patterns)).
- `Debug.Assert(pet is not Bird)` rules nothing out, because it's compiled out of release builds.

#### Tuples

In a tuple switch, a test on one element rules cases out for that element, whatever the others are. So past `if (a is Bird) return 0;`, `(a, b) switch { (Cat, _) => 1, (Dog, _) => 2 }` is complete.

An `if` guard (or branch) that tests several elements together rules out the combinations it covers:

- `if (a is Bird && b is Bird) return 0;` rules out `(Bird, Bird)`.
- `if ((a, b) is (Bird, Bird) or (Cat, Cat)) return 0;` rules out both of those combinations.
- `if (!(a is not Bird || b is not Bird)) return 0;` rules out `(Bird, Bird)`.

- `a is Bird && b is Bird ? 0 : (a, b) switch { ... }`, and the right of `||` and `&&`, rule out what the left side does.
- An earlier `switch (a, b) { case (Bird, Bird): return 0; }` that no section breaks out of, and that has no `default:`, rules out `(Bird, Bird)`. In a section of a switch statement over `(a, b)` with a single label, that label matched, and the labels before it didn't.

Combinations aren't ruled out by a guard with an unrelated condition, such as `a is Bird && b is Bird && flag`. They're also forgotten as soon as anything writes any of the elements. Combination facts only come from the places above, in the same function, with no label in between.

Elements that aren't unions are followed too: past `if (flag) return 0;`, `(pet, flag) switch { (_, false) => 1 }` is complete.

#### Where the test can be

**Before the switch, in the same block or an enclosing one:**

- `if (test) <exit>`, where the exit is one of:
  - `return`, `throw`, `break`, `continue`, `yield break`, or a `goto` past the switch
  - a call to a `[DoesNotReturn]` method
  - a call to a method that always throws: its body is `=> throw ...`, or ends in `throw` with no `return` anywhere in it
  - a block that ends in any of these

  These calls don't count as exits: a `[Conditional]` method (it may be compiled out), a method that only sometimes throws, an async method (it returns a faulted task), and a virtual, abstract or interface method.
- A call to a guard helper with a `[DoesNotReturnIf]` parameter: past `Guard.IsTrue(pet is not Bird);`, with `[DoesNotReturnIf(false)] bool condition`, Bird is ruled out. Not `Debug.Assert`, which is `[Conditional]`.
- `if`/`else` where only some branches fall through, including `else if` chains. A case is ruled out if every branch that falls through rules it out, so `if (flag) { if (pet is Bird) return 0; } else { if (pet is Bird) throw ...; }` rules out `Bird`.
- Inside nested blocks, `lock`, `using`, `checked`, `unchecked`, `unsafe` and `fixed`.
- Inside a `try`, if every `catch` exits too. A `catch` that falls through could run before the guard did.
- A guard with a label on it.
- A switch statement on the same value. If control falls out through a section's `break`, one of that section's labels matched. If there's no `default:`, falling out can also mean no label matched. So `switch (pet) { case Bird: return 0; }` rules out `Bird`.
- A statement that's sure to throw for some cases: `var n = pet switch { Bird => throw ..., Cat c => ..., Dog d => ... };`, `_ = pet is Bird ? throw ... : 0;`, `Console.WriteLine(pet is Cat or Dog ? "ok" : throw ...);`, or `x ?? throw ...` (which rules out null). A call to a method that never returns (`[DoesNotReturn]`, or a throw helper) counts as a throw: `Bird => Fail<int>()`. This doesn't count when the throwing part is only evaluated sometimes (the right side of `&&`, `||` or `??`, a branch of `?:`, inside a lambda), or when it's an argument to a `[Conditional]` call such as `Debug.WriteLine`.
- A loop just before the switch. It ended either because its condition was false, or at a `break`, so what's ruled out is what holds both ways: after `while (pet is Bird) pet = Next();` (or `do`/`while`, or `for`), the condition was false, even though the body writes `pet`; after `while (true) { pet = Next(); if (pet is not Bird) break; }`, what held at the break holds. A `break` from an inner loop or switch doesn't count, and a `break` with a `finally` on the way out that writes the variable holds nothing. The condition mustn't write the variable, except to assign it before testing it. If the loop writes the variable, nothing known from before the loop survives.
- The assignment the value last got (see [What a value was assigned](#what-a-value-was-assigned)).
- Before a label that control gets to by falling in or through `goto`s from earlier on: what holds on every way in. `if (pet is Cat) goto Known; if (pet is Bird) return 0; Known: ...` rules out Bird.

**Around the switch:**

- The `then` or `else` branch of an `if`, either branch of `?:`, or the right side of `&&` or `||`.
- An arm of an outer switch expression over the same value. That arm's pattern matched, and the earlier arms didn't. An earlier arm with a `when` clause only rules out the cases its clause always holds for, like `_ when pet is Dog`. The arm's own `when` clause held, too, whatever the outer switch is over (`n switch { > 0 when pet is not Bird => pet switch { ... }, ... }`), and an earlier arm whose pattern matches anything, like `_ when pet is Bird`, didn't match because its clause didn't hold. A `when` clause on what the arm binds the input to (`var p when p is Bird`, or `Shape s when s is Circle` on a closed hierarchy) says the same about the input. The outer switch can also be on what the value is a member of, or on a tuple literal with the value (or what it's a member of) as an element: in `(a, b) switch { (Bird, _) => 0, _ => a switch { ... } }`, `a` isn't a Bird, and in `(a, b) switch { (Bird, Bird) => 0, _ => (a, b) switch { ... } }`, `(Bird, Bird)` is ruled out.
- A section of an outer switch statement over the same value (or what it's a member of, or a tuple literal with it in). One of its labels matched, with its `when` clause holding, and the labels before it didn't. A `default:` section counts as coming after every label. None of this applies if the switch statement uses `goto case` or `goto default`.
- A `catch` with a filter: `catch (Exception) when (pet is not Bird) { ... }`. Filters run before the `finally` blocks inside the `try`, so this only counts if nothing in the `try` block (or a filter) writes the variable.
- The body of a `while` or `for` loop. The condition held when the iteration started, as long as nothing in the body writes the variable before the switch.
- The body of a `do` loop. Only what holds both on the way into the loop and whenever the condition sends control back around is used.
- The body of a `foreach` over a LINQ chain. See [Facts across functions](#facts-across-functions).
- A loop that contains the switch. Facts from before the loop still hold, as long as nothing in the loop writes the variable. That includes writes after the switch in the body, and in a `for` loop's incrementer.

It works the same way in iterators, async methods, lambdas, local functions and top-level statements.

#### Facts across functions

- **Lambdas and query clauses.** A test outside a lambda or query carries into it if the variable never changes. That means nothing assigns it after it's declared, and its declaration can't run again (a `goto` that jumps back over it would re-run it). `foreach` variables and unassigned parameters qualify.
- **Local functions.** A captured variable gets what holds for it at every call to the local function, when every reference to it is a direct call from outside its own body, and it runs when it's called (it isn't async or an iterator, unless the variable never changes). A test inside a local function works as usual.
- **LINQ.** A lambda that receives each element of a LINQ chain (`Enumerable`, `ParallelEnumerable`, or the `ImmutableArray` extensions) gets what that chain's filters established: `pets.Where(p => p is not Bird).Select(p => p switch { Cat => 1, Dog => 2 })`. The same applies to query syntax (`from p in pets where p is not Bird select ...`, with `let`, a second `from`, and `group ... into`), to `foreach (var p in pets.Where(...))`, and to the elements a chain hands back (see [What a value was assigned](#what-a-value-was-assigned)).
  - The filters are `Where` (with or without the index), `TakeWhile`, and `List<T>.FindAll` and `Array.FindAll`. A filter's predicate has to be a lambda with a single expression, or a method group for a helper (see [Tests that rule a case out](#tests-that-rule-a-case-out)). `SkipWhile(p).First()` is an element `p` was false for.
  - The filter is followed back through operators that pass elements on unchanged: `Where`, `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending`, `Order`, `OrderDescending`, `Reverse`, `Take`, `Skip`, `TakeLast`, `SkipLast`, `TakeWhile`, `SkipWhile`, `Distinct`, `DistinctBy`, `Except`, `ExceptBy`, `Intersect`, `IntersectBy`, `AsEnumerable`, `ToList`, `ToArray`, `ToHashSet`, the parallel ones (`AsParallel`, `AsSequential`, `AsOrdered`, `WithDegreeOfParallelism`, ...), and `Cast` or `OfType` to the type the elements already are. It's also followed through a `Select` that picks the element or a member of it (`Select(o => o.Pet)`, `Select(p => (p, 1))`, `Select(p => new { Pet = p })`), and through a local that never changes, set to a lazy query or to an immutable collection made from one (`ToImmutableArray()`, `ToImmutableList()`, `ToImmutableHashSet()`, `ToImmutableSortedSet()`, `ToFrozenSet()`), but not to a list or array, which could be changed later. `Concat`, `Union` and `UnionBy` keep what both sequences' chains rule out.
  - The receiving lambda can be a `predicate`, `selector`, `keySelector`, `elementSelector`, `collectionSelector`, `outerKeySelector` or `innerKeySelector`; either parameter of `Zip`'s and `Join`'s result selector, the element of `SelectMany`'s (and the second parameter, when the collection selector doesn't depend on the element or is a group from `GroupBy`); the element in `Aggregate` with a seed; and `List<T>.ForEach`, `Exists`, `TrueForAll` and `ConvertAll`, and `Array.ForEach`.
  - Operators that produce new elements (`Select` with anything else) or add elements (`Prepend`, `Append`, `DefaultIfEmpty`) cut the chain, and so does `SkipWhile` as a filter (except for the first element after it).
- **Local functions, private methods and constructors, and methods and constructors nothing outside the assembly can see.** A parameter gets what holds for its argument at every call, but only when:
  - every reference to the function is a direct call: never converted to a delegate, and never made from inside the function itself. A constructor's calls are `new` (or `new(...)`), `: this(...)`, `: base(...)` and a primary constructor's base call; a record's copy constructor (which `with` calls) and an attribute's constructor don't count.
  - every call passes something whose case can be worked out: a value that can be followed, a new case, a call to a method of your own, and so on (see [What a value was assigned](#what-a-value-was-assigned))
  - for a method, it isn't virtual, abstract, override, extern or partial, doesn't implement an interface member, and isn't the entry point
  - for a private method, the calls are found in every part of its type, in any file
  - for an `internal` method (or one in an `internal` type), they're found in every file, and the assembly has no `InternalsVisibleTo`; a method other assemblies can see could be called from anywhere

  Null counts as ruled out at a call where the compiler's flow state says the argument isn't null. A class's primary constructor parameter that nothing assigns gets what its constructor was called with in every member: with `internal class Owner(Pet pet)` made only by `new Owner(pet)` past `if (pet is Bird) return;`, `pet switch { Cat => 4, Dog => 4 }` in its members is complete.
- **Deconstructed `foreach`.** `foreach (var (pet, n) in items.Where(x => x.Pet is not Bird))` gets what the chain's filters say about the element's member that `pet` is (a tuple element, or a record property read through its generated Deconstruct).
- **Lambdas called directly.** A lambda's parameter gets what holds for the argument at every call, when the lambda is in a local that never changes and is only ever invoked (`Func<Pet, int> f = p => ...; f(pet)`), or when it's passed to a method of your own that only ever invokes it with one of its own by-value parameters, which it never assigns (`Run(pet, p => ...)` with `R Run<T, R>(T value, Func<T, R> f) => f(value)`). A lambda passed to a method from elsewhere gets nothing, since that method could call it with anything.

#### Arms that handle a case

An arm handles a case only if it matches every value of that case:

- `Cat`, `Cat c`, `Cat { }`, `Cat or Dog`, `_` and `var x`.
- Subpatterns that match anything: `Cat { Name: _ }`, `Dog(var name)`, `Cat(_) and { Name: var name }`, `Dog { Name: _ or null }`. Positional subpatterns count only through a `Deconstruct` method or a tuple.
- Tuple patterns like `(Cat, _)` and `(Cat, Dog) or (Dog, Cat)`, element by element.
- Patterns that look inside, combination by combination (see [Nested patterns](#nested-patterns)).
- For CS8846 only, an arm whose `when` clause always holds for a case, when nothing has written the value since the switch read it. For example, `_ when pet is Dog => ...` handles `Dog`, and so does `var p when p is Dog => ...`.
- For CS8846 only, arms whose `when` clauses between them hold for a case whatever up to two `bool` locals or parameters they test are: `Cat when loud => 1, Cat when !loud => 2` handles `Cat`, as long as no clause writes `loud`.

These don't handle their case:

- `Cat { Name: "Tom" }`
- `Dog { Name.Length: _ }`, which also needs `Name` to be non-null
- `Dog { Name: { } }`
- `Cat c when c.Name == "Tom"`
- a `null` arm on its own

#### Null

- If nullable warnings are enabled and some case type can be null, null is a case too. This applies to case types like `Cat?`, an unconstrained `T`, or `string?` in `Option<string?>`. A `null =>` arm has to handle it, or a test like `is null` or `is Dog or null` has to rule it out. Without this rule, suppressing CS8509 would hide CS8655, which the compiler only reports once CS8509 is gone.
- Under `#nullable disable`, null doesn't need to be handled.
- For a custom union, whether a case can be null comes from its single-parameter constructors. A `Cat? cat` parameter makes `Cat` a case that can be null.
- For a nullable union (`Pet?`), a class union, or a closed hierarchy, the variable itself can be null, and nullable flow state decides. A `Pet?` that may be null needs a `null =>` arm or an `is null` test, and one that's been checked doesn't. On a `Pet?`, `is null` covers both "no value" and a default union inside.
- When only null is missing, the compiler reports CS8655 (or CS8847) instead of CS8509, and that's suppressed when earlier code rules null out where the flow state can't see it: a `Where(s => s is not null)` filter, a copy made before the null check, or a helper whose every caller passes a value that isn't null.

#### What stops the narrowing

**These between the test and the switch:**

- assigning the variable, including in a nested statement, earlier in the same statement as the switch, or in a deconstruction
- passing it as `ref` or `out`, even to a `scoped ref` parameter
- calling a member of the union that isn't `readonly`, since it could reassign `this`
- calling a `this ref` extension method or a `ref` extension block member
- for a custom struct union, setting its `Value`
- a `when` clause that writes the variable
- a label that a `goto` could jump to, unless every `goto` to it comes from earlier on (then what holds at each `goto` counts too), or from later on, with nothing from the label on writing the variable. A `goto` that leaves a `try` with a `finally` on the way stops it, since the `finally` could write the variable.
- running in a `finally` block after the guard's `return`

**These anywhere in the function**, which stop the variable from being followed at all:

- a lambda or local function that writes it
- taking a reference to it: a `ref` local, `new Span<Pet>(ref pet)`, `&pet`, `__makeref(pet)`, a `ref` argument to a parameter that isn't `scoped`, an `[UnscopedRef]` member that returns `ref this`, or a `ref` extension property that hands out a span
- for a lambda or query that switches on a captured variable, any assignment to that variable, even after the lambda is created

**These are fine and don't stop the narrowing:**

- reading the variable, passing it by value or `in`, and calling `readonly` members on it
- assigning it, passing it as `out`, or passing it to a `scoped ref` parameter *before* the test
- capturing it in a lambda that only reads it
- assigning the switch's own result back: `pet = pet switch { ... }`, `(pet, n) = (pet switch { ... }, 1)`, `items[i] = pet switch { ... }`
- statements that don't write it, such as declarations, `using`, `lock`, `checked`, `try`, other loops and switches, `await`, and local functions

#### Nested patterns

Arms can look inside the input, and earlier code can rule out what's in there:

- **At the union a case holds**, for a union whose case is a union. With `union Thing(Pet, Rock)`, past `if (t is Rock) return 0; if (t is Pet and Bird) return 1;`, `t switch { Pet and (Cat or Dog) => 2 }` is complete.
- **At a union-typed member of a case**, of a union or a closed hierarchy. With `record Kitten(Toy Toy)`, past `if (y is Kitten { Toy: Ball }) return 0;`, `y switch { Kitten { Toy: Mouse } => 1, Dog => 2 }` is complete. Positional patterns work too: `Kitten(Mouse)`.
- **At a union-typed member of the object switched on**, whatever its type. Past `if (owner.Pet is Bird) return 0;`, `owner switch { { Pet: Cat } => 1, { Pet: Dog } => 2 }` is complete, if `owner` can't be null.

- **In a tuple switch**, element by element: `(t, flag) switch { (Pet and (Cat or Dog), _) => 2 }`, or `(a, b) switch { ({ Pet: Cat or Dog }, { Pet: Cat or Dog }) => 1 }` past guards on both Pets.

Each place the arms look at becomes another element, with its own cases, like a tuple's: one under a case is only there when the input holds that case. Every combination has to be one an arm without a `when` clause is sure to match, or include a case earlier code ruled out for its element. There can be at most six places, four steps deep, and 4,096 combinations.

#### Following mutable state

With `unionnarrowing.follow_mutable_state = true` in `.editorconfig`, these are followed too:

- fields that aren't readonly, instance or static, and settable auto-properties
- `this` in a struct, including a union's own members: `union Pet(...) { int Legs() { if (this is Bird) return 2; return this switch { ... }; } }`
- array elements, indexed by a followed variable: `pets[i]`
- `in`, `ref` and `ref readonly` parameters, and ref locals

Another thread could write any of these at any time, and so could code the method calls, through another reference to the same object or storage. So they're only followed when nothing comes between the test and the switch that could run other code: no calls (including property getters other than auto-properties', and indexers), no object creations, user-defined operators or conversions, string interpolation, `await`, `yield`, `foreach` or `using`, and nothing that takes a reference. Assigning a member the value is read through (on any object), any array element, or `this` also stops it. And the value isn't followed from one function into another: not into lambdas, not from what a function's callers pass it, and not through a LINQ chain's filters (`Where`, `First(predicate)`, a `foreach` over a filtered chain). What runs after the switch reads the value, like an arm's call, doesn't matter, unless a loop can bring control back around.

#### What it trusts

Like the compiler's own exhaustiveness check, the package assumes that a union's `Value` only changes when the union itself is written. That holds for `union` declarations. It also holds for custom struct unions and immutable class unions, unless their `Value` reads through to some other, shared state. For a custom union with `TryGetValue` and `HasValue` members, it assumes, as the compiler does when it matches patterns with them, that they agree with `Value`.

It also assumes that objects are made by their constructors, and that immutable collections (an `ImmutableArray` included) aren't changed through the array they were made over, as with `ImmutableCollectionsMarshal`.

### Analyzers

All of these work on struct unions. UN0002, UN0004 and UN0016 also work on closed hierarchies.

#### UN0002: Cases fall through to a throwing default

Reported on a switch over a union or closed hierarchy when its default only throws, and that default catches cases the switch doesn't otherwise handle. That's a switch expression ending in `_ => throw ...` (or `var x => throw ...`), or a switch statement whose `default:` section only throws. The message names the cases. For a tuple switch, it names the combinations, the way Add missing arms would write them: `(Dog, _), (Bird, _)`.

The same goes for a `throw` statement right after a switch statement with no `default:` (and no section that breaks out of it), or right after two or more `if (test) <exit>` statements: it's a default in all but name. That's a common way to satisfy definite return (CS0161), so it's only reported when some case actually reaches it.

- An arm with a `when` clause doesn't count as handling its case.
- Not reported when every case is handled, or ruled out by earlier code. A throwing `default:` that's only there for definite return (CS0161) is fine. (UN0016, which is off by default, reports a throwing discard arm on a switch expression like that.)
- Not reported when the default does anything other than throw.

It's also reported on an `if`/`else if` chain of at least two tests on one union or closed hierarchy value that ends in an `else` that only throws, when some case isn't sure to be caught by one of the tests: `if (pet is Cat) ... else if (pet is Dog) ... else throw ...` catches Bird. A single `if` with a throwing `else` is more likely a precondition, so it isn't reported.

#### UN0003: Default union holds no case

Reported on any of these, for a struct union with no case type that can be null:

- `default`, `default(Pet)`, `new Pet()`, and a parameter default like `Pet pet = default`
- `Activator.CreateInstance<Pet>()`, `Activator.CreateInstance(typeof(Pet))` and `RuntimeHelpers.GetUninitializedObject(typeof(Pet))`

Not reported:

- when a case type can be null, because then the default is just that null
- for `default` assigned to an `out` parameter, which is how a Try method says "nothing here"
- for a default that's only tested: by a type test or pattern, or a switch with an arm that matches it (`null`, a discard, `var`), or a switch statement
- for a local's initial value that nothing reads, because every way through the rest of the block assigns the local first: `Pet chosen = default; if (flag) chosen = a; else chosen = b;`
- for a custom union that declares its own parameterless constructor
- for `Activator.CreateInstance` with constructor arguments
- for other structs (but see [UN0018](#un0018-default-struct-holds-a-union-with-no-case))

#### UN0004: Switch arm, case or test can't match

Reported on a switch arm or case label when earlier code ruled out every case it could match. It's tagged as unnecessary, so the IDE fades it. For a tuple switch, it's reported when every combination of cases the arm could match was ruled out or matched by an earlier arm: past `if (a is Bird) return 0;`, `(Bird, _) =>` can't match. Arms that look inside count too (see [Nested patterns](#nested-patterns)): past `if (y is Kitten { Toy: Ball }) return 0;`, `Kitten { Toy: Ball } =>` can't match.

It's also reported on a type test that can't be true: past `if (pet is Bird) return 0;`, `pet is Bird b` is always false, and past `if (a is Bird) return false;`, so is `(a, b) is (Bird, _)`. There's no fix for this one, since removing a test changes what the code does.

Not reported:

- when the arm could still match some case, like `Dog or Bird` after ruling out only `Bird`
- when the arm could match null, like `null =>`, which matches a `default(Pet)`
- on constant case labels, which a `goto case` might target
- on an arm that's dead because of earlier arms, which the compiler already reports (CS8510, CS8120)
- when the analysis can't follow the value

#### UN0005: Switch on or test the union, not its Value

Reported on `pet.Value switch { ... }` and `switch (pet.Value)` for a struct union, when every pattern means the same thing on the union as on its `Value`. Patterns on a union test its `Value`, so switching on the union matches the same way, and the compiler then checks exhaustiveness. Type, declaration and `null` patterns qualify, as do discards, typed property patterns, `not`, `and` and `or`. In a switch statement, constant and `default:` labels qualify too.

It's also reported on a test like `pet.Value is Cat c`, which could be `pet is Cat c`, where the compiler checks that `Cat` is one of the cases.

Not reported:

- when a pattern means something else on the union: `var x` binds the union itself, and an untyped `{ }` is always true for the union
- when a pattern isn't allowed on the union, like `string` or `5` for `Pet`
- when a switch statement has other kinds of labels, such as relational ones
- on class unions, because `.Value` throws on null where a switch on the union doesn't
- on `Value` properties of anything other than a union, such as `int?.Value`

#### UN0006: Array of unions holds elements with no case

Reported when an array of a struct union is created full of default unions, or has elements reset to them:

- `new Pet[n]`, multidimensional arrays included
- `GC.AllocateArray<Pet>`, `GC.AllocateUninitializedArray<Pet>` and `Array.CreateInstance(typeof(Pet), n)`
- `Array.Resize` and `CollectionsMarshal.SetCount`, which fill new elements with defaults
- `Array.Clear` and `Span<T>.Clear()`, which reset elements
- `ArrayPool<Pet>.Rent`, which can return default elements

Not reported:

- for `new Pet[0]`
- for arrays with an initializer, or created from a collection expression
- for an array put in a local and filled right after, by `Array.Fill(array, value)`, `array.AsSpan().Fill(value)`, `new Span<Pet>(array).Fill(value)`, or a `for (var i = 0; i < array.Length; i++)` loop whose body starts with `array[i] = ...;`
- for unions with a case type that can be null

#### UN0007: Union member can be left holding no case

Reported on a field or auto-property of a struct union type in a class, when it has no initializer, isn't `required`, and some constructor doesn't assign it, much as CS8618 is for a reference that isn't nullable. It's also reported when there's no constructor to assign it at all: an implicit or primary constructor, or no static constructor for a static member.

These count as assigning it:

- direct assignment
- tuple deconstruction
- `out` or `ref` arguments
- calling another constructor with `: this(...)`

Not reported:

- on `required` members, which whoever creates the object has to set
- on members of structs
- on unions with a case type that can be null

#### UN0010: Union can come back as default

Reported on calls that can return a default struct union:

- `FirstOrDefault`, `LastOrDefault` and `SingleOrDefault` without a `defaultValue`, on `Enumerable` or `Queryable`
- `ElementAtOrDefault`
- `DefaultIfEmpty()` without a `defaultValue`
- `GetValueOrDefault(key)` on dictionaries, and `Nullable<Pet>.GetValueOrDefault()`
- your own generic methods, declared in source, that return `default` or `default(T)` for their type parameter on some path, when called with a union type argument (`Helpers.Fallback<Pet>()`, `new Box<Pet>().Get()`)
- `new Lazy<Pet>()` without a factory or value, whose `Value` is made with the parameterless constructor
- generic methods and properties from anywhere that say they can give the default, with a `T?` return for an unconstrained `T`, or `[MaybeNull]`: `List<T>.Find`, `Array.Find`, `FirstOrDefault` on an `ImmutableArray`, `JsonSerializer.Deserialize<T>`, `AsyncLocal<T>.Value`, `CollectionsMarshal.GetValueRefOrAddDefault`, and any library's `T? Get<T>()`
- a Try method's `out` value, with `[MaybeNullWhen(false)]`, when it's used anyway: the call's result is ignored (`map.TryGetValue(key, out var pet);`), or the variable is read where the call failed (`if (!map.TryGetValue(key, out var pet)) { ... pet ... }`)
- your own generic methods that store `default` through a `ref` or `out` parameter, or into an array's elements (`static void Reset<T>(ref T x) => x = default!;`), when called with a union

Not reported:

- for overloads that take a fallback value
- for a result that's only tested, like `pets.FirstOrDefault() is Cat c`
- for LINQ's `Max`, `Min`, `MaxBy` and `MinBy`, which are annotated `T?` for a sequence of a class, but throw on an empty one of a struct
- for unions with a case type that can be null
- for generic methods whose `default` comes only from a lambda or local function inside them
- for methods from other assemblies that don't say so: a plain `T Get<T>()` can't be seen into

#### UN0014: Boxed union never matches a case

A union is a struct. Boxing it (to `object`, `ValueType`, an interface, or a generic `T`) boxes the union itself, not what it holds, so a boxed `Pet` is a `Pet` at run time. Reported on:

- a type test against something the box can't be: `(object)pet is Cat` and `(object)pet is Cat or Dog` are never true
- a cast to one: `(Cat)(object)pet` always throws, and `(object)pet as Cat` is always null
- an arm or case label testing for one in a switch on a boxed union: `((object)pet) switch { Cat => ... }`
- `ReferenceEquals` with a union, which is boxed afresh for the call, so it's never true
- `pet.Equals(cat)`, with a value of a case: never true, since a union only equals another of the same union
- `pet.GetType() == typeof(Cat)`: never true (and `!=` always true)

`OfType<Cat>()` and `Cast<Cat>()` on a sequence of unions are covered by CA2021. A boxed union in a variable of type `object`, or a generic `T`, isn't followed.

#### UN0015: Union formatted as text shows its type name

A union doesn't override `ToString`, so formatting one gives its type name (`Pet`), not what it holds. Reported on `pet.ToString()`, `$"{pet}"`, `"..." + pet`, and a union passed to `Console.Write` or `WriteLine`, a `TextWriter`'s `Write` or `WriteLine`, `StringBuilder.Append`, `AppendLine`, `Insert` or `AppendFormat`, `string.Format`, `Concat` or `Join`, `Debug` or `Trace` output, and a logger's `Log`, `LogInformation`, `LogWarning` and the rest (`Microsoft.Extensions.Logging`). It's also reported on a sequence of unions passed to `string.Join`, `string.Concat` or `StringBuilder.AppendJoin`, each of which gets formatted the same way. Not reported for a union that declares its own `ToString`.

#### UN0016: Throwing default only catches cases added later

Off by default. Reported on a switch expression's throwing discard arm (`_ => throw ...`) when every case (or, for a tuple switch, every combination) is handled already, or ruled out by earlier code. All the arm does then is catch a case added to the type later, at run time, instead of the compiler reporting the switch as not exhaustive. Not reported when the arm also catches a null the compiler would otherwise want handled. Without the arm, a default union throws `SwitchExpressionException` instead of the arm's exception.

#### UN0018: Default struct holds a union with no case

Reported on the default of a struct or tuple that holds a union (as a field, an auto-property, or a tuple element, up to four levels down) whose default holds no case: `default(Box)`, `new Entry()` for a struct without its own parameterless constructor, `new Box[n]`, `default((Pet, int))`, and calls that can fall back to the default of one, like `entries.FirstOrDefault()`. The message says where the union is: `Box.Pet`. Structs from other assemblies aren't looked into, since their private fields aren't visible.

#### UN0019: Record's ToString shows a union member's type name

Reported on a public member of a union type (or a nullable union) in a record that doesn't declare its own `PrintMembers` or `ToString`. The record's generated `ToString` prints each public member with its own `ToString`, so `record Owner(Pet Pet)` prints `Owner { Pet = Pet }`. Declare `PrintMembers` to print the member's `Value`.

### Code fixes

#### Add missing arms (CS8509, CS8846, CS8655, CS8847)

Offered on a switch expression over a union or closed hierarchy (or a tuple with one in it). For CS8655 and CS8847, that's the `null` arm. Where the arms look inside the input (see [Nested patterns](#nested-patterns)), the missing arms are written the same way: `Kitten { Toy: Ball }`, `Pet and Bird`, `{ Pet: Dog }`. It adds a `Case => throw new NotImplementedException()` arm for each case that no arm without a `when` clause handles and that earlier code doesn't rule out. It also adds a `null` arm where null matters (see [Null](#null)). For closed hierarchies, it adds the leaf types. For a tuple, it adds an arm for each missing combination, with `_` for an element where every case is missing with the same rest: `(Dog, _)`. New arms follow the switch's existing layout: inline or one per line, with or without a trailing comma. It isn't offered for other types.

#### Add missing cases (IDE0010)

The same, for a switch statement: a `case Dog: throw new NotImplementedException();` section for each missing case, before the `default:` section if there is one. It's offered alongside the IDE's own fix, which adds a throwing default.

#### Name the cases (UN0002)

For a switch expression, replaces the throwing discard arm's pattern with the cases it catches: `_ => throw ...` becomes `Dog or Bird => throw ...` (or `(Dog, _) or (Bird, _) => throw ...` for a tuple), and `var x => throw ...` becomes `(Dog or Bird) and var x => throw ...`. A case added later is then missing, and the compiler reports it. For a switch statement, adds `case Dog:` and `case Bird:` labels to the default section, which keeps its `default:` for definite return; UN0002 then reports a case added later. For a `throw` after a switch statement, adds a section for the cases that throws the same way; after `if`s, one more `if (pet is Dog or Bird) throw ...;` before it. For an `if`/`else if` chain that ends in a throwing `else`, adds one more `else if (pet is Dog or Bird)` before the `else`, that throws the same way. The `else` then only catches a case added later, which UN0002 reports.

#### Add a throwing default (CS0161, CS1643, CS0165)

A switch statement that handles every case of a union (or every case left after narrowing) still lets control fall out of its end, as far as the compiler can tell, since a default union matches no case. At the end of a method (or local function, or accessor) that's "not all code paths return a value" (CS0161, or CS1643 in a lambda), and after one that assigns a variable in every section, "use of unassigned local variable" (CS0165). Both are errors, so nothing can suppress them. This adds `default: throw new UnreachableException();` to the switch (or `InvalidOperationException` where `UnreachableException` isn't available). It's only offered when the switch handles every case. For a method that ends in `if`s that test every case and exit (`if (pet is Cat) return 1; else if (pet is Dog) return 2; else if (pet is Bird) return 3;`), it adds the `throw` after them instead.

#### Make it required (UN0007)

Adds `required` to a settable or `init` property, or a field that isn't readonly, so whoever creates the object has to set it. Only offered where the member, and its setter, are at least as visible as its type, which `required` needs.

#### Remove the arm / Remove the case (UN0004, UN0016)

Removes a dead switch expression arm, or a dead case label. If the label is the only one in its section, the whole section goes, unless the section declares a local variable or local function that other sections could use. For UN0016, removes the throwing discard arm.

#### Switch on the union (UN0005)

Replaces `pet.Value` with `pet`, as the switch's input or in a test.

#### Look at what it holds (UN0014)

Rewrites the code to look at what the union holds: `(object)pet is Cat` becomes `pet is Cat`, `((object)pet) switch` becomes `pet switch` (and `switch ((object)pet)` becomes `switch (pet)`), `(object)pet as Cat` becomes `pet.Value as Cat`, `(Cat)(object)pet` becomes `(Cat)pet.Value!`, `pet.Equals(cat)` becomes `object.Equals(pet.Value, cat)`, and `pet.GetType()` becomes `pet.Value?.GetType()`.

#### Format its Value (UN0015)

Replaces the union with its `Value`: `$"{pet}"` becomes `$"{pet.Value}"`, `pet.ToString()` becomes `pet.Value?.ToString()`, and a sequence `pets` becomes `pets.Select(x => x.Value)`.

## Building from source

With the .NET 11 SDK pinned in [global.json](global.json), `dotnet build` and `dotnet test` build and test everything. [CONTRIBUTING.md](CONTRIBUTING.md) describes the projects and how the tests work.
