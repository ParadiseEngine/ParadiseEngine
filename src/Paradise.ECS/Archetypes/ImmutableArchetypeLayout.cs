using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.ECS;

/// <summary>
/// Header portion of the archetype layout data.
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
[StructLayout(LayoutKind.Sequential)]
public struct ArchetypeLayoutHeader<TMask> where TMask : unmanaged, IBitSet<TMask>
{
    /// <summary>Maximum entities that fit in a single chunk for this archetype.</summary>
    public int EntitiesPerChunk;

    /// <summary>Number of component types in this archetype.</summary>
    public int ComponentCount;

    /// <summary>Minimum component ID in this archetype (for offset calculation).</summary>
    public int MinComponentId;

    /// <summary>Maximum component ID in this archetype.</summary>
    public int MaxComponentId;

    /// <summary>Byte offset to BaseOffsets array (short[]) relative to data start.</summary>
    public int BaseOffsetsOffset;

    /// <summary>Bytes reserved at the END of every chunk of this archetype for per-chunk
    /// aggregates. Zero unless some component asked for one.</summary>
    public int ChunkAggregateBytes;

    /// <summary>The component mask for this archetype.</summary>
    public TMask ComponentMask;

    /// <summary>Size of this header in bytes.</summary>
    public static readonly int SizeInBytes = Unsafe.SizeOf<ArchetypeLayoutHeader<TMask>>();
}

/// <summary>
/// Describes the memory layout of component data within a chunk for a specific archetype.
/// Uses Struct of Arrays (SoA) layout where each component type has a contiguous array.
/// This provides better cache utilization and SIMD opportunities when iterating components.
/// </summary>
/// <typeparam name="TMask">The component mask type implementing IBitSet.</typeparam>
/// <typeparam name="TConfig">The world configuration type.</typeparam>
/// <remarks>
/// Memory layout example for 100 entities with Position(12B), Velocity(12B), Health(8B):
/// <code>
/// Chunk: [EntityIds×100][Position×100][Velocity×100][Health×100]
///        |----400B-----|---1200B----|---1200B-----|---800B---|
/// </code>
/// Entity IDs (4 bytes each) are stored at the beginning of each chunk.
/// Memory is allocated as a single block containing:
/// <code>
/// [ImmutableArchetypeLayout struct][ArchetypeLayoutHeader&lt;TMask&gt;][BaseOffsets (short[])]
/// </code>
/// BaseOffsets uses short (2 bytes) indexed by (componentId - minComponentId).
/// -1 indicates component not present; valid offsets are 0 to 32767.
/// Component sizes are looked up from the ComponentTypeInfo array.
/// Use <see cref="Create"/> to allocate and <see cref="Free"/> to deallocate.
/// </remarks>
public readonly unsafe ref struct ImmutableArchetypeLayout<TMask, TConfig>
    where TMask : unmanaged, IBitSet<TMask>
    where TConfig : IConfig, new()
{
    private readonly byte* _data;

    /// <summary>
    /// Gets the raw data pointer for this layout.
    /// Used when the layout needs to be stored in a non-ref struct (e.g., generated Queryable Data types).
    /// </summary>
    public nint DataPointer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (nint)_data;
    }

    /// <summary>
    /// Creates an archetype layout view from a data pointer.
    /// </summary>
    /// <param name="data">Pointer to the layout data allocated by <see cref="Create"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ImmutableArchetypeLayout(nint data)
    {
        _data = (byte*)data;
    }

    /// <summary>
    /// Gets the maximum number of entities that fit in a single chunk.
    /// </summary>
    public int EntitiesPerChunk
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Header.EntitiesPerChunk;
    }

    /// <summary>
    /// Byte offset, within a chunk, of the per-chunk aggregate slot reserved for
    /// <paramref name="componentId"/> — or −1 if that component reserved none.
    /// </summary>
    /// <remarks>
    /// Slots are packed into the tail of the chunk in COMPONENT ID ORDER, which is the same order
    /// <c>IBitSet.ForEach</c> walks, so this walk and the reservation above cannot disagree about
    /// who owns which bytes.
    ///
    /// The slot is per CHUNK, not per entity: every entity in the chunk shares it. What lives there
    /// is a summary of the chunk — the union of its entities' tag masks, today — and its point is
    /// that it is inside the chunk, so <c>Archetype.CopyChunksFrom</c> carries it with the data it
    /// describes rather than leaving it to be maintained alongside.
    /// </remarks>
    /// <param name="componentId">The component whose aggregate slot is wanted.</param>
    /// <param name="typeInfos">The registry's type infos — passed in because a layout is a raw
    /// pointer into unmanaged memory and cannot hold a managed array.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetChunkAggregateOffset(
        ComponentId componentId, ImmutableArray<ComponentTypeInfo> typeInfos)
    {
        int reserved = Header.ChunkAggregateBytes;
        if (reserved == 0)
            return -1;

        var action = new FindChunkAggregateOffsetAction
        {
            TypeInfos = typeInfos,
            Target = componentId.Value,
            Offset = TConfig.ChunkSize - reserved,
            Result = -1,
        };
        Header.ComponentMask.ForEach(ref action);
        return action.Result;
    }

    private struct FindChunkAggregateOffsetAction : IBitAction
    {
        public ImmutableArray<ComponentTypeInfo> TypeInfos;
        public int Target;
        public int Offset;
        public int Result;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Invoke(int bitIndex)
        {
            int size = TypeInfos[bitIndex].ChunkAggregateSize;
            if (size == 0) return;
            if (bitIndex == Target) Result = Offset;
            Offset += size;
        }
    }

    /// <summary>
    /// Gets the number of component types in this archetype.
    /// </summary>
    public int ComponentCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Header.ComponentCount;
    }

    /// <summary>
    /// Gets the minimum component ID in this archetype.
    /// </summary>
    public int MinComponentId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Header.MinComponentId;
    }

    /// <summary>
    /// Gets the maximum component ID in this archetype.
    /// </summary>
    public int MaxComponentId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Header.MaxComponentId;
    }

    /// <summary>
    /// Gets the component mask for this archetype.
    /// </summary>
    public TMask ComponentMask
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Header.ComponentMask;
    }

    private ref ArchetypeLayoutHeader<TMask> Header
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Unsafe.AsRef<ArchetypeLayoutHeader<TMask>>(_data);
    }

    /// <summary>
    /// Creates a new archetype layout from a component mask.
    /// Component type information is obtained from the provided type info array.
    /// </summary>
    /// <param name="allocator">The memory allocator to use.</param>
    /// <param name="typeInfos">The component type information array.</param>
    /// <param name="componentMask">The component mask defining which components are in this archetype.</param>
    /// <returns>A pointer to the allocated data (as nint). Use <see cref="Free"/> to deallocate.</returns>
    public static nint Create(
        IAllocator allocator,
        ImmutableArray<ComponentTypeInfo> typeInfos,
        TMask componentMask)
    {
        // Calculate min/max component ID range using FirstSetBit/LastSetBit
        int minId = componentMask.FirstSetBit();
        int maxId = componentMask.LastSetBit();

        // Handle empty archetype
        if (minId < 0)
        {
            minId = 0;
            maxId = -1; // Results in componentSlots = 0
        }

        int componentSlots = maxId - minId + 1;
        int componentCount = componentMask.PopCount();

        // Layout: [Header<TMask>][BaseOffsets (short[])]
        int baseOffsetsOffset = Memory.AlignUp(ArchetypeLayoutHeader<TMask>.SizeInBytes, sizeof(ushort));
        int totalBytes = baseOffsetsOffset + componentSlots * sizeof(ushort);

        byte* data = (byte*)allocator.Allocate((nuint)totalBytes);

        // Initialize header
        ref var header = ref Unsafe.AsRef<ArchetypeLayoutHeader<TMask>>(data);
        header.ComponentCount = componentCount;
        header.MinComponentId = minId;
        header.MaxComponentId = maxId;
        header.BaseOffsetsOffset = baseOffsetsOffset;
        header.ComponentMask = componentMask;

        // Initialize baseOffsets to -1 (0xFFFF in two's complement, so InitBlock with 0xFF works)
        short* baseOffsets = (short*)(data + baseOffsetsOffset);
        Unsafe.InitBlock(baseOffsets, 0xFF, (uint)(componentSlots * sizeof(short)));

        InitializeLayout(data, typeInfos, componentMask);
        return (nint)data;
    }

    private static void InitializeLayout(
        byte* data,
        ImmutableArray<ComponentTypeInfo> typeInfos,
        TMask componentMask)
    {
        ref var header = ref Unsafe.AsRef<ArchetypeLayoutHeader<TMask>>(data);
        int minId = header.MinComponentId;
        int componentSlots = header.MaxComponentId - minId + 1;
        var baseOffsets = new Span<short>((short*)(data + header.BaseOffsetsOffset), componentSlots);

        // Empty archetype still needs entity ID storage
        if (componentMask.IsEmpty)
        {
            header.EntitiesPerChunk = TConfig.ChunkSize / TConfig.EntityIdByteSize;
            return;
        }

        // Per-chunk aggregates live in the TAIL of the chunk, and everything below simply carves a
        // smaller chunk. Placing them last is what keeps this change invisible: every column offset
        // and every entity-id read is measured from offset 0 as before, so only the capacity moves.
        var aggregateAction = new SumChunkAggregatesAction { TypeInfos = typeInfos, TotalSize = 0 };
        componentMask.ForEach(ref aggregateAction);
        header.ChunkAggregateBytes = aggregateAction.TotalSize;
        int usableChunkSize = TConfig.ChunkSize - aggregateAction.TotalSize;

        // Calculate total size per entity (entity ID + components, without alignment)
        var sumAction = new SumComponentSizesAction { TypeInfos = typeInfos, TotalSize = TConfig.EntityIdByteSize };
        componentMask.ForEach(ref sumAction);
        int totalSizePerEntity = sumAction.TotalSize;

        // Tag-only archetype: only entity IDs
        if (totalSizePerEntity == TConfig.EntityIdByteSize)
        {
            header.EntitiesPerChunk = usableChunkSize / TConfig.EntityIdByteSize;
            // Mark all tag components as present (offset after entity IDs, but size 0)
            var zeroAction = new SetZeroOffsetsAction { BaseOffsets = baseOffsets, MinId = minId };
            componentMask.ForEach(ref zeroAction);
            return;
        }

        // Start with optimistic estimate
        int entitiesPerChunk = usableChunkSize / totalSizePerEntity;
        if (entitiesPerChunk == 0)
            entitiesPerChunk = 1;

        // Calculate base offsets and verify fit, adjusting entitiesPerChunk if needed
        while (entitiesPerChunk > 1)
        {
            int totalSize = CalculateAndStoreOffsets(componentMask, typeInfos, baseOffsets, minId, entitiesPerChunk);
            if (totalSize <= usableChunkSize)
                break;
            entitiesPerChunk--;
        }

        // Final calculation with the determined entitiesPerChunk
        if (entitiesPerChunk == 1)
        {
            CalculateAndStoreOffsets(componentMask, typeInfos, baseOffsets, minId, entitiesPerChunk);
        }

        header.EntitiesPerChunk = entitiesPerChunk;
    }

    private static int CalculateAndStoreOffsets(
        TMask componentMask,
        ImmutableArray<ComponentTypeInfo> typeInfos,
        Span<short> baseOffsets,
        int minId,
        int entitiesPerChunk)
    {
        // Start after entity ID array (aligned to 4 bytes, which entity IDs naturally are)
        var action = new CalculateOffsetsAction
        {
            TypeInfos = typeInfos,
            BaseOffsets = baseOffsets,
            MinId = minId,
            EntitiesPerChunk = entitiesPerChunk,
            CurrentOffset = entitiesPerChunk * TConfig.EntityIdByteSize
        };
        componentMask.ForEach(ref action);
        return action.CurrentOffset;
    }

    private struct SumComponentSizesAction : IBitAction
    {
        public ImmutableArray<ComponentTypeInfo> TypeInfos;
        public int TotalSize;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Invoke(int bitIndex)
        {
            TotalSize += TypeInfos[bitIndex].Size;
        }
    }

    /// <summary>Sums the per-chunk reservations of an archetype's components, and records where
    /// each one's slot begins — measured back from the end of the chunk, so nothing a chunk
    /// already contains has to move.</summary>
    private struct SumChunkAggregatesAction : IBitAction
    {
        public ImmutableArray<ComponentTypeInfo> TypeInfos;
        public int TotalSize;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Invoke(int bitIndex)
        {
            TotalSize += TypeInfos[bitIndex].ChunkAggregateSize;
        }
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Required by IBitAction interface")]
    private ref struct SetZeroOffsetsAction : IBitAction
    {
        public Span<short> BaseOffsets;
        public int MinId;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Invoke(int bitIndex)
        {
            BaseOffsets[bitIndex - MinId] = 0;
        }
    }

    private ref struct CalculateOffsetsAction : IBitAction
    {
        public ImmutableArray<ComponentTypeInfo> TypeInfos;
        public Span<short> BaseOffsets;
        public int MinId;
        public int EntitiesPerChunk;
        public int CurrentOffset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Invoke(int bitIndex)
        {
            var comp = TypeInfos[bitIndex];
            int slotIndex = bitIndex - MinId;

            // Tag components (size 0) have offset 0
            if (comp.Size == 0)
            {
                BaseOffsets[slotIndex] = 0;
                return;
            }

            // Align the base offset
            int alignment = comp.Alignment > 0 ? comp.Alignment : 1;
            CurrentOffset = Memory.AlignUp(CurrentOffset, alignment);

            BaseOffsets[slotIndex] = (short)CurrentOffset;
            CurrentOffset += comp.Size * EntitiesPerChunk;
        }
    }

    /// <summary>
    /// Gets the base offset for a component's array within the chunk.
    /// </summary>
    /// <param name="componentId">The component ID.</param>
    /// <returns>The base offset, or -1 if the component is not in this archetype.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetBaseOffset(ComponentId componentId)
    {
        int id = componentId.Value;
        ref var header = ref Header;
        if (id < header.MinComponentId || id > header.MaxComponentId)
            return -1;

        int slotIndex = id - header.MinComponentId;
        short offset = ((short*)(_data + header.BaseOffsetsOffset))[slotIndex];
        return offset; // -1 indicates not present, valid offsets return >= 0
    }

    /// <summary>
    /// Checks if this archetype contains the specified component.
    /// </summary>
    /// <param name="componentId">The component ID.</param>
    /// <returns>True if the component is present.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasComponent(ComponentId componentId)
    {
        return ComponentMask.Get(componentId.Value);
    }

    /// <summary>
    /// Calculates the byte offset for a specific entity's ID within the chunk.
    /// Entity IDs are stored at the beginning of the chunk.
    /// </summary>
    /// <param name="entityIndex">The entity's index within the chunk.</param>
    /// <returns>The byte offset from chunk start for this entity's ID.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetEntityIdOffset(int entityIndex)
    {
        return entityIndex * TConfig.EntityIdByteSize;
    }

    /// <summary>
    /// Reads the entity ID at the specified index from a chunk's raw bytes.
    /// Handles variable entity ID byte sizes (1, 2, or 4 bytes).
    /// </summary>
    /// <param name="bytes">The chunk's raw byte span.</param>
    /// <param name="entityIndex">The entity's index within the chunk.</param>
    /// <returns>The entity ID.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadEntityId(Span<byte> bytes, int entityIndex)
    {
        int offset = entityIndex * TConfig.EntityIdByteSize;
        return TConfig.EntityIdByteSize switch
        {
            1 => bytes.GetRef<byte>(offset),
            2 => bytes.GetRef<ushort>(offset),
            4 => bytes.GetRef<int>(offset),
            _ => ThrowHelper.ThrowInvalidEntityIdByteSize<int>(TConfig.EntityIdByteSize)
        };
    }

    /// <summary>
    /// Frees the memory allocated for an archetype layout.
    /// </summary>
    /// <param name="allocator">The allocator that was used to create the layout.</param>
    /// <param name="data">The data pointer returned by <see cref="Create"/>.</param>
    public static void Free(IAllocator allocator, nint data)
    {
        if (data != 0)
        {
            allocator.Free((void*)data);
        }
    }
}
