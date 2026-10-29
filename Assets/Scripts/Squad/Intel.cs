using UnityEngine;

/// <summary>
/// The squad's shared knowledge: what the officers between them have heard and seen. For now it
/// only holds the stimulus rules and configures the bus with them while it is active. Without an
/// active Intel, shots and hits go nowhere and the bus warns once.
/// </summary>
public class Intel : MonoBehaviour
{
    [Tooltip("The shared stimulus numbers: loudness, notice and alarm speeds, how long an origin is remembered.")]
    [SerializeField] private StimulusRules _stimulusRules;

    // Enable and disable rather than Awake and destroy, so switching the component off also
    // silences the bus instead of leaving stale rules behind.
    private void OnEnable()
    {
        if (_stimulusRules == null)
        {
            Debug.LogError($"{name}: Intel has no Stimulus Rules — shots, hits and thrown bodies make no stimuli.", this);
            return;
        }
        Stimuli.Configure(_stimulusRules);
    }

    private void OnDisable() => Stimuli.Configure(null);
}
