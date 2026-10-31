using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IEngine.Core;
using Microsoft.Xna.Framework.Graphics;

namespace IEngine.Render;

sealed class SpriteSubmitter : IDisposable
{
    public const int MaxQuads = 16383;
    const int RingVertices = 1 << 17;
    const int Stride = 24;

    struct Run
    {
        public Texture2D Texture;
        public Effect Effect;
        public Dictionary<string, object> Params;
        public int First, Quads;
    }

    readonly GraphicsDevice device;
    readonly DynamicVertexBuffer vertices;
    readonly IndexBuffer quads;
    readonly VertexPositionColorTexture[] staging = new VertexPositionColorTexture[MaxQuads * 4];
    float[] keys = new float[1024];
    Run[] runs = new Run[256];
    VertexRing ring = new(RingVertices);

    public long Batches, Sprites, Draws, Sorted;
    public long Discards => ring.Discards;

    public SpriteSubmitter(GraphicsDevice device)
    {
        this.device = device;
        vertices = new DynamicVertexBuffer(device, VertexPositionColorTexture.VertexDeclaration, RingVertices, BufferUsage.WriteOnly);
        quads = new IndexBuffer(device, IndexElementSize.SixteenBits, MaxQuads * 6, BufferUsage.WriteOnly);
        FillQuads();
        device.DeviceReset += OnReset;
    }

    public GraphicsDevice Device => device;
    public bool Usable => !vertices.IsDisposed && !quads.IsDisposed && !device.IsDisposed;

    void OnReset(object sender, EventArgs e)
    {
        FillQuads();
        ring.Invalidate();
    }

    void FillQuads()
    {
        var pattern = new short[MaxQuads * 6];
        for (int q = 0, i = 0; q < MaxQuads; q++, i += 6)
        {
            int v = q * 4;
            pattern[i] = (short)v;
            pattern[i + 1] = (short)(v + 1);
            pattern[i + 2] = (short)(v + 2);
            pattern[i + 3] = (short)(v + 1);
            pattern[i + 4] = (short)(v + 3);
            pattern[i + 5] = (short)(v + 2);
        }
        quads.SetData(pattern);
    }

    internal void Submit(object batcherObject, SpriteSortMode sortMode, EffectPass defaultPass)
    {
        var batcher = (SpriteBatcher)batcherObject;
        int count = batcher._batchItemCount;
        if (count == 0) { return; }
        SpriteBatchItem[] items = batcher._batchItemList;
        batcher._batchItemCount = 0;
        if ((uint)(sortMode - SpriteSortMode.Texture) <= 2u) { Sort(items, count); }
        device._graphicsMetrics._spriteCount += count;
        Batches++;
        Sprites += count;
        for (int done = 0; done < count;)
        {
            int n = Math.Min(count - done, MaxQuads);
            int runCount = Pack(items, done, n);
            int baseVertex = ring.Reserve(n * 4, out bool discard);
            vertices.SetData(baseVertex * Stride, staging, 0, n * 4, Stride, discard ? SetDataOptions.Discard : SetDataOptions.NoOverwrite);
            device.SetVertexBuffer(vertices);
            device.Indices = quads;
            for (int r = 0; r < runCount; r++) { Draw(ref runs[r], baseVertex, defaultPass); }
            Array.Clear(runs, 0, runCount);
            done += n;
        }
    }

    void Sort(SpriteBatchItem[] items, int count)
    {
        if (keys.Length < count) { keys = new float[Math.Max(count, keys.Length * 2)]; }
        for (int i = 0; i < count; i++) { keys[i] = items[i].SortKey; }
        KeyedIntroSort.Sort(keys, items, count);
        Sorted += count;
    }

    int Pack(SpriteBatchItem[] items, int from, int n)
    {
        ref VertexPositionColorTexture dst = ref MemoryMarshal.GetArrayDataReference(staging);
        int runCount = 0;
        Texture2D texture = null;
        Effect effect = null;
        Dictionary<string, object> parameters = null;
        for (int i = 0; i < n; i++)
        {
            SpriteBatchItem item = items[from + i];
            if (i == 0 || item.Texture != texture || item.Effect.Effect != effect || item.Effect.Params != parameters)
            {
                texture = item.Texture;
                effect = item.Effect.Effect;
                parameters = item.Effect.Params;
                if (runCount == runs.Length) { Array.Resize(ref runs, runs.Length * 2); }
                runs[runCount++] = new Run { Texture = texture, Effect = effect, Params = parameters, First = i };
            }
            runs[runCount - 1].Quads++;
            ref VertexPositionColorTexture v = ref Unsafe.Add(ref dst, i * 4);
            v = item.vertexTL;
            Unsafe.Add(ref v, 1) = item.vertexTR;
            Unsafe.Add(ref v, 2) = item.vertexBL;
            Unsafe.Add(ref v, 3) = item.vertexBR;
            item.Texture = null;
            item.Effect = default;
        }
        return runCount;
    }

    void Draw(ref Run run, int baseVertex, EffectPass defaultPass)
    {
        if (run.Effect == null || run.Params == null) { defaultPass.Apply(); }
        else { new SpriteBatch.EffectWithParams(run.Effect, run.Params).Apply(); }
        device.Textures[0] = run.Texture;
        int first = baseVertex + run.First * 4, primitives = run.Quads * 2;
        if (run.Effect == null)
        {
            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, first, 0, primitives);
            Draws++;
            return;
        }
        foreach (EffectPass pass in run.Effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            device.Textures[0] = run.Texture;
            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, first, 0, primitives);
            Draws++;
        }
    }

    public void Dispose()
    {
        device.DeviceReset -= OnReset;
        vertices.Dispose();
        quads.Dispose();
    }
}
