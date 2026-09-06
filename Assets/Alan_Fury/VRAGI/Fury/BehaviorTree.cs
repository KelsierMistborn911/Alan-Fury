using System;

/// <summary>
/// Минимальный каркас дерева поведения (BT).
/// Узел возвращает статус: Success / Running / Failure.
///   - Selector: пробует детей по очереди, отдаёт первый НЕ-Failure (приоритет сверху вниз).
///   - Sequence: идёт по детям, пока Success; первый Running/Failure всплывает наверх.
///   - Condition: проверка (true → Success, false → Failure).
///   - Action: действие, само возвращает статус.
///
/// Лямбды сейчас; при росте поведения можно перевести на классы-наследники.
/// </summary>
public enum NodeStatus { Success, Running, Failure }

public abstract class BTNode
{
    public abstract NodeStatus Tick();
}

public class BTSelector : BTNode
{
    private readonly BTNode[] _children;
    public BTSelector(params BTNode[] children) { _children = children; }

    public override NodeStatus Tick()
    {
        for (int i = 0; i < _children.Length; i++)
        {
            NodeStatus s = _children[i].Tick();
            if (s != NodeStatus.Failure) return s; // Success или Running → стоп
        }
        return NodeStatus.Failure;
    }
}

public class BTSequence : BTNode
{
    private readonly BTNode[] _children;
    public BTSequence(params BTNode[] children) { _children = children; }

    public override NodeStatus Tick()
    {
        for (int i = 0; i < _children.Length; i++)
        {
            NodeStatus s = _children[i].Tick();
            if (s != NodeStatus.Success) return s; // Running или Failure всплывает
        }
        return NodeStatus.Success;
    }
}

public class BTCondition : BTNode
{
    private readonly Func<bool> _check;
    public BTCondition(Func<bool> check) { _check = check; }
    public override NodeStatus Tick() => _check() ? NodeStatus.Success : NodeStatus.Failure;
}

public class BTAction : BTNode
{
    private readonly Func<NodeStatus> _act;
    public BTAction(Func<NodeStatus> act) { _act = act; }
    public override NodeStatus Tick() => _act();
}
