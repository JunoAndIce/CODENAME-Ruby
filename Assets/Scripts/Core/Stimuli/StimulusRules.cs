using UnityEngine;

/// <summary>
/// The shared numbers behind stimuli: how loud things are, how fast a body must move to be noticed
/// and how long an origin is remembered. One asset, so shots, hits and thrown bodies agree.
/// </summary>
[CreateAssetMenu(fileName = "StimulusRules", menuName = "Ruby/Stimulus Rules")]
public class StimulusRules : ScriptableObject
{
    [Header("Motion")]
    [Tooltip("Speed, in m/s, at which a moving body is worth a glance, and at which its hits make noise and pass its origin on.")]
    [SerializeField, Min(0f)] private float _noticeSpeed = 4f;
    [Tooltip("Speed, in m/s, at which a moving body is cause for alarm. Kept above Notice Speed.")]
    [SerializeField, Min(0f)] private float _alarmSpeed = 15f;
    [Tooltip("Seconds a thrown body keeps leading back to where it was thrown from.")]
    [SerializeField, Min(0.1f)] private float _originLifetime = 5f;

    [Header("Loudness")]
    [Tooltip("How far a gunshot carries, in metres.")]
    [SerializeField, Min(0f)] private float _gunshotLoudness = 25f;
    [Tooltip("How far the whip landing carries, in metres.")]
    [SerializeField, Min(0f)] private float _whipLoudness = 6f;
    [Tooltip("How far a hit at Notice Speed carries, in metres.")]
    [SerializeField, Min(0f)] private float _impactLoudness = 10f;
    [Tooltip("Extra metres of carry per m/s of closing speed above Notice Speed.")]
    [SerializeField, Min(0f)] private float _impactLoudnessPerSpeed = 0.5f;
    [Tooltip("The most a hit can carry, in metres.")]
    [SerializeField, Min(0f)] private float _impactLoudnessMax = 20f;
    [Tooltip("Seconds between impact sounds from one moving body, so a tumble is a few thuds rather than dozens.")]
    [SerializeField, Min(0f)] private float _impactCooldown = 0.25f;

    public float NoticeSpeed => _noticeSpeed;
    public float AlarmSpeed => _alarmSpeed;
    public float OriginLifetime => _originLifetime;
    public float GunshotLoudness => _gunshotLoudness;
    public float WhipLoudness => _whipLoudness;
    public float ImpactCooldown => _impactCooldown;

    /// <summary>How far a hit at this closing speed carries.</summary>
    public float ImpactLoudness(float closingSpeed)
        => Mathf.Min(_impactLoudnessMax, _impactLoudness + _impactLoudnessPerSpeed * Mathf.Max(0f, closingSpeed - _noticeSpeed));

    // Alarm below Notice would make every glance an alarm.
    private void OnValidate() => _alarmSpeed = Mathf.Max(_alarmSpeed, _noticeSpeed + 0.1f);
}
