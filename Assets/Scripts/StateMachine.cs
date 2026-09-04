public class StateMachine<T> where T : class, IState
{
    public T Current { get; private set; }

    /// <summary>Ignores null and re-entry into the current state.</summary>
    public void Change(T next)
    {
        if (next == null || next == Current) return;

        Current?.Exit();
        Current = next;
        Current.Enter();
    }

    public void Tick() => Current?.Tick();

    public void FixedTick() => Current?.FixedTick();
}
