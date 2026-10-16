using System;

namespace Paradise.BLOB;

public class ValuePositionBuilder : IBuilder
{
    public int DataPosition { get; set; }
    public int DataSize { get; set; }
    public int PatchPosition { get; set; }
    public int PatchSize { get; set; }
    
    public void Build(IBlobStream stream)
    {
        // This records an existing value's positions for references; it cannot emit a value.
        throw new NotSupportedException();
    }
}

public class ValuePositionBuilder<T> : ValuePositionBuilder, IBuilder<T> where T : unmanaged {}
