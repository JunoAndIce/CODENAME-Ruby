using UnityEngine;

/// <summary>
/// Sets its body's Rigidbody.mass from a weight class, and owns its kinematic flag: Immovable
/// bodies are kinematic, everything else dynamic. Everything else reads the Rigidbody, so
/// nothing needs to know this component exists.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class Weight : MonoBehaviour
{
    [SerializeField] private WeightTable _table;
    [SerializeField] private WeightClass _class = WeightClass.Medium;
    [Tooltip("Mass in kg when Class is Custom. Ignored otherwise.")]
    [SerializeField, Min(0.01f)] private float _customKg = 1f;

    private float Kg => _class == WeightClass.Custom ? _customKg : _table.KgFor(_class);

    /// <summary>The whole body's weight in kg. It holds while a ragdoll spreads Rigidbody.mass over its bones.</summary>
    public float TotalMassKg { get; private set; }

    /// <summary>A body's whole weight: its Weight's, or the Rigidbody's own mass when it has none.</summary>
    public static float MassOf(Rigidbody body)
        => body.TryGetComponent(out Weight weight) ? weight.TotalMassKg : body.mass;

    private void Awake() => Apply();

    // In the editor too, so the Rigidbody's Inspector shows the mass the game will use.
    private void OnValidate() => Apply();

    private void Apply()
    {
        Rigidbody body = GetComponent<Rigidbody>();
        // Kinematic is what every system already treats as fixed: the tether anchors to it,
        // walking and thrown bodies can't move it, and the PathGrid bakes it as static.
        bool immovable = _class == WeightClass.Immovable;
        if (body.isKinematic != immovable) body.isKinematic = immovable;

        if (_class != WeightClass.Custom && _table == null)
        {
            // No silent default: a guessed mass would quietly skew every drag, pull and throw.
            Debug.LogWarning($"{name}: Weight is set to {_class} but has no Weight Table; its Rigidbody mass is left as is.", this);
            TotalMassKg = body.mass;
            return;
        }

        TotalMassKg = Kg;
        if (!Mathf.Approximately(body.mass, Kg)) body.mass = Kg;
    }
}
