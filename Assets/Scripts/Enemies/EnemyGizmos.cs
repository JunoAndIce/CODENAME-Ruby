using UnityEngine;

// What an enemy draws in the Scene view, kept out of the controller: Unity only calls the two
// messages on the MonoBehaviour, which forward here. Edit mode and the prefab open in Prefab Mode
// during Play never run Awake, so Memory and Arms can be null: every guard below stays.
internal static class EnemyGizmos
{
    public static void DrawSelected(EnemyController enemy)
    {
        // The view, flat at eye height: green when the player is noticed, red when she's in the
        // view but hidden, grey otherwise. Edit mode has no player to test, so it stays grey.
        bool tracking = Application.isPlaying && enemy.Player != null;
        bool inView = tracking && enemy.Vision.InView(enemy.Player.transform.position);
        Gizmos.color = inView ? (enemy.Vision.CanSeePlayer() ? Color.green : Color.red) : Color.grey;
        Vector3 eye = enemy.EyePosition;
        DrawFlatCircle(eye, enemy.CloseRadius);
        // Pitched straight up or down he has no facing and no cone (see InView), so draw none
        // rather than invent a direction.
        if (EnemyVision.TryFlatForward(enemy.transform, out Vector3 flatForward)) DrawFlatArc(eye, flatForward, enemy.ViewAngle, enemy.ViewRange);

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(enemy.transform.position, enemy.GiveUpRadius);

        // What the senses sweep: loose bodies moving out to Motion Radius, corpses within Body Notice Range.
        Gizmos.color = new Color(0.6f, 0.2f, 1f);
        Gizmos.DrawWireSphere(enemy.transform.position, enemy.MotionRadius);
        Gizmos.color = new Color(1f, 0.4f, 0.8f);
        Gizmos.DrawWireSphere(enemy.transform.position, enemy.BodyNoticeRange);

        // Where he counts as having reached the spot he knew, and Attack Near Radius's own ring is DrawRoutes'.
        // In edit mode there is no news of Ruby, so that ring is drawn round him, to judge its size.
        if (TryGetRecentNews(enemy, out RubyFact news))
        {
            Gizmos.color = AttackNearColour;
            DrawFlatCircle(news.Position + Vector3.up * 0.1f, enemy.KnownSpotRadius);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(news.Position + Vector3.up * 0.8f + Vector3.right * enemy.AttackNearRadius, $"Attack Near Radius {enemy.AttackNearRadius:0.#} m, reported {Time.time - news.Time:0.0}s ago");
#endif
        }
        else if (!Application.isPlaying)
        {
            Gizmos.color = AttackNearColour;
            DrawFlatCircle(enemy.transform.position + Vector3.up * 0.1f, enemy.AttackNearRadius);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(enemy.transform.position + Vector3.up * 0.8f + Vector3.right * enemy.AttackNearRadius, $"Attack Near Radius {enemy.AttackNearRadius:0.#} m");
#endif
        }

        // The gun's engagement band: fires out to Range, closes in only to Min Distance. In edit
        // mode Arms isn't set yet, so read the gun under the hand directly.
        Gun gun = enemy.Arms != null ? enemy.Arms.HeldGun : enemy.GetComponentInChildren<Gun>();
        if (gun != null)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(enemy.transform.position, gun.Range);
            Gizmos.color = new Color(1f, 0.5f, 0f);
            Gizmos.DrawWireSphere(enemy.transform.position, gun.MinDistance);
        }

        // Sight line in play: green when the player is in view, red when something static is in the way.
        if (Application.isPlaying && enemy.Player != null)
        {
            Gizmos.color = enemy.Vision.CanSeePlayer() ? Color.green : Color.red;
            Gizmos.DrawLine(enemy.EyePosition, enemy.Player.transform.position);
        }
    }

    // Every enemy's routes, faintly, so you can see who walks what without selecting each one:
    // cyan for the Safe route, orange for the Aggressive one.
    public static void DrawRoutes(EnemyController enemy)
    {
        DrawRoute(enemy.SafeRoute, new Color(0f, 1f, 1f, 0.35f));
        DrawRoute(enemy.AggressiveRoute, new Color(1f, 0.55f, 0.1f, 0.45f));

        // Without selecting him, because it decides what you see him do: a disturbance inside it is searched
        // for as an attack. Enemies who share news of her draw the same ring, one on top of the other.
        if (TryGetRecentNews(enemy, out RubyFact news))
        {
            Gizmos.color = AttackNearColour;
            DrawFlatCircle(news.Position + Vector3.up * 0.1f, enemy.AttackNearRadius);
        }
    }

    private static readonly Color AttackNearColour = new(1f, 0.55f, 0.1f, 0.6f);

    // Around where he last heard or saw of Ruby, while that report is recent enough to count. Playing doesn't
    // mean he has woken: the prefab open in Prefab Mode during Play never runs Awake, so it has no Memory to ask.
    private static bool TryGetRecentNews(EnemyController enemy, out RubyFact news)
    {
        news = default;
        return Application.isPlaying && enemy.Memory != null && enemy.Awareness.TryGetNewestRubyFact(out news)
            && Time.time - news.Time <= enemy.AttackNearTime;
    }

    private static void DrawRoute(PatrolRoute route, Color colour)
    {
        if (route == null || route.Count == 0) return;

        Gizmos.color = colour;
        int count = route.Count;
        for (int i = 0; i < count; i++)
        {
            PatrolPoint point = route[i];
            DrawFlatArea(point.Area);

            // A random route has no order, so lines between its points would only mislead.
            if (route.Mode == PatrolMode.Random) continue;

            bool last = i == count - 1;
            if (!last) Gizmos.DrawLine(point.Position, route[i + 1].Position);
            else if (route.Mode == PatrolMode.Loop && count > 2) Gizmos.DrawLine(point.Position, route[0].Position);
        }
    }

    private static void DrawFlatArea(GridArea area)
    {
        if (area.Shape == AreaShape.Rectangle)
            Gizmos.DrawWireCube(area.Centre, new Vector3(area.HalfSize.x * 2f, 0f, area.HalfSize.y * 2f));
        else
            DrawFlatCircle(area.Centre, area.HalfSize.x);
    }

    // A pie slice: the two edges and the rim between them, centred on forward.
    private static void DrawFlatArc(Vector3 centre, Vector3 forward, float angle, float radius)
    {
        const int segments = 24;
        Vector3 previous = centre + Quaternion.AngleAxis(-angle * 0.5f, Vector3.up) * forward * radius;
        Gizmos.DrawLine(centre, previous);
        for (int i = 1; i <= segments; i++)
        {
            float step = -angle * 0.5f + angle * i / segments;
            Vector3 next = centre + Quaternion.AngleAxis(step, Vector3.up) * forward * radius;
            Gizmos.DrawLine(previous, next);
            previous = next;
        }
        Gizmos.DrawLine(centre, previous);
    }

    private static void DrawFlatCircle(Vector3 centre, float radius)
    {
        const int segments = 24;
        Vector3 previous = centre + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            Vector3 next = centre + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(previous, next);
            previous = next;
        }
    }
}
