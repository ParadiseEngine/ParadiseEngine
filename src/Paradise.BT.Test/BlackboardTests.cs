namespace Paradise.BT.Test;

public sealed class BlackboardTests
{
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
}
