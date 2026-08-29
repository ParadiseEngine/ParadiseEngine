namespace Paradise.ECS.Concurrent.Test;

/// <summary>
/// Test components for unit testing with explicit manual IDs.
/// </summary>
[System.Runtime.InteropServices.Guid("5B9313BE-CB77-4C8B-A0E4-82A3B369C717")]
[Component(Id = 0)]
public partial struct TestHealth
{
    public int Current;
    public int Max;
}

[System.Runtime.InteropServices.Guid("B6170E3B-FEE1-4C16-85C9-B5130A253BAC")]
[Component(Id = 1)]
public partial struct TestPosition
{
    public float X, Y, Z;
}

[System.Runtime.InteropServices.Guid("1040E96A-7D4A-4241-BDE1-36D4DBFCF7C0")]
[Component(Id = 2)]
public partial struct TestVelocity
{
    public float X, Y, Z;
}

[System.Runtime.InteropServices.Guid("A7B3C4D5-E6F7-4890-ABCD-1234567890AB")]
[Component(Id = 3)]
public partial struct TestTag;

[System.Runtime.InteropServices.Guid("B8C4D5E6-F7A8-4901-BCDE-2345678901BC")]
[Component(Id = 4)]
public partial struct TestDamage
{
    public int Amount;
}
