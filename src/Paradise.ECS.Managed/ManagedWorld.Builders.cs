namespace Paradise.ECS;

public sealed partial class ManagedWorld<TMask, TConfig, TInner>
{
    public Entity CreateEntity<TBuilder>(TBuilder builder) where TBuilder : unmanaged, IComponentsBuilder
        => ApplyBuilder(default, builder, BuilderOperation.Create);

    public Entity OverwriteEntity<TBuilder>(Entity entity, TBuilder builder) where TBuilder : unmanaged, IComponentsBuilder
        => ApplyBuilder(entity, builder, BuilderOperation.Overwrite);

    public Entity AddComponents<TBuilder>(Entity entity, TBuilder builder) where TBuilder : unmanaged, IComponentsBuilder
        => ApplyBuilder(entity, builder, BuilderOperation.Add);

    private unsafe Entity ApplyBuilder<TBuilder>(Entity entity, TBuilder builder, BuilderOperation operation)
        where TBuilder : unmanaged, IComponentsBuilder
    {
        AssertStructuralChangesAllowed(operation switch
        {
            BuilderOperation.Create => nameof(CreateEntity),
            BuilderOperation.Add => nameof(AddComponents),
            _ => nameof(OverwriteEntity)
        });
        bool sourceHasManaged = operation != BuilderOperation.Create
            && !ArchetypeRegistry.GetById(GetLocation(entity).ArchetypeId)!.Layout.ComponentMask.And(ManagedSlotMask).IsEmpty;
        var mask = TMask.Empty;
        builder.CollectTypes(ref mask);
        if (mask.And(ManagedSlotMask).IsEmpty && !sourceHasManaged)
            return ApplyPreparedBuilder(entity, builder, operation);

        // Run arbitrary builder code once, before mutation, then replay only its validated data.
        var layout = ArchetypeRegistry.GetOrCreate((HashedKey<TMask>)mask).Layout;
        var scratch = ChunkManager.Allocate();
        try
        {
            var bytes = ChunkManager.GetBytes(scratch);
            bytes.Clear();
            var preserved = TMask.Empty;
            if (operation == BuilderOperation.Add)
            {
                var location = GetLocation(entity);
                var source = ArchetypeRegistry.GetById(location.ArchetypeId)!;
                preserved = source.Layout.ComponentMask.And(ManagedSlotMask);
                var (chunkIndex, index) = source.GetChunkLocation(location.GlobalIndex);
                var sourceBytes = ChunkManager.GetBytes(source.GetChunk(chunkIndex));
                for (int id = layout.MinComponentId; id <= layout.MaxComponentId; id++)
                {
                    var componentId = new ComponentId(id);
                    if (!mask.Get(id) || IsManagedSlot(componentId) || !source.Layout.HasComponent(componentId))
                        continue;
                    int size = TypeInfos[id].Size;
                    if (size > 0)
                        sourceBytes.Slice(source.Layout.GetBaseOffset(componentId) + index * size, size)
                            .CopyTo(bytes.Slice(layout.GetBaseOffset(componentId), size));
                }
            }

            builder.WriteComponents(ChunkManager, layout, scratch, 0);
            foreach (var info in _managedTypes)
            {
                if (layout.HasComponent(info.SlotTypeId))
                    ValidateRawSlot(info.SlotTypeId, bytes.Slice(layout.GetBaseOffset(info.SlotTypeId), sizeof(int)));
            }

            fixed (byte* sourceBytes = bytes)
            fixed (ComponentTypeInfo* typeInfos = TypeInfos.AsSpan())
            {
                var staged = new StagedBuilder(mask, preserved, layout.DataPointer, sourceBytes, typeInfos);
                return ApplyPreparedBuilder(entity, staged, operation);
            }
        }
        finally
        {
            ChunkManager.Free(scratch);
        }
    }

    private Entity ApplyPreparedBuilder<TBuilder>(Entity entity, TBuilder builder, BuilderOperation operation)
        where TBuilder : unmanaged, IComponentsBuilder
    {
        if (operation == BuilderOperation.Create)
        {
            var created = Inner.CreateEntity(builder);
            ZeroHandles(created);
            return created;
        }

        var location = GetLocation(entity);
        var source = ArchetypeRegistry.GetById(location.ArchetypeId)!;
        Span<int> handles = stackalloc int[_managedTypes.Length];
        if (operation == BuilderOperation.Overwrite)
            CaptureHandles(source, location.GlobalIndex, handles);
        var result = operation == BuilderOperation.Add
            ? Inner.AddComponents(entity, builder)
            : Inner.OverwriteEntity(entity, builder);
        if (operation == BuilderOperation.Overwrite)
        {
            ReleaseHandles(handles);
            ZeroHandles(entity);
        }
        ZeroTail(source);
        return result;
    }

    private enum BuilderOperation
    {
        Create,
        Add,
        Overwrite
    }

    private readonly unsafe struct StagedBuilder(TMask mask, TMask preserved, nint layoutPointer,
        byte* sourceBytes, ComponentTypeInfo* typeInfos) : IComponentsBuilder
    {
        public void CollectTypes<TM>(ref TM targetMask) where TM : unmanaged, IBitSet<TM>
        {
            var layout = new ImmutableArchetypeLayout<TMask, TConfig>(layoutPointer);
            for (int id = layout.MinComponentId; id <= layout.MaxComponentId; id++)
            {
                if (mask.Get(id))
                    targetMask = targetMask.Set(id);
            }
        }

        public void WriteComponents<TM, TC, TChunkManager>(TChunkManager chunkManager,
            ImmutableArchetypeLayout<TM, TC> layout, ChunkHandle chunkHandle, int indexInChunk)
            where TM : unmanaged, IBitSet<TM>
            where TC : IConfig, new()
            where TChunkManager : IChunkManager
        {
            var sourceLayout = new ImmutableArchetypeLayout<TMask, TConfig>(layoutPointer);
            var targetBytes = chunkManager.GetBytes(chunkHandle);
            for (int id = sourceLayout.MinComponentId; id <= sourceLayout.MaxComponentId; id++)
            {
                if (!mask.Get(id) || preserved.Get(id))
                    continue;
                int size = typeInfos[id].Size;
                if (size == 0)
                    continue;
                var componentId = new ComponentId(id);
                new ReadOnlySpan<byte>(sourceBytes + sourceLayout.GetBaseOffset(componentId), size)
                    .CopyTo(targetBytes.Slice(layout.GetBaseOffset(componentId) + indexInChunk * size, size));
            }
        }
    }
}
