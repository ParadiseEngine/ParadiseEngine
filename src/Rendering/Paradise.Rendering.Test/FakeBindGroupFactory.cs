using System;
using System.Collections.Generic;

namespace Paradise.Rendering.Test;

internal sealed class FakeBindGroupFactory : IBindGroupFactory
{
    private uint _next = 1;

    public Dictionary<BindGroupHandle, BindGroupDesc> Groups { get; } = [];
    public int Created { get; private set; }

    public BindGroupHandle CreateBindGroup(in BindGroupDesc desc)
    {
        var handle = new BindGroupHandle(_next++, 1);
        Groups.Add(handle, desc);
        Created++;
        return handle;
    }

    public void DestroyBindGroup(BindGroupHandle handle)
    {
        if (!Groups.Remove(handle)) throw new InvalidOperationException("Bind group destroyed twice.");
    }
}
