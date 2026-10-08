using System;
using System.Collections.Generic;

/// <summary>
/// Generic state machine shared by player and enemy. Nodes are keyed by state Type, so each
/// state class has one instance per machine, built once and reused across transitions.
/// </summary>
public class StateMachine
{
    readonly Dictionary<Type, StateNode> _nodes = new();
    readonly HashSet<ITransition> _anyTransitions = new();
    StateNode _current;

    public IState Current => _current?.State;

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

        _current?.State.Exit();
        _current = GetOrAddNode(state);
        _current.State.Enter();
    }

    /// <summary>Leave the current state without entering another.</summary>
    public void Clear()
    {
        _current?.State.Exit();
        _current = null;
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
        public HashSet<ITransition> Transitions { get; } = new();

        public StateNode(IState state) => State = state;
    }
}
