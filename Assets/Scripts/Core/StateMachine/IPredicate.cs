using System;

public interface IPredicate
{
    bool Evaluate();
}

public class FuncPredicate : IPredicate
{
    readonly Func<bool> _func;
    public FuncPredicate(Func<bool> func) => _func = func;

    public bool Evaluate() => _func.Invoke();
}
