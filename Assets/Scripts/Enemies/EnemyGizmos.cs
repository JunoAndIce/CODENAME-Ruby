using UnityEngine;

// What an enemy draws in the Scene view, kept out of the controller: Unity only calls the two
// messages on the MonoBehaviour, which forward here. Edit mode and the prefab open in Prefab Mode
// during Play never run Awake, so Memory and Arms can be null: every guard below stays.
internal static class EnemyGizmos
{
    public static void DrawSelected(EnemyController enemy)
    {
        // Their range post, so a spot set on the wrong enemy shows.
        if (enemy.PostSpot != null)
        {
            Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.9f);
            Gizmos.DrawLine(enemy.transform.position, enemy.PostSpot.Position);
        }

        // The view, flat at eye height: green when the player is noticed, red when she's in the
        // view but hidden, grey otherwise. Edit mode has no player to test, so it stays grey. The arc is
        // all they see things by; the close circle is for Ruby alone.
        bool tracking = Application.isPlaying && enemy.Player != null;
        bool inView = tracking && enemy.Vision.InView(enemy.Player.transform.position);
        Gizmos.color = inView ? (enemy.Vision.CanSeePlayer() ? Color.green : Color.red) : Color.grey;
        Vector3 eye = enemy.EyePosition;
        DrawFlatCircle(eye, enemy.CloseRadius);
        // Pitched straight up or down they have no facing and no cone (see InView), so draw none
        // rather than invent a direction.
        if (EnemyVision.TryFlatForward(enemy.transform, out Vector3 flatForward)) DrawFlatArc(eye, flatForward, enemy.ViewAngle, enemy.ViewRange);

        // The beat they are rolling through, and what it turns them toward.
        if (Application.isPlaying && enemy.Beat != null && enemy.Beat.Running)
        {
            Gizmos.color = new Color(1f, 0.5f, 1f, 0.9f);
            Gizmos.DrawLine(eye, enemy.Beat.FacePoint);
            Gizmos.DrawWireSphere(enemy.Beat.FacePoint, 0.2f);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(eye + Vector3.up * 0.5f, $"{enemy.Beat.Kind} beat {enemy.Beat.Progress:P0}");
#endif
        }

        // The way out they cover in the fight's hunt plan: where they stand, and which way they look from there.
        if (Application.isPlaying && enemy.Fight != null && enemy.Fight.TryGetSlot(enemy, out HuntSlot slot))
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(eye, slot.Spot);
            Gizmos.DrawWireSphere(slot.Spot, 0.3f);
            Gizmos.DrawLine(slot.Spot + Vector3.up * 0.1f, slot.Spot + Vector3.up * 0.1f + slot.Outward * 1.2f);
        }

        // The path they hunt once Ruby is out of sight: where they lost her, which way she was going, and their guess
        // at where she went. The cached guess only: drawing never floods.
        if (Application.isPlaying && enemy.Memory != null && enemy.Awareness.HasGuess)
        {
            Vector3 lift = Vector3.up * 0.15f;
            Vector3 from = enemy.Awareness.GuessFrom + lift;
            Vector3 guess = enemy.Awareness.Guess + lift;
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(from, 0.25f);
            Gizmos.DrawLine(from, from + enemy.Awareness.GuessHeading * 0.5f);
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.6f);
            Gizmos.DrawLine(from, guess);
            Gizmos.DrawSphere(guess, 0.3f);
        }

        // Attack Near Radius round where she was last reported. Only on the selected enemy: drawn on every enemy in
        // Play, the rings piled up and hid the scene. In edit mode there is no news of Ruby, so the ring is drawn
        // round them, to judge its size.
        if (TryGetRecentNews(enemy, out RubyFact news))
        {
            Gizmos.color = AttackNearColour;
            DrawFlatCircle(news.Position + Vector3.up * 0.1f, enemy.AttackNearRadius);
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

        // How far they go for a free gun once unarmed. Drawn armed or not, because that's when it's set up. 0 means they never re-arms, so nothing.
        if (enemy.RearmRadius > 0f)
        {
            Gizmos.color = new Color(0.25f, 0.6f, 1f);
            Gizmos.DrawWireSphere(enemy.transform.position, enemy.RearmRadius);
        }

        // The firing spot they have claimed in their fight, and the way they'd walk to it.
        if (Application.isPlaying && enemy.TryGetFiringSpot(out Vector3 firingSpot))
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(firingSpot, 0.4f);
            Gizmos.DrawLine(enemy.transform.position, firingSpot);
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
    }

    private static readonly Color AttackNearColour = new(1f, 0.55f, 0.1f, 0.6f);

    // Around where they last heard or saw of Ruby, while that report is recent enough to count. Playing doesn't
    // mean they have woken: the prefab open in Prefab Mode during Play never runs Awake, so it has no Memory to ask.
    private static bool TryGetRecentNews(EnemyController enemy, out RubyFact news)
    {
        news = default;
        return Application.isPlaying && enemy.Memory != null && enemy.Awareness.TryGetNewestRedFact(out news)
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
