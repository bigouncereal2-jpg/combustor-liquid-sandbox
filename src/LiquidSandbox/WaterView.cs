using System.Runtime.InteropServices;
using Il2CppGasSim;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LiquidSandbox.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiquidSandbox;

/// <summary>Draws the water. Full cells go into the game's own one-pixel-per-cell grid texture,
/// before it adds its cursor and part markings. Part-filled cells are drawn on top as a mesh of
/// exact rectangles, filled from the side gravity points to, so the surface sits at the real
/// water level inside each cell at any zoom.</summary>
internal sealed unsafe class WaterView
{
    // Light (surface / falling) and deep colours per liquid: water, gasoline, diesel.
    static readonly Color32[] Light = { default, new(78, 168, 245, 255), new(250, 214, 96, 255), new(204, 146, 62, 255) };
    static readonly Color32[] Deep = { default, new(24, 76, 186, 255), new(196, 136, 28, 255), new(104, 64, 20, 255) };
    const int DepthSteps = 16;
    const float HazeStrength = 0.18f;
    const MeshUpdateFlags Quiet = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontResetBoneBounds
        | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds;

    [StructLayout(LayoutKind.Sequential)]
    struct Vertex
    {
        public float X, Y, Z;
        public uint Color;
    }

    readonly Color32[,] _depthColors = new Color32[LiquidGrid.KindCount + 1, DepthSteps + 1];
    Vertex[] _vertices = new Vertex[4 * 512];
    int[] _indices = Array.Empty<int>();
    int _quadCapacity;
    GameObject _object;
    Mesh _mesh;
    MeshRenderer _meshRenderer;
    IntPtr _surface;
    Vector2 _pivot;
    float _cellSize = 1f;
    int _width, _height;
    Il2CppStructArray<VertexAttributeDescriptor> _layout;

    public WaterView()
    {
        for (int k = 1; k <= LiquidGrid.KindCount; k++)
            for (int d = 0; d <= DepthSteps; d++) _depthColors[k, d] = Color32.Lerp(Light[k], Deep[k], (float)d / DepthSteps);
    }

    public void Draw(GasSimRenderer renderer, LiquidGrid water, ReadOnlySpan<byte> solid, Span<Color32> pixels)
    {
        if (pixels.Length < water.Width * water.Height || solid.Length < water.Width * water.Height) return;
        PaintFullCells(water, solid, pixels);
        if (!Ensure(renderer, water)) return;
        int quads = BuildPartialCells(water, solid);
        Upload(quads);
    }

    /// <summary>Full cells, shaded darker the more water sits above them along gravity.</summary>
    void PaintFullCells(LiquidGrid water, ReadOnlySpan<byte> solid, Span<Color32> pixels)
    {
        int w = water.Width, h = water.Height, stride = water.Stride;
        int[] cells = water.Water;
        bool level = water.Strength > 0f;
        // Walk each line from the side gravity points away from, towards the side it points to.
        int lines, length, firstX, firstY, stepX, stepY, lineX, lineY;
        switch (water.Gravity)
        {
            case GridDir.Up: lines = w; length = h; firstX = 0; firstY = 0; stepX = 0; stepY = 1; lineX = 1; lineY = 0; break;
            case GridDir.Left: lines = h; length = w; firstX = w - 1; firstY = 0; stepX = -1; stepY = 0; lineX = 0; lineY = 1; break;
            case GridDir.Right: lines = h; length = w; firstX = 0; firstY = 0; stepX = 1; stepY = 0; lineX = 0; lineY = 1; break;
            default: lines = w; length = h; firstX = 0; firstY = h - 1; stepX = 0; stepY = -1; lineX = 1; lineY = 0; break;
        }
        for (int line = 0; line < lines; line++)
        {
            int x = firstX + line * lineX, y = firstY + line * lineY;
            int depth = 0;
            for (int n = 0; n < length; n++, x += stepX, y += stepY)
            {
                int k = y * w + x;
                if (solid[k] != 0)
                {
                    depth = 0;
                    continue;
                }
                int i = (y + 1) * stride + x + 1;
                int amount = cells[i];
                if (amount <= 0)
                {
                    depth = 0;
                    Haze(water, i, ref pixels[k]);
                    continue;
                }
                int kind = water.Kind[i];
                if (water.Fire[i] != 0) { pixels[k] = Flame(i, water.Ticks); depth = 0; continue; }
                if (amount >= LiquidGrid.Full)
                    pixels[k] = level && water.Rests(i) ? _depthColors[kind, Math.Min(depth, DepthSteps)] : Light[kind];
                depth = water.Rests(i) ? depth + 1 : 0;
            }
        }
    }

    static Color32 Flame(int i, long tick)
    {
        uint h = (uint)i * 2654435761u ^ (uint)(tick / 3) * 2246822519u;
        h ^= h >> 15;
        return Color32.Lerp(new Color32(255, 96, 24, 255), new Color32(255, 214, 90, 255), (h & 255) / 255f);
    }

    /// <summary>Vapour shows as a faint haze of its liquid's colour, by how saturated the air is.</summary>
    static void Haze(LiquidGrid water, int i, ref Color32 pixel)
    {
        float best = 0f;
        int kind = 0;
        for (int k = 1; k <= LiquidGrid.KindCount; k++)
        {
            int v = water.Vapor[k][i];
            if (v == 0) continue;
            float share = Math.Min(1f, (float)v / LiquidGrid.Saturation[k]);
            if (share > best) { best = share; kind = k; }
        }
        if (kind != 0 && best > 0.35f) pixel = Color32.Lerp(pixel, Light[kind], (best - 0.35f) / 0.65f * HazeStrength);
    }

    bool Ensure(GasSimRenderer renderer, LiquidGrid water)
    {
        var surface = renderer != null ? renderer.Surface : null;
        if (surface == null) return false;
        if (_object != null && _mesh != null && _surface == surface.Pointer && _width == water.Width && _height == water.Height) return true;
        Dispose();
        var sprite = surface.GetComponent<SpriteRenderer>();
        if (sprite == null || sprite.sprite == null) return false;
        _surface = surface.Pointer;
        _width = water.Width;
        _height = water.Height;
        _pivot = sprite.sprite.pivot;
        float ppu = sprite.sprite.pixelsPerUnit;
        _cellSize = ppu > 0f ? 1f / ppu : 1f;

        _object = new GameObject("LiquidSandbox Water");
        _object.transform.SetParent(surface, false);
        var filter = _object.AddComponent<MeshFilter>();
        _meshRenderer = _object.AddComponent<MeshRenderer>();
        _meshRenderer.sharedMaterial = sprite.sharedMaterial;
        _meshRenderer.sortingLayerID = sprite.sortingLayerID;
        _meshRenderer.sortingOrder = sprite.sortingOrder + 1;
        _meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        _meshRenderer.receiveShadows = false;
        _mesh = new Mesh { name = "LiquidSandbox Water" };
        filter.sharedMesh = _mesh;
        _layout ??= Layout();
        _quadCapacity = 0;
        return true;
    }

    static Il2CppStructArray<VertexAttributeDescriptor> Layout()
    {
        var layout = new Il2CppStructArray<VertexAttributeDescriptor>(2);
        layout[0] = new VertexAttributeDescriptor
        {
            _attribute_k__BackingField = VertexAttribute.Position,
            _format_k__BackingField = VertexAttributeFormat.Float32,
            _dimension_k__BackingField = 3,
            _stream_k__BackingField = 0,
        };
        layout[1] = new VertexAttributeDescriptor
        {
            _attribute_k__BackingField = VertexAttribute.Color,
            _format_k__BackingField = VertexAttributeFormat.UNorm8,
            _dimension_k__BackingField = 4,
            _stream_k__BackingField = 0,
        };
        return layout;
    }

    static uint Pack(Color32 c, byte alpha) => c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)alpha << 24);

    int BuildPartialCells(LiquidGrid water, ReadOnlySpan<byte> solid)
    {
        int w = water.Width, h = water.Height, stride = water.Stride;
        int[] cells = water.Water;
        bool level = water.Strength > 0f;
        GridDir dir = water.Gravity;
        float size = _cellSize, ox = -_pivot.x * size, oy = -_pivot.y * size;
        int quads = 0;
        for (int y = 0; y < h; y++)
        {
            int row = (y + 1) * stride + 1, srow = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                int amount = cells[i];
                if (amount <= 0 || amount >= LiquidGrid.Full || solid[srow + x] != 0) continue;
                float f = (float)amount / LiquidGrid.Full;
                float x0 = ox + x * size, y0 = oy + y * size, x1 = x0 + size, y1 = y0 + size;
                Color32 tint = water.Fire[i] != 0 ? Flame(i, water.Ticks) : Light[water.Kind[i]];
                uint color = Pack(tint, 255);
                if (!level)
                {
                    // No gravity, no surface: show how full the cell is as how strongly it is tinted.
                    color = Pack(tint, (byte)Math.Clamp((int)(f * 255f + 0.5f), 24, 255));
                }
                else if (water.Rests(i))
                {
                    switch (dir)
                    {
                        case GridDir.Down: y1 = y0 + f * size; break;
                        case GridDir.Up: y0 = y1 - f * size; break;
                        case GridDir.Left: x1 = x0 + f * size; break;
                        default: x0 = x1 - f * size; break;
                    }
                }
                else
                {
                    // Falling: a stream as wide as the cell is full, running along gravity.
                    if (f >= 0.25f)
                    {
                        float inset = (1f - f) * 0.5f * size;
                        if (dir is GridDir.Down or GridDir.Up) { x0 += inset; x1 -= inset; }
                        else { y0 += inset; y1 -= inset; }
                    }
                    else
                    {
                        // A little falling liquid is a droplet with the same area, not a hairline.
                        float inset = (1f - MathF.Sqrt(f)) * 0.5f * size;
                        x0 += inset; x1 -= inset; y0 += inset; y1 -= inset;
                    }
                }
                if ((quads + 1) * 4 > _vertices.Length) Array.Resize(ref _vertices, _vertices.Length * 2);
                int v = quads * 4;
                _vertices[v] = new Vertex { X = x0, Y = y0, Color = color };
                _vertices[v + 1] = new Vertex { X = x0, Y = y1, Color = color };
                _vertices[v + 2] = new Vertex { X = x1, Y = y1, Color = color };
                _vertices[v + 3] = new Vertex { X = x1, Y = y0, Color = color };
                quads++;
            }
        }
        return quads;
    }

    void Upload(int quads)
    {
        if (quads > _quadCapacity)
        {
            int capacity = Math.Max(256, _quadCapacity);
            while (capacity < quads) capacity *= 2;
            _indices = new int[capacity * 6];
            for (int q = 0, i = 0; q < capacity; q++)
            {
                int v = q * 4;
                _indices[i++] = v; _indices[i++] = v + 1; _indices[i++] = v + 2;
                _indices[i++] = v; _indices[i++] = v + 2; _indices[i++] = v + 3;
            }
            _mesh.SetVertexBufferParams(capacity * 4, _layout);
            _mesh.SetIndexBufferParams(capacity * 6, IndexFormat.UInt32);
            fixed (int* p = _indices) _mesh.InternalSetIndexBufferData((IntPtr)p, 0, 0, capacity * 6, sizeof(int), Quiet);
            _mesh.subMeshCount = 1;
            _quadCapacity = capacity;
            _mesh.bounds = GridBounds();
        }
        if (quads > 0)
            fixed (Vertex* p = _vertices) _mesh.InternalSetVertexBufferData(0, (IntPtr)p, 0, 0, quads * 4, sizeof(Vertex), Quiet);
        var sub = new SubMeshDescriptor
        {
            _indexStart_k__BackingField = 0,
            _indexCount_k__BackingField = quads * 6,
            _topology_k__BackingField = MeshTopology.Triangles,
            _baseVertex_k__BackingField = 0,
            _firstVertex_k__BackingField = 0,
            _vertexCount_k__BackingField = Math.Max(quads * 4, 1),
            _bounds_k__BackingField = GridBounds(),
        };
        _mesh.SetSubMesh(0, sub, Quiet);
        if (_meshRenderer.enabled != quads > 0) _meshRenderer.enabled = quads > 0;
    }

    Bounds GridBounds()
    {
        float size = _cellSize;
        var min = new Vector3(-_pivot.x * size, -_pivot.y * size, -1f);
        var extent = new Vector3(_width * size, _height * size, 2f);
        return new Bounds(min + extent * 0.5f, extent);
    }

    public void Reset() => Dispose();

    public void Dispose()
    {
        if (_object != null) UnityEngine.Object.Destroy(_object);
        if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
        _object = null;
        _mesh = null;
        _meshRenderer = null;
        _surface = IntPtr.Zero;
        _quadCapacity = 0;
    }
}
