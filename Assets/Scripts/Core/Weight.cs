using UnityEngine;

/// <summary>
/// Where a body's mass comes from. It writes its class's kg into Rigidbody.mass, and the
/// Rigidbody stays the runtime truth: the tether, pushing and later impact damage all read
/// Rigidbody.mass, so none of them need to know this component exists.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class Weight : MonoBehaviour
{
    [SerializeField] private WeightTable _table;
    [SerializeField] private WeightClass _class = WeightClass.Medium;
    [Tooltip("Mass in kg when Class is Custom. Ignored otherwise.")]
    [SerializeField, Min(0.01f)] private float _customKg = 1f;

    public WeightClass Class => _class;

    /// <summary>This body's mass in kg, or 0 when a table class has no table to read.</summary>
    public float Kg => _class == WeightClass.Custom ? _customKg
                     : _table != null ? _table.KgFor(_class)
                     : 0f;

    // Awake, so the mass is right before anything (a tether attaching, a push) can read it.
    private void Awake() => Apply();

    // Also in the editor, so the Rigidbody's Inspector shows the mass the game will actually use
    // rather than a stale hand-typed one. Awake re-applies it at runtime regardless.
    private void OnValidate() => Apply();

    private void Apply()
    {
        if (_class != WeightClass.Custom && _table == null)
        {
            // No silent default: a guessed mass would quietly skew every tug-of-war and throw.
            Debug.LogWarning($"{name}: Weight is set to {_class} but has no Weight Table; its Rigidbody mass is left as is.", this);
            return;
        }

        Rigidbody body = GetComponent<Rigidbody>();
        if (!Mathf.Approximately(body.mass, Kg)) body.mass = Kg;
    }
}
