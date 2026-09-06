using UnityEngine;

/// <summary>
/// The chain has hold of an enemy or object. This state owns the charge, because charging
/// only exists while holding — splitting them into two states would mean a Charging state
/// that can only ever be entered from Holding and exited back to it.
///
/// Per the original spec, entering here locks the camera to frame the player and the grabbed
/// target, and suspends the hover indicator.
/// </summary>
public class PlayerHoldingState : PlayerStateBase
{
    private float _chargeTimer;
    private Vector3 _swingDirection;

    public PlayerHoldingState(PlayerController player) : base(player) { }

    public override PlayerState Id => PlayerState.Holding;

    public override void Enter()
    {
        // TODO: _chargeTimer = 0f; _swingDirection = Vector3.zero
        // TODO: CameraController.LockTo(player, grabbed)
        // TODO: suspend the targeting indicator
    }

    public override void Exit()
    {
        // TODO: CameraController.Unlock()
        // TODO: resume the targeting indicator
        //
        // Exit must undo both on EVERY path out — thrown, broken free, or the player died.
        // A camera left locked to a destroyed transform is the failure to watch for.
    }

    public override void Tick()
    {
        // TODO: _chargeTimer += Time.deltaTime, clamped to Player.MaxChargeTime
        //
        // TODO: read mouse/stick movement and quantise to the four axis directions from the
        //       spec: +1/-1 on x is right/left, +1/-1 on z is up/down
        //
        // TODO: released early -> power scales with _chargeTimer; held to full -> full power
        // TODO: on release, swing and then EnemyController.Release(velocity), back to Free
        //
        // TODO: the grabbed enemy's break-out timer also ends this — the enemy escapes and
        //       we return to Free without a throw
    }
}
