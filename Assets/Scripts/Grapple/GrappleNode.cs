using UnityEngine;

/// <summary>
/// A grapple anchor on any host: static world, entity, or prop. Multiple nodes per
/// object are expected. Carries break strength and tension events; resolves its own
/// host body (a kinematic or missing body means the node counts as static world).
/// </summary>
public class GrappleNode : MonoBehaviour
{
    [Header("Strength")]
    [SerializeField, Min(1f)] private float _breakStrength = 800f;   // sustained force before tearing free

    [Header("Grabbing")]
    [Tooltip("Stateful allow/deny for this node. Systems (health, timers, enemy AI) flip this at runtime to make a node grabbable or not — e.g. an enemy grabbable only below half health, or a terrain piece on a timer.")]
    [SerializeField] private bool _grabable = true;

    public float BreakStrength => _breakStrength;

    /// <summary>Runtime configuration seam for spawners/systems — no reflection needed.</summary>
    public void Configure(float breakStrength) => _breakStrength = Mathf.Max(1f, breakStrength);

    /// <summary>All live nodes. Controllers iterate this instead of polling
    /// FindObjectsByType; the list is maintained by enable/disable and stays cheap
    /// at hundreds-to-thousands of nodes on a map.</summary>
    public static readonly System.Collections.Generic.List<GrappleNode> All = new();

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    /// <summary>External systems set this false to un-grab this node.</summary>
    public bool Grabable
    {
        get => _grabable;
        set
        {
            if (_grabable == value) return;
            _grabable = value;
            TetherLog.Event($"{name} grabable -> {value}");
        }
    }

    /// <summary>Null or kinematic for static-world nodes.</summary>
    public Rigidbody Host { get; private set; }
    public bool IsStaticWorld => Host == null || Host.isKinematic;
    public bool IsOccupied { get; internal set; }

    public Vector3 AnchorPoint => transform.position;

    /// <summary>Fires every physics step while tethered, with current tension force.</summary>
    public event System.Action<GrappleNode, float> OnTension;
    /// <summary>Future structural-break seam: when a node's tension exceeds its break
    /// strength, a multi-part object (table = surface + legs) rips apart along its
    /// nodes. NOT a tether release — the grapple chain always holds.</summary>
    public event System.Action<GrappleNode, float> OnBreak;

    private void Awake() => Host = GetComponentInParent<Rigidbody>();

    public void ReportTension(float force) => OnTension?.Invoke(this, force);

    public void ReportBreak(float force) => OnBreak?.Invoke(this, force);
}
