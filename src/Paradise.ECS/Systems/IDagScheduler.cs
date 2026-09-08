namespace Paradise.ECS;

/// <summary>Computes parallel execution waves from system dependencies and component access.</summary>
public interface IDagScheduler
{
    /// <summary>
    /// Computes execution waves from system metadata.
    /// Each wave contains system indices (into the input span) that can run in parallel.
    /// </summary>
    /// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
    /// <param name="systems">The metadata for all systems to schedule.</param>
    /// <returns>An array of waves, where each wave is an array of local indices into <paramref name="systems"/>.</returns>
    int[][] ComputeWaves<TMask>(ReadOnlySpan<SystemMetadata<TMask>> systems)
        where TMask : unmanaged, IBitSet<TMask>;
}

/// <summary>
/// Default DAG scheduler using Kahn's topological sort and greedy wave assignment.
/// Resolves explicit dependency edges (<c>[After]</c>/<c>[Before]</c>) and
/// separates systems with component read/write conflicts into different waves.
/// </summary>
public sealed class DefaultDagScheduler : IDagScheduler
{
    /// <inheritdoc/>
    public int[][] ComputeWaves<TMask>(ReadOnlySpan<SystemMetadata<TMask>> systems)
        where TMask : unmanaged, IBitSet<TMask>
        => DagScheduling.ComputeWaves(systems, snapshotReads: false);
}

internal static class DagScheduling
{
    internal static int[][] ComputeWaves<TMask>(ReadOnlySpan<SystemMetadata<TMask>> systems, bool snapshotReads)
        where TMask : unmanaged, IBitSet<TMask>
    {
        int n = systems.Length;
        if (n == 0) return [];

        // Map global SystemId → local index (0..N-1)
        var globalToLocal = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++)
            globalToLocal[systems[i].SystemId] = i;

        // Build forward and reverse adjacency lists from AfterSystemIds (skip deps not in the set)
        var adj = new List<int>[n];      // adj[pred] → successors
        var predAdj = new List<int>[n];  // predAdj[succ] → predecessors
        var inDegree = new int[n];
        for (int i = 0; i < n; i++)
        {
            adj[i] = new List<int>();
            predAdj[i] = new List<int>();
        }

        for (int i = 0; i < n; i++)
        {
            var afterIds = systems[i].AfterSystemIds;
            if (afterIds.IsDefault) continue;
            foreach (var globalId in afterIds)
            {
                if (globalToLocal.TryGetValue(globalId, out var localPred))
                {
                    adj[localPred].Add(i);
                    predAdj[i].Add(localPred);
                    inDegree[i]++;
                }
            }
        }

        // [CurrentTick] fresh reads must observe same-tick writes: order writers of a
        // fresh-read component before the reader (the plain read/write conflict below only
        // separates the pair into different waves; it does not fix the direction).
        AddFreshReadEdges(systems, adj, predAdj, inDegree);

        // Topological sort (Kahn's algorithm)
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
            if (inDegree[i] == 0) queue.Enqueue(i);

        var topoOrder = new List<int>(n);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            topoOrder.Add(node);
            foreach (var succ in adj[node])
            {
                inDegree[succ]--;
                if (inDegree[succ] == 0) queue.Enqueue(succ);
            }
        }

        if (topoOrder.Count != n)
            throw new InvalidOperationException("Cyclic dependency detected among systems.");

        // Greedy wave assignment: earliest wave respecting deps, then bump on conflicts
        var waveOf = new int[n];

        var waveLists = new List<List<int>>();
        foreach (var node in topoOrder)
        {
            int wave = 0;
            foreach (var pred in predAdj[node])
            {
                wave = Math.Max(wave, waveOf[pred] + 1);
            }

            while (true)
            {
                while (waveLists.Count <= wave) waveLists.Add(new List<int>());

                bool hasConflict = false;
                foreach (var other in waveLists[wave])
                {
                    if (HasConflict(systems[node], systems[other], snapshotReads))
                    {
                        hasConflict = true;
                        break;
                    }
                }

                if (!hasConflict) break;
                wave++;
            }

            waveLists[wave].Add(node);
            waveOf[node] = wave;
        }

        var waves = new int[waveLists.Count][];
        for (int w = 0; w < waveLists.Count; w++)
            waves[w] = waveLists[w].ToArray();
        return waves;
    }

    private static bool HasConflict<TMask>(SystemMetadata<TMask> a, SystemMetadata<TMask> b, bool snapshotReads)
        where TMask : unmanaged, IBitSet<TMask>
    {
        return snapshotReads
            ? a.WriteMask.ContainsAny(b.WriteMask)
                || a.WriteMask.ContainsAny(b.FreshReadMask)
                || b.WriteMask.ContainsAny(a.FreshReadMask)
            : a.WriteMask.ContainsAny(b.ReadMask) || b.WriteMask.ContainsAny(a.ReadMask);
    }

    /// <summary>Adds implicit writer → fresh-reader dependency edges: any system whose write set
    /// overlaps another system's <see cref="SystemMetadata{TMask}.FreshReadMask"/> must run
    /// first, so the CurrentTick reader observes the same-tick write. Mutually fresh-reading
    /// writers form a cycle and are rejected like any other cyclic dependency.</summary>
    private static void AddFreshReadEdges<TMask>(
        ReadOnlySpan<SystemMetadata<TMask>> systems,
        List<int>[] adj,
        List<int>[] predAdj,
        int[] inDegree)
        where TMask : unmanaged, IBitSet<TMask>
    {
        for (int writer = 0; writer < systems.Length; writer++)
        {
            for (int reader = 0; reader < systems.Length; reader++)
            {
                if (writer == reader) continue;
                if (!systems[writer].WriteMask.ContainsAny(systems[reader].FreshReadMask)) continue;
                adj[writer].Add(reader);
                predAdj[reader].Add(writer);
                inDegree[reader]++;
            }
        }
    }
}
