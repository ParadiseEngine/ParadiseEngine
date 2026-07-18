namespace Paradise.ECS;

/// <summary>
/// DAG scheduler for SNAPSHOT-READ execution (<c>SystemSchedule.Run(readWorld)</c> with
/// <c>[assembly: SnapshotReadSystems]</c> codegen): read-only fields bind to the immutable
/// previous-tick world, so read-vs-write conflicts cannot alias and only <b>write ∩ write</b>
/// overlaps (plus explicit <c>[After]</c>/<c>[Before]</c> edges, plus implicit writer →
/// <c>[CurrentTick]</c>-fresh-reader edges) split systems into waves.
/// With <c>[SingleWriter]</c> components (PECS3008) writes are disjoint by construction and the
/// schedule collapses to a single, fully parallel wave.
///
/// Do NOT use this scheduler with classic single-world <c>Run()</c> semantics — there,
/// same-tick reads alias in-flight writes and require <see cref="DefaultDagScheduler"/>.
/// </summary>
public sealed class SnapshotDagScheduler : IDagScheduler
{
    /// <inheritdoc/>
    public int[][] ComputeWaves<TMask>(ReadOnlySpan<SystemMetadata<TMask>> systems)
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

        // [CurrentTick] fresh reads bind to the WRITE world even in snapshot mode: a fresh
        // reader must observe same-tick writes, so every system writing a fresh-read component
        // becomes an implicit predecessor — the reader lands in a strictly LATER wave.
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

        // Greedy wave assignment: earliest wave respecting deps, then bump on write-write conflicts
        var waveOf = new int[n];
        for (int i = 0; i < n; i++) waveOf[i] = -1;

        var waveLists = new List<List<int>>();
        foreach (var node in topoOrder)
        {
            int wave = 0;
            foreach (var pred in predAdj[node])
            {
                if (waveOf[pred] >= 0)
                    wave = Math.Max(wave, waveOf[pred] + 1);
            }

            while (true)
            {
                while (waveLists.Count <= wave) waveLists.Add(new List<int>());

                bool hasConflict = false;
                foreach (var other in waveLists[wave])
                {
                    if (HasWriteConflict(systems[node], systems[other]))
                    {
                        hasConflict = true;
                        break;
                    }
                }

                if (!hasConflict) break;
                wave++;
            }

            while (waveLists.Count <= wave) waveLists.Add(new List<int>());
            waveLists[wave].Add(node);
            waveOf[node] = wave;
        }

        var waves = new int[waveLists.Count][];
        for (int w = 0; w < waveLists.Count; w++)
            waves[w] = waveLists[w].ToArray();
        return waves;
    }

    /// <summary>Snapshot mode: reads bind to the immutable world and can never alias writes —
    /// only overlapping WRITE sets (or a write overlapping a <c>[CurrentTick]</c> fresh read)
    /// force separate waves.</summary>
    private static bool HasWriteConflict<TMask>(SystemMetadata<TMask> a, SystemMetadata<TMask> b)
        where TMask : unmanaged, IBitSet<TMask>
        => a.WriteMask.ContainsAny(b.WriteMask)
        || a.WriteMask.ContainsAny(b.FreshReadMask)
        || b.WriteMask.ContainsAny(a.FreshReadMask);

    /// <summary>Adds implicit writer → fresh-reader dependency edges: any system whose write set
    /// overlaps another system's <see cref="SystemMetadata{TMask}.FreshReadMask"/> must run
    /// first, so the CurrentTick reader observes the same-tick write. Mutually fresh-reading
    /// writers form a cycle and are rejected like any other cyclic dependency.</summary>
    internal static void AddFreshReadEdges<TMask>(
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
