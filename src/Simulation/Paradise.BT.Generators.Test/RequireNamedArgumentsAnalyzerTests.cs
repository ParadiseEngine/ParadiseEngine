using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Paradise.BT.Generators;

namespace Paradise.BT.Generators.Test;

/// <summary>
/// PBT0013: a call to a <c>[RequireNamedArguments]</c> member passing two or more value arguments
/// must name them all. One value argument cannot transpose; child arguments are type-distinct.
/// </summary>
public class RequireNamedArgumentsAnalyzerTests
{
    /// <summary>A hand-written stand-in for what the builder generator emits.</summary>
    private const string Prelude = """
        using System;

        namespace Paradise.BT
        {
            [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method)]
            public sealed class RequireNamedArgumentsAttribute : Attribute { }
        }

        namespace Paradise.BT.Builder
        {
            public abstract class BTreeNode { }
        }

        namespace Game.Builder
        {
            public sealed class Forage : Paradise.BT.Builder.BTreeNode
            {
                [Paradise.BT.RequireNamedArguments]
                public Forage(float minStamina, float maxDistance, bool requireVisible = false) { }
            }

            public sealed class Repeat : Paradise.BT.Builder.BTreeNode
            {
                [Paradise.BT.RequireNamedArguments]
                public Repeat(int tickTimes, Paradise.BT.Builder.BTreeNode child, int breakStates = 0) { }
            }

            public static class Nodes
            {
                [Paradise.BT.RequireNamedArguments]
                public static Forage Forage(float minStamina, float maxDistance, bool requireVisible = false)
                    => new(minStamina: minStamina, maxDistance: maxDistance, requireVisible: requireVisible);
            }
        }
        """;

    private static CSharpAnalyzerTest<RequireNamedArgumentsAnalyzer, DefaultVerifier> Test(string code)
        => new() { TestCode = Prelude + code };

    [Test]
    public async Task Two_Positional_Value_Arguments_Are_Reported()
    {
        await Test("""
            static class Usage
            {
                static void Build() => _ = new Game.Builder.Forage({|PBT0013:0.3f|}, {|PBT0013:6f|});
            }
            """).RunAsync().ConfigureAwait(false);
    }

    [Test]
    public async Task Named_Value_Arguments_Pass()
    {
        await Test("""
            static class Usage
            {
                static void Build() =>
                    _ = new Game.Builder.Forage(minStamina: 0.3f, maxDistance: 6f, requireVisible: true);
            }
            """).RunAsync().ConfigureAwait(false);
    }

    /// <summary>One value cannot transpose, and the child is type-distinct — both stay positional.</summary>
    [Test]
    public async Task A_Single_Value_Argument_And_A_Child_Pass()
    {
        await Test("""
            static class Usage
            {
                static void Build() => _ = new Game.Builder.Repeat(3, new Game.Builder.Forage(minStamina: 0.3f, maxDistance: 6f));
            }
            """).RunAsync().ConfigureAwait(false);
    }

    [Test]
    public async Task A_Second_Value_Argument_Beside_A_Child_Requires_Names()
    {
        await Test("""
            static class Usage
            {
                static void Build() => _ = new Game.Builder.Repeat(
                    {|PBT0013:3|},
                    new Game.Builder.Forage(minStamina: 0.3f, maxDistance: 6f),
                    {|PBT0013:1|});
            }
            """).RunAsync().ConfigureAwait(false);
    }

    [Test]
    public async Task Factory_Methods_Are_Checked_Too()
    {
        await Test("""
            static class Usage
            {
                static void Build() => _ = Game.Builder.Nodes.Forage({|PBT0013:0.3f|}, {|PBT0013:6f|});
            }
            """).RunAsync().ConfigureAwait(false);
    }

    /// <summary>An unmarked member is not this analyzer's business.</summary>
    [Test]
    public async Task Unmarked_Members_Are_Ignored()
    {
        await Test("""
            static class Usage
            {
                static void Add(int a, int b) { }
                static void Build() => Add(1, 2);
            }
            """).RunAsync().ConfigureAwait(false);
    }
}
