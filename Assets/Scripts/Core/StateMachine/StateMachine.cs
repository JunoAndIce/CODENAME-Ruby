using System;
using System.Collections.Generic;

/// <summary>
/// Generic state machine, run by the enemy. Nodes are keyed by state Type, so each
/// state class has one instance per machine, built once and reused across transitions. A table's
/// order is its priority: transitions are tried in the order they were added, any-state ones first,
/// and the first whose condition holds wins.
/// </summary>
public class StateMachine
{
    readonly Dictionary<Type, StateNode> _nodes = new();
    readonly List<ITransition> _anyTransitions = new();
    StateNode _current;

    public IState Current => _current?.State;

    /// <summary>
    /// Raised on every change of state, by the table or by SetState from outside, after the old state's
    /// Exit and before the new one's Enter, with (from, to); from is null on the first. For state the
    /// owner keeps across several states, which no single Exit can clear because it doesn't know where
    /// it is going.
    /// </summary>
    public event Action<IState, IState> Changed;

    public void Tick()
    {
        ITransition transition = GetTransition();
        if (transition != null) SetState(transition.To);

        _current?.State.Tick();
    }

    public void FixedTick() => _current?.State.FixedTick();

    public void SetState(IState state)
    {
        if (_current?.State == state) return;

        IState from = _current?.State;
        from?.Exit();
        _current = GetOrAddNode(state);
        Changed?.Invoke(from, _current.State);
        _current.State.Enter();
    }

    public void AddTransition(IState from, IState to, IPredicate condition)
        => GetOrAddNode(from).Transitions.Add(new Transition(GetOrAddNode(to).State, condition));

    public void AddAnyTransition(IState to, IPredicate condition)
        => _anyTransitions.Add(new Transition(GetOrAddNode(to).State, condition));

    StateNode GetOrAddNode(IState state)
    {
        if (!_nodes.TryGetValue(state.GetType(), out StateNode node))
        {
            node = new StateNode(state);
            _nodes.Add(state.GetType(), node);
        }
        return node;
    }

    ITransition GetTransition()
    {
        foreach (ITransition transition in _anyTransitions)
            if (transition.Condition.Evaluate()) return transition;

        if (_current == null) return null;

        foreach (ITransition transition in _current.Transitions)
            if (transition.Condition.Evaluate()) return transition;

        return null;
    }

    class StateNode
    {
        public IState State { get; }
        public List<ITransition> Transitions { get; } = new();

        public StateNode(IState state) => State = state;
    }
}
