using UnityEngine;

/// <summary>
/// One enemy's body as the chain and the gun treat it: the record of being held and thrown, the gun
/// dropped or popped out of his hand, and the corpse's darkening. Kept out of EnemyController so the
/// controller doesn't carry them. The grab methods stay on the controller because they call SetState;
/// they call this. The tuning (Grab Pop Up, Grab Pop Out, Corpse Brightness) stays on the controller
/// and is read on every call, so retuning in the Inspector during Play keeps working.
/// </summary>
public class EnemyBodyState
{
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private readonly EnemyController _enemy;

    private Vector3 _heldFrom;
    private Vector3 _thrownFrom;
    private float _thrownAt = float.NegativeInfinity;

    public EnemyBodyState(EnemyController enemy)
    {
        _enemy = enemy;
    }

    /// <summary>On the chain right now. A death mid-swing reads this to stay held.</summary>
    public bool IsHeld { get; private set; }

    /// <summary>How far he has been moved from where he was grabbed, in flat metres; 0 when he isn't held. What tells a watcher Ruby is dragging him.</summary>
    public float HeldMovedBy => IsHeld ? PathGrid.FlatDistance(_heldFrom, _enemy.transform.position) : 0f;

    /// <summary>
    /// Whether the chain threw him within the last `within` seconds, and where he was when it let go. A
    /// watcher tests that spot, not the body: a thrown body can be many metres off by his next scan.
    /// </summary>
    public bool WasThrownWithin(float within, out Vector3 from)
    {
        from = _thrownFrom;
        return Time.time - _thrownAt <= within;
    }

    /// <summary>The chain has him: records where he was grabbed and that he is held.</summary>
    public void NoteHeld()
    {
        _heldFrom = _enemy.transform.position;
        IsHeld = true;
    }

    /// <summary>
    /// The chain let go. `thrown` is whether a watcher should see it as a throw: a thrown release, or one
    /// let go mid-spin, where he flies out along her aim. A plain drop isn't.
    /// </summary>
    public void NoteReleased(bool thrown)
    {
        IsHeld = false;
        if (thrown)
        {
            _thrownFrom = _enemy.transform.position;
            _thrownAt = Time.time;
        }
    }

    /// <summary>Lets the gun go with the body's velocity, so it skids on the way the enemy was moving.</summary>
    public void DropGun()
    {
        if (_enemy.Arms != null) _enemy.Arms.Drop(_enemy.Body.linearVelocity);
    }

    // The grab knocks the gun out of his hand: a small hop up and away from his body, on top of
    // how he was moving.
    public void PopGun()
    {
        if (!_enemy.IsArmed) return;
        Vector3 outward = _enemy.Arms.HeldGun.transform.position - _enemy.transform.position;
        outward.y = 0f;
        outward = outward.sqrMagnitude > 0.0001f ? outward.normalized : _enemy.transform.forward;
        _enemy.Arms.Drop(_enemy.Body.linearVelocity + outward * _enemy.GrabPopOut + Vector3.up * _enemy.GrabPopUp);
    }

    /// <summary>
    /// Darkens every renderer on the enemy to Corpse Brightness, through property blocks so the
    /// shared materials (and every other enemy using them) are untouched.
    /// </summary>
    public void Darken()
    {
        float brightness = _enemy.CorpseBrightness;
        var block = new MaterialPropertyBlock();
        foreach (Renderer rendered in _enemy.GetComponentsInChildren<Renderer>())
        {
            Material material = rendered.sharedMaterial;
            if (material == null) continue;

            Color colour = material.HasProperty(BaseColorId) ? material.GetColor(BaseColorId) : material.color;
            colour = new Color(colour.r * brightness, colour.g * brightness, colour.b * brightness, colour.a);

            rendered.GetPropertyBlock(block);
            block.SetColor(BaseColorId, colour);   // URP Lit
            block.SetColor(ColorId, colour);       // built-in and sprite shaders
            rendered.SetPropertyBlock(block);
        }
    }
}
