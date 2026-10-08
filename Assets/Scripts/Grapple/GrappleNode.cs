using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A grapple anchor on any host: static world, entity or prop, several per object if wanted.
/// A kinematic or missing body means the node counts as static world.
/// </summary>
public class GrappleNode : MonoBehaviour
{
    [Header("Strength")]
    [Tooltip("Tension this node should take before tearing free. The chain never breaks: going over this only logs a warning, as tuning data for the later structural-break system.")]
    [SerializeField, Min(1f)] private float _breakStrength = 800f;

    [Header("Grabbing")]
    [Tooltip("Whether the grapple can attach here.")]
    [SerializeField] private bool _grabable = true;   // keeps its old spelling: scene overrides name it

    /// <summary>All live nodes, so the grapple scans a list instead of searching the scene.</summary>
    public static readonly List<GrappleNode> All = new();

    const float MountSkip = 0.35f;     // sight ignores hits this close to the anchor: its mount surface
    const float StickMargin = 0.3f;    // how much nearer the cursor another node must be to take the mark

    static readonly List<(float distance, GrappleNode node)> Candidates = new();
    static readonly RaycastHit[] SightHits = new RaycastHit[16];

    public float BreakStrength => _breakStrength;
    public bool Attachable => _grabable;

    public Rigidbody Host { get; private set; }
    public bool IsStaticWorld => Host == null || Host.isKinematic;
    /// <summary>On a body that can't be moved (kinematic), as opposed to bare world with no body.</summary>
    public bool IsImmovable => Host != null && Host.isKinematic;
    public bool IsOccupied { get; internal set; }
    /// <summary>What this node lets the grapple grab (an enemy). Null for world and props.</summary>
    public IGrabbable Grabbable { get; private set; }

    public Vector3 AnchorPoint => transform.position;

    void Awake()
    {
        Host = GetComponentInParent<Rigidbody>();
        Grabbable = GetComponentInParent<IGrabbable>();
    }

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    /// <summary>
    /// The free node nearest the cursor: within `snapRadius` of it, within `range` of the user, and
    /// in the user's sight. Nearest the cursor, not the user, so a fast click grabs what the
    /// reticle is on. `current` (the node marked last frame) keeps the mark until another is
    /// clearly nearer, so the mark doesn't flicker between two close nodes. Sight is tested
    /// nearest first, so usually one raycast runs.
    /// </summary>
    public static GrappleNode FindBest(Vector3 origin, Vector3 cursor, float range, float snapRadius,
                                       GrappleNode current, Rigidbody user)
    {
        Candidates.Clear();
        foreach (GrappleNode node in All)
        {
            if (node == null || node.IsOccupied || !node.Attachable) continue;
            if (FlatDistance(node.AnchorPoint, origin) > range) continue;

            float toCursor = FlatDistance(node.AnchorPoint, cursor);
            if (toCursor <= snapRadius) Candidates.Add((toCursor, node));
        }
        Candidates.Sort((a, b) => a.distance.CompareTo(b.distance));

        GrappleNode best = null;
        float bestDistance = 0f;
        foreach ((float distance, GrappleNode node) in Candidates)
        {
            if (!InSight(origin, node, user)) continue;
            best = node;
            bestDistance = distance;
            break;
        }
        if (best == null || current == null || current == best) return best;

        // The marked node keeps the mark unless the best is clearly nearer.
        foreach ((float distance, GrappleNode node) in Candidates)
        {
            if (node != current) continue;
            if (distance - bestDistance < StickMargin && InSight(origin, current, user)) return current;
            break;
        }
        return best;
    }

    // A clear line from the user to the anchor. Only static geometry blocks it: walls and
    // immovable objects, not crates or enemies, which the rope catches on once hooked instead.
    static bool InSight(Vector3 origin, GrappleNode node, Rigidbody user)
    {
        Vector3 to = node.AnchorPoint - origin;
        float distance = to.magnitude;
        if (distance < 0.01f) return true;

        int count = Physics.RaycastNonAlloc(origin, to / distance, SightHits, distance, Physics.AllLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = SightHits[i];
            Rigidbody body = hit.collider.attachedRigidbody;
            if (body != null && !body.isKinematic) continue;
            if (body != null && (body == user || body == node.Host)) continue;
            // The surface a world node is mounted on.
            if ((hit.point - node.AnchorPoint).sqrMagnitude < MountSkip * MountSkip) continue;
            return false;
        }
        return true;
    }

    static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
