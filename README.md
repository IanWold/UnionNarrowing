<div align="center">

<h1>
UnionNarrowing
</h1>

[![UnionNarrowing NuGet version](https://img.shields.io/nuget/v/UnionNarrowing?style=for-the-badge&logo=nuget&label=Nuget)](https://www.nuget.org/packages/UnionNarrowing)

Roslyn diagnostic fixes to enhance unions in C# 15+

</div>

---

C# 15 checks that a switch over a union (what other languages call a discriminated union, tagged union or sum type) handles every case, but it only looks at the switch itself. If code before the switch has already ruled a case out, the compiler still asks for it. UnionNarrowing follows what earlier code proves about a union (or a closed hierarchy), the way TypeScript narrows a type after a check or Kotlin smart-casts it, and suppresses the exhaustiveness warnings that this narrowing shows to be wrong. It suppresses nothing else.

It also comes with analyzers and code fixes for the pitfalls that come with unions: throwing defaults that hide cases, default union values that hold no case at all, and boxed unions that never match their cases.

- [Install](#install)
- [Why does a switch over a union still warn?](#why-does-a-switch-over-a-union-still-warn)
- [Quick tour](#quick-tour)
- [Reference](#reference)
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

## Why does a switch over a union still warn?

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

    return pet switch // warning CS8509
    {
        Cat => 4,
        Dog => 4,
    };
}
```

The compiler reports:

```
warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern 'Bird' is not covered.
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

The [wiki](https://github.com/IanWold/UnionNarrowing/wiki/Reference) lists exactly what the package does, for anyone who needs to check a specific behavior: the [diagnostics](https://github.com/IanWold/UnionNarrowing/wiki/Diagnostics), what the [suppressions](https://github.com/IanWold/UnionNarrowing/wiki/Suppressions) follow, and each of the [analyzers and code fixes](https://github.com/IanWold/UnionNarrowing/wiki/Analyzers). Examples there use the `Pet` union from [Why does a switch over a union still warn?](#why-does-a-switch-over-a-union-still-warn), plus an `IFlier` interface that only `Bird` implements. For worked examples, see the tests in [UnionNarrowing.Tests](UnionNarrowing.Tests/), where every switch is marked `/*suppressed*/` or `/*warns*/`.

These are the compiler warnings it can remove, as the compiler words them:

| Warning | Message |
|---|---|
| CS8509 | The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '...' is not covered. |
| CS8846 | The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '...' is not covered. However, a pattern with a 'when' clause might successfully match this value. |
| CS8655 | The switch expression does not handle some null inputs (it is not exhaustive). For example, the pattern '...' is not covered. |
| CS8847 | The switch expression does not handle some null inputs (it is not exhaustive). For example, the pattern '...' is not covered. However, a pattern with a 'when' clause might successfully match this value. |

## Building from source

With the .NET 11 SDK pinned in [global.json](global.json), `dotnet build` and `dotnet test` build and test everything. [CONTRIBUTING.md](CONTRIBUTING.md) describes the projects and how the tests work.
