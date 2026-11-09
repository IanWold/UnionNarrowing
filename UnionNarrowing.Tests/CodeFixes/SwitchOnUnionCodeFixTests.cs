using UnionNarrowing.Suppressors.Analyzers;
using UnionNarrowing.CodeFixes;

namespace UnionNarrowing.Tests;

/// <summary>
/// Switch on, or test, the union instead of its Value (UN0005).
/// </summary>
public class SwitchOnUnionCodeFixTests
{
    [Fact]
    public Task Fix_all() =>
        Verify.FixAll(new SwitchOnUnionCodeFix(), """
            class C
            {
                int M(Pet pet) => pet.Value switch { Cat => 1, _ => 0 };

                bool N(Pet pet) => pet.Value is Dog;
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, _ => 0 };

                bool N(Pet pet) => pet is Dog;
            }
            """, new SwitchOnValueAnalyzer()
        );

    [Fact]
    public Task Switch_on_the_union() =>
        Verify.CodeFix(new SwitchOnUnionCodeFix(), """
            class C
            {
                int M(Pet pet) => pet.Value switch { Cat => 1, Dog => 2, Bird => 3, _ => 0 };
            }
            """, """
            class C
            {
                int M(Pet pet) => pet switch { Cat => 1, Dog => 2, Bird => 3, _ => 0 };
            }
            """, new SwitchOnValueAnalyzer()
        );

    [Fact]
    public Task Test_the_union() =>
        Verify.CodeFix(new SwitchOnUnionCodeFix(), """
            class C
            {
                bool M(Pet pet) => pet.Value is Cat or Dog;
            }
            """, """
            class C
            {
                bool M(Pet pet) => pet is Cat or Dog;
            }
            """, new UnionNarrowing.Suppressors.Analyzers.SwitchOnValueAnalyzer()
        );
}
