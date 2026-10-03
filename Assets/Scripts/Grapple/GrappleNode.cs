using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A grapple anchor on any host: static world, entity, or prop. Multiple nodes per object are
/// expected. Carries a break strength and resolves its own host body (a kinematic or missing
/// body means the node counts as static world).
/// </summary>
public class GrappleNode : MonoBehaviour
{
    [Header("Strength")]
    [Tooltip("Tension this node should take before tearing free. The chain itself never breaks: going over this only logs a warning, the tuning data for the later structural-break system (ripping a table apart along its nodes).")]
    [SerializeField, Min(1f)] private float _breakStrength = 800f;

    [Header("Grabbing")]
    [Tooltip("Whether the grapple can attach here right now. Systems (health, timers, enemy AI) flip this at runtime — e.g. an enemy grabbable only below half health, or a terrain piece on a timer.")]
    [SerializeField] private bool _grabable = true;   // the field keeps its old spelling: scene overrides name it

    /// <summary>All live nodes. The grapple scans this instead of polling FindObjectsByType;
    /// enable/disable keep it current, and it stays cheap at hundreds-to-thousands of nodes.</summary>
    public static readonly List<GrappleNode> All = new();

    public float BreakStrength => _breakStrength;

    /// <summary>Whether the grapple can attach here right now (see the field's tooltip).</summary>
    public bool Attachable
    {
        get => _grabable;
        set
        {
            if (_grabable == value) return;
            _grabable = value;
            TetherLog.Event($"{name} attachable -> {value}");
        }
    }

    /// <summary>Null or kinematic for static-world nodes.</summary>
    public Rigidbody Host { get; private set; }
    public bool IsStaticWorld => Host == null || Host.isKinematic;
    public bool IsOccupied { get; internal set; }
    /// <summary>What this node lets the grapple grab, if anything (an enemy). Null for world and props.</summary>
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
    /// The best free node to attach to: the closest one the aim points at, inside range and the
    /// aim cone. Cheap math only (no physics calls), so scanning scales to hundreds-to-thousands
    /// of live nodes. Occlusion is deliberately left out: it wasn't earning its cost.
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

            // Closest node the player is pointing at: distance weighted by aim offset.
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
