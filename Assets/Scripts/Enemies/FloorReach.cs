using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// One flood along a floor and who stands inside it, for speech, sound and calling people in. Each
/// use of it in Intel owns an instance, so each keeps its own warn-once sets: a key warned by one
/// must not silence another's, and each warns in its own words (the caller words them, since only
/// it knows what the setup problem costs it).
///
/// Three steps: Begin floods and remembers the field's generation, Measure reads one enemy off
/// that field, End checks nothing flooded in between. PathGrid.FloodFrom hands back the grid's one
/// scratch field and the next flood overwrites it, so a read that ran across another flood would
/// silently drop or invent listeners. End turns that into an error. It is a guard, not a fallback:
/// the callers collect before they tell anyone, so it must never fire in normal play.
/// </summary>
public class FloorReach
{
    public enum Status { Reached, NoGrid, NoReach }
    public enum Standing { InRange, OutOfRange, OffGrid }

    private readonly HashSet<Object> _warnedNoGrid = new();
    private readonly HashSet<Object> _warnedNoReach = new();
    private readonly HashSet<Object> _warnedOffGrid = new();

    private PathGrid _grid;
    private FlowField _field;
    private int _generation;

    /// <summary>The flood this reach is reading. Valid between a Begin that returned Reached and End.</summary>
    public FlowField Field => _field;

    /// <summary>True the first time a source with no grid under it is reported. A setup problem warns once per key, not once per call.</summary>
    public bool FirstNoGrid(Object key) => _warnedNoGrid.Add(key);

    /// <summary>True the first time a source with no walkable ground in reach is reported.</summary>
    public bool FirstNoReach(Object key) => _warnedNoReach.Add(key);

    /// <summary>True the first time a listener standing on no grid is reported.</summary>
    public bool FirstOffGrid(Object key) => _warnedOffGrid.Add(key);

    /// <summary>
    /// Floods once from source out to range metres on the floor under it. Reached leaves the field
    /// open for Measure; the other two leave nothing to read, and the caller warns and returns.
    /// </summary>
    public Status Begin(Vector3 source, float range)
    {
        _grid = PathGrid.At(source);
        _field = null;
        if (_grid == null) return Status.NoGrid;

        FlowField field = _grid.FloodFrom(source, range);
        if (!field.HasGoal) return Status.NoReach;

        _field = field;
        _generation = field.Generation;
        return Status.Reached;
    }

    /// <summary>
    /// Where one enemy stands against the flood: inside range (metres set), outside it or on
    /// another floor (floors are independent by design, so he simply doesn't hear), or on no grid
    /// at all, which only the caller knows whether to warn about.
    /// </summary>
    public Standing Measure(EnemyController enemy, float range, out float metres)
    {
        metres = 0f;
        PathGrid enemyGrid = PathGrid.At(enemy.transform.position);
        if (enemyGrid == null) return Standing.OffGrid;
        if (enemyGrid != _grid) return Standing.OutOfRange;
        if (!_field.TryGetDistance(enemy.transform.position, out metres) || metres > range) return Standing.OutOfRange;
        return Standing.InRange;
    }

    /// <summary>Ends the read. An error when something flooded this grid since Begin: the scratch field was overwritten under it.</summary>
    public void End(string what, Object context)
    {
        FlowField field = _field;
        _field = null;
        if (field == null || field.Generation == _generation) return;
        Debug.LogError($"The shared flood for {what} was overwritten while it was being read: something flooded the same grid before the read finished, so who it reached is wrong. Collect first and tell afterwards.", context);
    }
}
