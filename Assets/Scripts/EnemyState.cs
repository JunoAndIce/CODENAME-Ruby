
public enum EnemyState
{
    // AI states — decisions the enemy makes for itself, one EnemyStateBase class each.
    Idle,
    Patrol,
    Searching,
    Alert,        // chasing; ChaseState.Id
    Attacking,

    // Overrides — things done TO the enemy. Handled by EnemyController, no state class.
    Grabbed,
    Ragdoll,
    Dead
}
