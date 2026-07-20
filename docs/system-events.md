# Deterministic System Events

> Design spec for the event primitive in `Paradise.ECS`. Status: **design locked, implementation staged.**

## 1. Motivation

Systems today can communicate only through components. That forces every "X happened → N
things react" signal into one of three ad-hoc shapes (observed in the ImmortalCultivation
game core):

- **per-entity boolean flags** (`NpcJustDied`, `NpcJustSpawned`) — permanent components on a
  fixed archetype, 99.99% zero, scanned every tick;
- **single-slot intent/outcome bags** — one in-flight message per feature, no fan-out;
- **the managed post-pass** — every reaction funnels into one broad serial method.

An **event system** generalizes the `EntityCommandBuffer`. The ECB is already an event buffer:
*append typed records to an off-entity buffer, process them at a defined point in deterministic
order.* It is just the special case where (a) the record set is fixed to structural ops, (b)
there is one built-in consumer (playback), and (c) the buffer is **transient** — drained before
the tick ends, so it never needs snapshotting.

`SystemEvents` lifts all three restrictions: arbitrary unmanaged event types, any number of
consumers (read-many fan-out), and — because consumers are *systems that run next frame* rather
than a same-tick playback — the buffer is **durable world state** that participates in the
snapshot.

## 2. The model (locked)

### 2.1 One rule: every event is one-frame-deferred world state

No barriers inside a frame. Every system runs in one fully-parallel wave. An event **appended
in frame N is visible to consumers in frame N+1** — never the same frame. There is no
"transient, same-tick" event class; that would require an intra-wave barrier, which this model
forbids by construction.

Consequence: at every tick boundary, the events produced in frame N are alive (produced,
not-yet-consumed). Alive at a boundary ⇒ **part of world state ⇒ snapshotted and saved.**

### 2.2 Why this is sound under replay

A tick is a pure function `S(N) --inputs(N)--> S(N+1)`. Events are *inputs and outputs of that
function*, not hidden intermediates:

- The **incoming** events (produced N−1) are part of `S(N)` — read by systems this frame.
- The **outgoing** events (produced N) are part of `S(N+1)` — the function's output.

So determinism needs only what determinism always needs: an identical starting state and a
deterministic tick. The event buffer is *in* the starting state, which is exactly why it must be
snapshotted (unlike the ECB, whose buffer is empty at the boundary because its consumer ran
same-tick).

### 2.3 Reads never conflict with writes → zero scheduling edges

Within frame N:

- reads bind to the **read world** (the immutable previous-tick snapshot) → incoming events N−1;
- writes go to a fresh **outgoing** buffer → events N.

These are physically different buffers, so an event read can never alias an event write. Events
therefore introduce **no read/write mask entries and no DAG edges** — every system can read and
write events in the same fully-parallel wave with no ordering constraint. This is the property
that makes "all systems parallel, no restriction" hold.

## 3. Determinism of the merge (mirror the ECB exactly)

Under a parallel wave, many systems append concurrently. The *order* events land in next
frame's incoming buffer determines consumption order, so it must be deterministic. Reuse the
ECB's proven mechanism verbatim:

- Each **work item** carries its own event write buffer, **rented from a pool in schedule
  order** (`SystemEventBufferPool`, sibling of `EntityCommandBufferPool`).
- A work item runs on exactly one thread and appends sequentially → each buffer is internally
  ordered.
- After all waves complete, the schedule **commits** the per-work-item buffers into the world's
  outgoing store **in rent order = schedule order** (sibling of `EntityCommandBufferPool.PlaybackAll`).

Result: `ParallelWaveScheduler` and `SequentialWaveScheduler` produce a bit-identical outgoing
event buffer, independent of thread count — the same guarantee the ECB already gives for entity
IDs.

## 4. Data structures

### 4.1 `SystemEvents<T>` — the per-type buffer (world-owned)

```
sealed class SystemEvents<T> where T : unmanaged   // one per event type, owned by the World
    ReadOnlySpan<T> Incoming { get; }   // events produced last frame; read by systems this frame
    // outgoing is the schedule's concern (per-work-item buffers), committed here post-wave
    void CommitOutgoing(spans in schedule order)    // replaces Incoming for next frame
    void CopyFrom(SystemEvents<T> source)           // bit-copy of the incoming region
```

- Backing store: a single contiguous `T[]`/native region (not pooled segments — must be
  bit-copyable for `CopyFrom` and serialization). Fixed capacity per type (`[SystemEvent(InitCapacity)]`),
  grow-by-realloc on overflow in DEBUG with a diagnostic; volumes are tiny in practice.
- No ring / no `lastingFrames`: every event lives exactly one frame (produced N, consumed N+1,
  gone). The double-buffering is provided by the read-world/write-world split, not an internal ring.

### 4.2 `WorldEventStore` — the set of typed buffers on the World

`World<TMask,TConfig>` gains one field: `WorldEventStore _events`. It holds the registered event
types' `SystemEvents<T>` buffers. `World.CopyFrom` copies it alongside `_entityManager` and
`_archetypeRegistry` — that is the single line that puts events into the snapshot.

Event types are registered like components (generated id + `Size` + `InitCapacity`), so the
store is a fixed-size array indexed by event-type id (AOT-friendly, no per-tick `Dictionary`).

Two public managed entry points on the store (both call on the world's owner thread only):

```
ReadOnlySpan<T> Incoming<T>()      // read the previous frame's events (read-many)
void            SetIncoming<T>(…)  // 0.5.1: re-seed incoming on load (a mid-window save)
void            Emit<T>(in T e)    // 0.5.2: MANAGED emit — the non-system sibling of Append
```

`Emit<T>` lets non-system managed code (a command handler, a host pre-pass) put an event on the
bus. It stages into a store-owned buffer that `Commit` dispatches **after** all per-work-item
system writers (a fixed, deterministic position in the merge), then clears. Called in a pre-schedule
pass, a managed emit therefore commits with that tick's wave and is delivered one frame later with
identical semantics to a system emit. It is NOT thread-safe and must run on the owner thread and
outside a schedule run — the same contract as `world.GetComponent<T>().Value = …`; the managed
staging is transient (drained each commit, reset by `CopyFrom`/`Clear`), so it never enters the
snapshot.

### 4.3 `SystemEventWriter` — the injected write handle (mirror `EntityCommandBuffer`)

One non-generic type with a generic append, exactly like the ECB's `AddComponent<T>`:

```
sealed class SystemEventWriter
    void Append<T>(in T e) where T : unmanaged   // records into this work item's buffer
```

Per work item, rented in schedule order. Committed post-wave.

### 4.4 `SystemEventReader` — the injected read handle

```
readonly struct SystemEventReader
    ReadOnlySpan<T> Read<T>() where T : unmanaged   // incoming (previous-frame) events
```

Binds to the **read world's** store in snapshot mode (previous-tick snapshot), or the write
world under classic `Run()`.

## 5. Generator integration (small, mirrors existing kinds)

Two new `FieldKind`s in `SystemGenerator`, both modeled on the existing `CommandBuffer` kind:

- Field of type `Paradise.ECS.SystemEventWriter` → `FieldKind.EventWriter`; dispatcher passes the
  work item's writer (exactly like `commands`).
- Field of type `Paradise.ECS.SystemEventReader` → `FieldKind.EventReader`; dispatcher passes a
  reader over `readWorld` (snapshot mode) or `world` (classic).

Neither kind contributes to read/write masks (§2.3), so **no DAG/conflict changes** — the
smallest possible generator surface. `IWorldSystem` accepts both kinds too (add to the allow-list
next to `CommandBuffer`).

## 6. Frame lifecycle

```
tick N  (SnapshotLoop):
  write = pool.Rent();  write.CopyFrom(current)       // events copied too (incoming N-1)
  schedule.Run(readWorld: current):
     wave: every system runs in parallel
        - SystemEventReader.Read<T>()  → current.Incoming<T>  (events N-1)      [read world]
        - SystemEventWriter.Append<T>() → this work item's buffer               [no contention]
     post-wave commit: write.Events.CommitOutgoing(perWorkItem buffers in schedule order)
                       → write.Incoming<T> now = events N   (replaces the N-1 copy)
     ECB playback (unchanged)
  publish write                                        // snapshot's Incoming = events N
tick N+1: reads write.Incoming = events N. ✓
```

- The N−1 copy that `CopyFrom` produced in `write` is overwritten by the commit; on a paused
  tick (no `Run`), it is left intact, which is correct (events persist across a pause).
- **Save** persists a published world's `Incoming` set; reload resumes with the correct
  incoming events for the next tick. Bounded and usually empty.

## 7. Consumption semantics

- **Read-many:** any number of systems read `Incoming<T>` for the same type in frame N; reads
  are non-destructive. This is the fan-out (one `NpcDied` → reputation, faction, rumor, chronicle
  reactors, each an independent system).
- **Auto-expire:** `Incoming` is wholesale replaced each frame by the commit; an event that is
  produced and not consumed simply vanishes after its one frame. No manual drain.
- **Exactly-once effects stay single-writer:** an event may fan out to N *readers*, but any
  shared/structural mutation it triggers still has ONE owner system (or the ECB). Events do not
  loosen PECS3008; they are the sanctioned way to *notify* across the single-writer boundary.

## 8. Testing

- **Determinism:** same events under `SequentialWaveScheduler` and `ParallelWaveScheduler`
  (16-thread) → identical `Incoming` bytes across many seeds/orders.
- **Snapshot round-trip:** produce events in N; `CopyFrom` a snapshot; assert `Incoming` copied;
  consume in N+1; assert both worlds agree.
- **Cross-frame delivery:** append in N, assert invisible in N, visible in N+1, gone in N+2.
- **Fan-out:** two reader systems both observe one appended event in N+1.
- **Save round-trip (game side):** serialize a world with pending incoming events, reload,
  continue one tick, assert bit-identical to the un-saved run.

## 9. Staged implementation plan — SHIPPED

All stages below are landed and released. Status in brackets.

1. **Runtime core (no generator):** `SystemEvents<T>`, `WorldEventStore`, `SystemEventWriter`,
   `SystemEventReader`, `SystemEventBufferPool`; wire `World.CopyFrom`, `SystemSchedule` commit,
   `WorkItem`. Direct (hand-called) API + unit tests §8(1-4). *Provable without codegen.* **[done, 0.5.0]**
2. **Generator injection:** `FieldKind.EventWriter` / `FieldKind.EventReader`, dispatcher
   emission, `IWorldSystem` allow-list. Analyzer tests. **[done, 0.5.0]**
3. **Event-type registration (OPTIONAL / deferred):** generator-assigned stable ids. NOT
   required for correctness — the engine never serializes event ids; the game's `SaveService`
   persists events through its own typed DTO and re-`Append`s on load, so process-local ids are
   consistent within any single run (determinism holds per-process). Only needed if raw engine
   ids were ever written to disk, which they are not. **[not needed — confirmed by the game migration]**
4. **Pack `Paradise.ECS` 0.5.0**; bump the game. **[done — published to nuget.org]**
5. **Game migration:** `NpcDied` fan-out replaces the `NpcJustDied` flag path; save round-trip
   §8(5); docs. **[done]**
6. **`SetIncoming<T>` restore API (0.5.1):** `WorldEventStore.SetIncoming<T>(ReadOnlySpan<T>)`
   replaces a type's incoming buffer so a host can re-seed events that were in-flight when a save
   was taken mid-window; carried by `CopyFrom`, covered by set → read-back → snapshot tests. **[done, 0.5.1]**
7. **`Emit<T>` managed-emit API (0.5.2):** `WorldEventStore.Emit<T>(in T)` — the non-system sibling
   of `SystemEventWriter.Append`, so managed code (command handlers, host passes) can put events on
   the bus. Staged store-side, dispatched by `Commit` after all system writers (deterministic),
   transient. Enables reactors whose *producers* are managed, not systems. **[done, 0.5.2]**

**Consumer status.** The bus went well past the `NpcDied` pilot: the immortal-cultivation game
moved *all eight* rng/trade feature outcomes onto it — each feature system now emits a `*Resolved`
event instead of writing an `*Outcome` component, the sole player-state owner system applies the
numeric deltas from those events, and a thin managed drain turns the same events into narration.
`SetIncoming` (stage 6) is what lets a save taken between roll and application round-trip exactly.

## 10. Relationship to the ECB (the subset)

Both are append-in-wave, commit-in-schedule-order buffers. The ECB is **not** snapshotted and
`SystemEvents` **is** — because the ECB's consumer (playback) runs same-tick and empties it at
the boundary, whereas an event's consumers are next-frame systems, so the buffer is alive at the
boundary. Same primitive, differing only in *who consumes and when*; that single difference
decides snapshot participation.
