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

    public float BreakStrength => _breakStrength;
    public bool Attachable => _grabable;

    public Rigidbody Host { get; private set; }
    public bool IsStaticWorld => Host == null || Host.isKinematic;
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
    /// The closest free node the aim points at, inside range and the aim cone. Distance and
    /// angle math only, no physics calls, so it scales to thousands of nodes.
    /// </summary>
    public static GrappleNode FindBest(Vector3 origin, Vector3 aimDir, float range, float cone)
    {
        GrappleNode best = null;
        float bestScore = float.MaxValue;

        foreach (GrappleNode node in All)
        {
            if (node == null || node.IsOccupied || !node.Attachable) continue;

            Vector3 to = node.AnchorPoint - origin;
            to.y = 0f;
            float dist = to.magnitude;
            if (dist > range || dist < 0.1f) continue;

            float angle = Vector3.Angle(aimDir, to / dist);
            if (angle > cone) continue;

            // Distance weighted by aim offset.
            float score = dist + angle * (range / 90f);
            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        return best;
    }
}
