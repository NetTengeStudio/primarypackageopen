using System;
using System.Runtime.CompilerServices;
using System.Numerics;

namespace PrimaryPackage.MotionBlur;

public readonly struct MotionSample {
    public readonly float X;
    public readonly float Y;
    public readonly float Coherence;

    public MotionSample(float x, float y, float coherence) {
        X = x;
        Y = y;
        Coherence = coherence;
    }
}

public sealed class MotionField {
    public const int Tile = 64;

    private const int VectorThreshold = 16;

    private readonly float[] velocityX = new float[Tile];
    private readonly float[] velocityY = new float[Tile];
    private readonly float[] weight = new float[Tile];

    private int staged;
    private int total;
    private float scale;
    private float offsetX;
    private float offsetY;
    private float sumWeight;
    private float sumX;
    private float sumY;
    private float sumSquare;

    public int Count => total;

    public void Begin(float cameraVelocityX, float cameraVelocityY, float zoom) {
        staged = 0;
        total = 0;
        scale = zoom;
        offsetX = -cameraVelocityX * zoom;
        offsetY = cameraVelocityY * zoom;
        sumWeight = 0.0f;
        sumX = 0.0f;
        sumY = 0.0f;
        sumSquare = 0.0f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(float worldVelocityX, float worldVelocityY, float share) {
        if (share <= 0.0f) { return; }
        velocityX[staged] = worldVelocityX;
        velocityY[staged] = worldVelocityY;
        weight[staged] = share;
        staged++;
        total++;
        if (staged == Tile) { Flush(); }
    }

    public MotionSample Resolve() {
        Flush();
        if (sumWeight <= 1e-6f) { return default; }
        float inverse = 1.0f / sumWeight;
        float meanX = sumX * inverse;
        float meanY = sumY * inverse;
        float meanSquare = meanX * meanX + meanY * meanY;
        float variance = sumSquare * inverse - meanSquare;
        if (variance < 0.0f) { variance = 0.0f; }
        float magnitude = MathF.Sqrt(meanSquare);
        float spread = MathF.Sqrt(variance);
        return new MotionSample(meanX, meanY, magnitude / (magnitude + spread + 1e-4f));
    }

    public static bool Shape(MotionSample sample, float shutter, float minimum, float soft, float maximum,
        out float x, out float y) {
        x = 0.0f;
        y = 0.0f;
        float gain = shutter * sample.Coherence;
        float px = sample.X * gain;
        float py = sample.Y * gain;
        float lengthSquare = px * px + py * py;
        if (lengthSquare < minimum * minimum) { return false; }
        float length = MathF.Sqrt(lengthSquare);
        if (length > maximum) {
            float trim = maximum / length;
            px *= trim;
            py *= trim;
        }
        else if (length < soft) {
            float ramp = PrimaryPackage.Core.Scalar.Smooth((length - minimum) / (soft - minimum));
            px *= ramp;
            py *= ramp;
        }
        x = px;
        y = py;
        return true;
    }

    private void Flush() {
        int i = 0;
        int lanes = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && staged >= VectorThreshold && staged >= lanes) {
            Vector<float> accWeight = Vector<float>.Zero;
            Vector<float> accX = Vector<float>.Zero;
            Vector<float> accY = Vector<float>.Zero;
            Vector<float> accSquare = Vector<float>.Zero;
            Vector<float> forward = new Vector<float>(scale);
            Vector<float> inverted = new Vector<float>(-scale);
            Vector<float> shiftX = new Vector<float>(offsetX);
            Vector<float> shiftY = new Vector<float>(offsetY);
            for (; i <= staged - lanes; i += lanes) {
                Vector<float> w = new Vector<float>(weight, i);
                Vector<float> sx = new Vector<float>(velocityX, i) * forward + shiftX;
                Vector<float> sy = new Vector<float>(velocityY, i) * inverted + shiftY;
                accWeight += w;
                accX += w * sx;
                accY += w * sy;
                accSquare += w * (sx * sx + sy * sy);
            }
            sumWeight += Vector.Sum(accWeight);
            sumX += Vector.Sum(accX);
            sumY += Vector.Sum(accY);
            sumSquare += Vector.Sum(accSquare);
        }
        for (; i < staged; i++) {
            float w = weight[i];
            float sx = velocityX[i] * scale + offsetX;
            float sy = velocityY[i] * -scale + offsetY;
            sumWeight += w;
            sumX += w * sx;
            sumY += w * sy;
            sumSquare += w * (sx * sx + sy * sy);
        }
        staged = 0;
    }
}
