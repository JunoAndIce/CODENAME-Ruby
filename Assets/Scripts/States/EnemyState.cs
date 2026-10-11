public enum EnemyState
{
    // AI states: decisions the enemy makes for itself.
    Idle,
    Patrol,
    Searching,
    Aggro,
    // Overrides: things done to the enemy, entered from outside rather than by the table.
    Grabbed,
    Ragdoll,
    Dead
}
