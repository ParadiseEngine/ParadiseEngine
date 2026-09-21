namespace Paradise.ECS;

/// <summary>Provides tag presence operations independently of the world's mask and configuration.</summary>
public interface ITagWorld
{
    void AddTag<TTag>(Entity entity) where TTag : ITag;
    bool HasTag<TTag>(Entity entity) where TTag : ITag;
    void RemoveTag<TTag>(Entity entity) where TTag : ITag;
}
