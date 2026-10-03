using System;
using System.Collections.Generic;

public class StateMachine
{
    StateNode current;
    Dictionary<Type, StateNode> nodes = new();
    HashSet<ITransition> anyTransitions = new();

    public IState Current => current?.State;
    public void Tick()
    {
        var transition = GetTransition();
        if (transition != null)
            ChangeState(transition.To);

        current?.State?.Tick();
    }

    public void FixedTick() => current?.State?.FixedTick();
    public void SetState(IState state)
    {
        if (current?.State == state) return;

        current?.State?.Exit();
        current = GetOrAddNode(state);
        current.State?.Enter();
    }

    void ChangeState(IState state)
    {
        if (state == current?.State) return;

        current?.State?.Exit();
        current = nodes[state.GetType()];
        current.State?.Enter();
    }

    public void AddTransition(IState from, IState to, IPredicate condition)
    {
        GetOrAddNode(from).AddTransition(GetOrAddNode(to).State, condition);
    }

    public void AddAnyTransition(IState to, IPredicate condition)
    {
        anyTransitions.Add(new Transition(GetOrAddNode(to).State, condition));
    }
    
    StateNode GetOrAddNode(IState state)
    {
        var node = nodes.GetValueOrDefault(state.GetType());

        if (node == null)
        {
            node = new StateNode(state);
            nodes.Add(state.GetType(), node);
        }

        return node;
    }

    ITransition GetTransition()
    {
        foreach (var transition in anyTransitions)
            if (transition.Condition.Evaluate())
                return transition;

        if (current == null) return null;

        foreach (var transition in current.Transitions)
            if (transition.Condition.Evaluate())
                return transition;

        return null;
    }

    class StateNode
    {
        public IState State { get; }
        public HashSet<ITransition> Transitions { get; }

        public StateNode(IState state)
        {
            State = state;
            Transitions = new HashSet<ITransition>();
        }

        public void AddTransition(IState to, IPredicate condition)
        {
            Transitions.Add(new Transition(to, condition));
        }
    }
}
