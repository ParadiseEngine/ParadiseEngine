namespace Paradise.BT.Test;

public sealed class BlackboardTests
{
    private struct TestData
    {
        public int Value;
    }

    private struct OtherData
    {
        public float X;
    }

    private sealed class TestService
    {
        public string Name { get; set; } = "";
    }

    private sealed class OtherService
    {
        public int Id { get; set; }
    }

    // ============================
    // Named values
    // ============================

    [Test]
    public async Task Named_And_Typed_Values_Can_Be_Read_Back()
    {
        var blackboard = new Blackboard();
        var cooldown = TimeSpan.FromSeconds(2);

        blackboard.Set("shots", 3);
        blackboard.SetData(cooldown);

        await Assert.That(blackboard.Get<int>("shots")).IsEqualTo(3);
        await Assert.That(blackboard.GetData<TimeSpan>()).IsEqualTo(cooldown);
        await Assert.That(blackboard.Has("shots")).IsTrue();
        await Assert.That(blackboard.HasData<TimeSpan>()).IsTrue();
    }

    [Test]
    public async Task TryGet_Returns_False_For_Missing_Value()
    {
        var blackboard = new Blackboard();
        bool found = blackboard.TryGet("missing", out int value);

        await Assert.That(found).IsFalse();
        await Assert.That(value).IsEqualTo(0);
    }

    [Test]
    public async Task TryGet_Returns_True_For_Existing_Value()
    {
        var blackboard = new Blackboard();
        blackboard.Set("health", 100);

        bool found = blackboard.TryGet("health", out int value);

        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo(100);
    }

    [Test]
    public async Task Has_Returns_False_For_Missing_Key()
    {
        var blackboard = new Blackboard();

        await Assert.That(blackboard.Has("nonexistent")).IsFalse();
    }

    [Test]
    public async Task Remove_Named_Value_Returns_True_And_Removes()
    {
        var blackboard = new Blackboard();
        blackboard.Set("key", 42);

        bool removed = blackboard.Remove("key");

        await Assert.That(removed).IsTrue();
        await Assert.That(blackboard.Has("key")).IsFalse();
    }

    [Test]
    public async Task Remove_Missing_Named_Value_Returns_False()
    {
        var blackboard = new Blackboard();

        bool removed = blackboard.Remove("nonexistent");

        await Assert.That(removed).IsFalse();
    }

    [Test]
    public async Task Get_Missing_Named_Value_Throws_KeyNotFoundException()
    {
        var blackboard = new Blackboard();

        KeyNotFoundException? ex = null;
        try
        {
            blackboard.Get<int>("missing");
        }
        catch (KeyNotFoundException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Named_Value_Overwrite_Updates_Value()
    {
        var blackboard = new Blackboard();
        blackboard.Set("score", 10);
        blackboard.Set("score", 20);

        await Assert.That(blackboard.Get<int>("score")).IsEqualTo(20);
    }

    [Test]
    public async Task Named_Value_Can_Store_Null()
    {
        var blackboard = new Blackboard();
        blackboard.Set<string?>("key", null);

        await Assert.That(blackboard.Has("key")).IsTrue();

        bool found = blackboard.TryGet<string?>("key", out string? value);
        await Assert.That(found).IsTrue();
        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task Get_Named_Value_With_Wrong_Type_Throws_InvalidCastException()
    {
        var blackboard = new Blackboard();
        blackboard.Set("key", 42);

        InvalidCastException? ex = null;
        try
        {
            blackboard.Get<string>("key");
        }
        catch (InvalidCastException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task TryGet_With_Wrong_Type_Returns_False()
    {
        var blackboard = new Blackboard();
        blackboard.Set("key", 42);

        bool found = blackboard.TryGet("key", out string? value);

        await Assert.That(found).IsFalse();
        await Assert.That(value).IsNull();
    }

    // ============================
    // Typed struct data
    // ============================

    [Test]
    public async Task SetData_And_GetData_Round_Trips()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(new TestData { Value = 99 });

        await Assert.That(blackboard.GetData<TestData>().Value).IsEqualTo(99);
    }

    [Test]
    public async Task HasData_Generic_Returns_True_After_Set()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(new TestData { Value = 1 });

        await Assert.That(blackboard.HasData<TestData>()).IsTrue();
    }

    [Test]
    public async Task HasData_Generic_Returns_False_Before_Set()
    {
        var blackboard = new Blackboard();

        await Assert.That(blackboard.HasData<TestData>()).IsFalse();
    }

    [Test]
    public async Task HasData_ByType_Returns_True_After_Set()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(new TestData());

        await Assert.That(blackboard.HasData(typeof(TestData))).IsTrue();
    }

    [Test]
    public async Task HasData_ByType_Returns_False_Before_Set()
    {
        var blackboard = new Blackboard();

        await Assert.That(blackboard.HasData(typeof(TestData))).IsFalse();
    }

    [Test]
    public async Task GetDataRef_Mutations_Persist()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(new TestData { Value = 10 });

        ref TestData dataRef = ref blackboard.GetDataRef<TestData>();
        dataRef.Value = 42;

        await Assert.That(blackboard.GetData<TestData>().Value).IsEqualTo(42);
    }

    [Test]
    public async Task SetData_Overwrites_Existing_Value()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(new TestData { Value = 1 });
        blackboard.SetData(new TestData { Value = 2 });

        await Assert.That(blackboard.GetData<TestData>().Value).IsEqualTo(2);
    }

    [Test]
    public async Task RemoveData_Returns_True_And_Removes()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(new TestData { Value = 1 });

        bool removed = blackboard.RemoveData<TestData>();

        await Assert.That(removed).IsTrue();
        await Assert.That(blackboard.HasData<TestData>()).IsFalse();
    }

    [Test]
    public async Task RemoveData_Missing_Returns_False()
    {
        var blackboard = new Blackboard();

        bool removed = blackboard.RemoveData<TestData>();

        await Assert.That(removed).IsFalse();
    }

    [Test]
    public async Task GetData_Missing_Throws_KeyNotFoundException()
    {
        var blackboard = new Blackboard();

        KeyNotFoundException? ex = null;
        try
        {
            blackboard.GetData<TestData>();
        }
        catch (KeyNotFoundException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Multiple_Typed_Data_Types_Coexist()
    {
        var blackboard = new Blackboard();
        blackboard.SetData(new TestData { Value = 1 });
        blackboard.SetData(new OtherData { X = 3.14f });

        await Assert.That(blackboard.GetData<TestData>().Value).IsEqualTo(1);
        await Assert.That(blackboard.GetData<OtherData>().X).IsEqualTo(3.14f);
    }

    // ============================
    // Object data
    // ============================

    [Test]
    public async Task SetObject_And_GetObject_Round_Trips()
    {
        var blackboard = new Blackboard();
        var service = new TestService { Name = "Renderer" };
        blackboard.SetObject(service);

        await Assert.That(blackboard.GetObject<TestService>().Name).IsEqualTo("Renderer");
    }

    [Test]
    public async Task SetObject_Overwrites_Existing_Object()
    {
        var blackboard = new Blackboard();
        blackboard.SetObject(new TestService { Name = "Old" });
        blackboard.SetObject(new TestService { Name = "New" });

        await Assert.That(blackboard.GetObject<TestService>().Name).IsEqualTo("New");
    }

    [Test]
    public async Task RemoveObject_Returns_True_And_Removes()
    {
        var blackboard = new Blackboard();
        blackboard.SetObject(new TestService());

        bool removed = blackboard.RemoveObject<TestService>();

        await Assert.That(removed).IsTrue();
    }

    [Test]
    public async Task RemoveObject_Missing_Returns_False()
    {
        var blackboard = new Blackboard();

        bool removed = blackboard.RemoveObject<TestService>();

        await Assert.That(removed).IsFalse();
    }

    [Test]
    public async Task GetObject_Missing_Throws_KeyNotFoundException()
    {
        var blackboard = new Blackboard();

        KeyNotFoundException? ex = null;
        try
        {
            blackboard.GetObject<TestService>();
        }
        catch (KeyNotFoundException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Multiple_Object_Types_Coexist()
    {
        var blackboard = new Blackboard();
        blackboard.SetObject(new TestService { Name = "A" });
        blackboard.SetObject(new OtherService { Id = 5 });

        await Assert.That(blackboard.GetObject<TestService>().Name).IsEqualTo("A");
        await Assert.That(blackboard.GetObject<OtherService>().Id).IsEqualTo(5);
    }

    [Test]
    public async Task SetObject_Null_Throws_ArgumentNullException()
    {
        var blackboard = new Blackboard();

        ArgumentNullException? ex = null;
        try
        {
            blackboard.SetObject<TestService>(null!);
        }
        catch (ArgumentNullException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    // ============================
    // Pointer-based access (unsupported)
    // ============================

    [Test]
    public async Task GetDataPtrRO_Throws_NotSupportedException()
    {
        var blackboard = new Blackboard();

        NotSupportedException? ex = null;
        try
        {
            blackboard.GetDataPtrRO(typeof(TestData));
        }
        catch (NotSupportedException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task GetDataPtrRW_Throws_NotSupportedException()
    {
        var blackboard = new Blackboard();

        NotSupportedException? ex = null;
        try
        {
            blackboard.GetDataPtrRW(typeof(TestData));
        }
        catch (NotSupportedException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    // ============================
    // Null argument validation
    // ============================

    [Test]
    public async Task HasData_ByType_Null_Throws_ArgumentNullException()
    {
        var blackboard = new Blackboard();

        ArgumentNullException? ex = null;
        try
        {
            blackboard.HasData(null!);
        }
        catch (ArgumentNullException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Has_Null_Key_Throws_ArgumentNullException()
    {
        var blackboard = new Blackboard();

        ArgumentNullException? ex = null;
        try
        {
            blackboard.Has(null!);
        }
        catch (ArgumentNullException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Get_Null_Key_Throws_ArgumentNullException()
    {
        var blackboard = new Blackboard();

        ArgumentNullException? ex = null;
        try
        {
            blackboard.Get<int>(null!);
        }
        catch (ArgumentNullException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Set_Null_Key_Throws_ArgumentNullException()
    {
        var blackboard = new Blackboard();

        ArgumentNullException? ex = null;
        try
        {
            blackboard.Set(null!, 42);
        }
        catch (ArgumentNullException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task TryGet_Null_Key_Throws_ArgumentNullException()
    {
        var blackboard = new Blackboard();

        ArgumentNullException? ex = null;
        try
        {
            blackboard.TryGet(null!, out int _);
        }
        catch (ArgumentNullException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Remove_Null_Key_Throws_ArgumentNullException()
    {
        var blackboard = new Blackboard();

        ArgumentNullException? ex = null;
        try
        {
            blackboard.Remove(null!);
        }
        catch (ArgumentNullException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
    }
}
