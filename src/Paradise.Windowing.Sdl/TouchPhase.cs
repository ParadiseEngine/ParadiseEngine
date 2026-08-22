namespace Paradise.Windowing.Sdl;

/// <summary>What a finger just did. Internal to the backend: the contract expresses a touch as
/// a button transition plus pointer moves, so this only survives long enough to pick which.</summary>
internal enum TouchPhase
{
    Down,
    Move,
    Up,
}
