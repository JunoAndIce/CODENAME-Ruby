using UnityEngine;

/// <summary>
/// Sets its body's Rigidbody.mass from a weight class. Everything else reads Rigidbody.mass, so
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

    private void Awake() => Apply();

    // In the editor too, so the Rigidbody's Inspector shows the mass the game will use.
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
