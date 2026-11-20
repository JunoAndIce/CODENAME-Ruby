#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The PathGrid gizmo's cells, drawn as one textured quad (a texel per cell) and one mesh of tight-cell
/// markers, rebuilt only when what they show changes. A cube per cell (Gizmos.DrawCube) issued tens of
/// thousands of draws on every repaint of the Scene and Game views, four times as many at half the cell
/// size, and lagged the Editor badly with the grid selected. Editor-only: builds never draw the grid.
/// </summary>
public class GridOverlay
{
    private const double RebuildInterval = 0.2;   // seconds between rebuilds while a prop keeps re-marking
    private const float Lift = 0.02f;             // above the floor, so the quad doesn't z-fight it

    private readonly List<Vector3> _markerVertices = new();
    private readonly List<Color32> _markerColours = new();
    private readonly List<int> _markerIndices = new();
    private Texture2D _texture;
    private Color32[] _pixels;
    private Mesh _quad;
    private Mesh _markers;
    private Material _cellMaterial;
    private Material _markerMaterial;
    private bool _failed;
    private double _nextRebuild;
    private long _shownKey = long.MinValue;

    /// <summary>One colour per cell, x + z * width, for the caller to fill before Upload.</summary>
    public Color32[] Pixels => _pixels;

    /// <summary>
    /// True when the overlay must be refilled: it shows another key (the layer version and draw
    /// settings, packed by the caller), or the grid's size changed, and Rebuild Interval has passed
    /// since the last rebuild. A key that keeps changing (a crate sliding) rebuilds five times a
    /// second, not every repaint. Prepares Pixels at the grid's size.
    /// </summary>
    public bool NeedsRebuild(long key, int width, int depth)
    {
        if (_failed) return false;
        bool resized = _texture == null || _texture.width != width || _texture.height != depth;
        if (!resized && key == _shownKey) return false;
        double now = Time.realtimeSinceStartupAsDouble;
        if (!resized && now < _nextRebuild) return false;

        _nextRebuild = now + RebuildInterval;
        _shownKey = key;
        if (resized) Allocate(width, depth);
        return !_failed;
    }

    /// <summary>Starts a fresh marker list for this rebuild.</summary>
    public void ClearMarkers()
    {
        _markerVertices.Clear();
        _markerColours.Clear();
        _markerIndices.Clear();
    }

    /// <summary>A flat square marker of side size centred on point (a tight cell's fit point).</summary>
    public void AddMarker(Vector3 point, float size, Color32 colour)
    {
        float half = size * 0.5f;
        int first = _markerVertices.Count;
        float y = point.y + Lift * 2f;   // over the cell texel it sits in
        _markerVertices.Add(new Vector3(point.x - half, y, point.z - half));
        _markerVertices.Add(new Vector3(point.x - half, y, point.z + half));
        _markerVertices.Add(new Vector3(point.x + half, y, point.z + half));
        _markerVertices.Add(new Vector3(point.x + half, y, point.z - half));
        for (int i = 0; i < 4; i++) _markerColours.Add(colour);
        _markerIndices.Add(first); _markerIndices.Add(first + 1); _markerIndices.Add(first + 2);
        _markerIndices.Add(first); _markerIndices.Add(first + 2); _markerIndices.Add(first + 3);
    }

    /// <summary>Sends Pixels and the markers to the GPU. Call after filling them.</summary>
    public void Upload()
    {
        _texture.SetPixels32(_pixels);
        _texture.Apply(false);

        _markers.Clear();
        _markers.indexFormat = _markerVertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        _markers.SetVertices(_markerVertices);
        _markers.SetColors(_markerColours);
        _markers.SetTriangles(_markerIndices, 0);
    }

    /// <summary>
    /// Draws the cells over the grid's floor rectangle (origin is cell (0,0)'s min corner) and the
    /// markers. Only from OnDrawGizmos, where the current camera is the one drawing gizmos.
    /// </summary>
    public void Draw(Vector3 origin, Vector2 extent)
    {
        if (_failed || _texture == null) return;

        _cellMaterial.SetPass(0);
        Graphics.DrawMeshNow(_quad, Matrix4x4.TRS(origin + Vector3.up * Lift, Quaternion.identity, new Vector3(extent.x, 1f, extent.y)));
        if (_markerIndices.Count == 0) return;
        _markerMaterial.SetPass(0);
        Graphics.DrawMeshNow(_markers, Matrix4x4.identity);
    }

    /// <summary>Frees the texture, meshes and materials. The grid calls it when disabled.</summary>
    public void Dispose()
    {
        Object.DestroyImmediate(_texture);
        Object.DestroyImmediate(_quad);
        Object.DestroyImmediate(_markers);
        Object.DestroyImmediate(_cellMaterial);
        Object.DestroyImmediate(_markerMaterial);
        _texture = null;
        _shownKey = long.MinValue;
    }

    private void Allocate(int width, int depth)
    {
        if (_cellMaterial == null)
        {
            // Unlit, alpha-blended, texture times vertex colour: what both the cells and the markers need.
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Debug.LogError("GridOverlay: the Sprites/Default shader wasn't found, so the PathGrid gizmo can't draw its cells.");
                _failed = true;
                return;
            }
            _cellMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _markerMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, mainTexture = Texture2D.whiteTexture };
            _quad = BuildQuad();
            _markers = new Mesh { hideFlags = HideFlags.HideAndDontSave, name = "PathGrid markers" };
        }

        Object.DestroyImmediate(_texture);
        _texture = new Texture2D(width, depth, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Point,   // hard cell edges
            wrapMode = TextureWrapMode.Clamp,
            name = "PathGrid cells",
        };
        _pixels = new Color32[width * depth];
        _cellMaterial.mainTexture = _texture;
    }

    // A unit square on XZ from (0,0) to (1,1), UVs matching, so a texel lands on its cell.
    private static Mesh BuildQuad()
    {
        var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, name = "PathGrid quad" };
        mesh.SetVertices(new List<Vector3> { new(0f, 0f, 0f), new(0f, 0f, 1f), new(1f, 0f, 1f), new(1f, 0f, 0f) });
        mesh.SetUVs(0, new List<Vector2> { new(0f, 0f), new(0f, 1f), new(1f, 1f), new(1f, 0f) });
        mesh.SetColors(new List<Color32> { Color.white, Color.white, Color.white, Color.white });
        mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        return mesh;
    }
}
#endif
