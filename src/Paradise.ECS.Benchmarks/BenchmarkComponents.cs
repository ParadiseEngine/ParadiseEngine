namespace Paradise.ECS.Benchmarks;

[System.Runtime.InteropServices.Guid("EAEEB062-5E8F-4B6A-B3B6-1F60393B3547")]
[Component(Id = 0)]
public partial struct BenchPosition
{
    public float X, Y, Z;
}

[System.Runtime.InteropServices.Guid("897422E5-00F8-47FE-BB9B-9035DD17E0A3")]
[Component(Id = 1)]
public partial struct BenchVelocity
{
    public float X, Y, Z;
}
