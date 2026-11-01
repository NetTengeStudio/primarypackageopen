using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MoreBlood
{
  public class AdvancedDecal
  {
    public static HashSet<AdvancedDecal> Decals = new();
    public static void UpdateAll()
    {
      foreach (AdvancedDecal decal in Decals) decal.Update();
    }

    private float size; public float Size
    {
      get => size;
      set
      {
        float s = value * Prefab.SizeFluctuation.Next();

        size = Math.Clamp(s, Prefab.MinSpriteSize, Prefab.MaxSpriteSize);

        LifeTime =
          Math.Clamp(
            Utils.ExpSegment(Prefab.SLStart, Prefab.SLEnd, Prefab.LifetimeExponent, s) *
            Prefab.LifetimeFluctuation.Next() * Mod.Config.GlobalDecalLifetime,
            Prefab.MinLifetime, Prefab.MaxLifetime
          );

        InitialLifeTime = LifeTime;
      }
    }
    public AdvancedDecalPrefab Prefab;
    public Sprite Sprite;
    public Hull Hull;
    private double lifeTime; public double LifeTime
    {
      get => lifeTime;
      set => lifeTime = Math.Max(0, value);
    }
    public double TimeLeft => Math.Max(0, LifeTime - (Timing.TotalTimeUnpaused - CreationTime));
    public double InitialLifeTime;
    public double LifeTimeLambda;
    public double CreationTime;
    public Color CurrentColor;
    public float Rotation;
    public Vector2 HullPosition;
    public Rectangle HullRectangle;
    public Rectangle WorldRectangle;
    public Vector2 WorldPosition
      => Hull?.Submarine is null ?
         HullPosition + Hull.Rect.Location.ToVector2() :
         HullPosition + Hull.Rect.Location.ToVector2() + Hull.Submarine.DrawPosition;
    public Vector2 SpriteSize;
    public Vector2 HalfSpriteSize;
    public void ConnectToHull(Vector2 worldPosition, Hull hull)
    {
      Hull = hull;

      Mixins.GetHullMixin(hull).AdvancedDecals.Add(this);
      HullPosition = worldPosition - hull.WorldRect.Location.ToVector2();
      HullRectangle = new Rectangle(
        (int)(HullPosition.X - HalfSpriteSize.X * Size),
        (int)(HullPosition.Y - HalfSpriteSize.Y * Size),
        (int)(SpriteSize.X * Size),
        (int)(SpriteSize.Y * Size)
      );
    }

    private Vector2 drawPos;
    public void Draw(SpriteBatch spriteBatch, float depth)
    {
      if (Sprite?.Texture == null) { return; }
      spriteBatch.Draw(Sprite.Texture, drawPos, Sprite.SourceRect, CurrentColor, Rotation, HalfSpriteSize, Size, SpriteEffects.None, depth);
    }

    public void Update()
    {
      drawPos = HullPosition + Hull.Rect.Location.ToVector2();
      if (Hull.Submarine != null) { drawPos += Hull.Submarine.DrawPosition; }
      drawPos.Y = -drawPos.Y;

      LifeTimeLambda = (Timing.TotalTimeUnpaused - CreationTime) / LifeTime;
      if (LifeTimeLambda > 1) Remove();
      CurrentColor = ColorPoint.Lerp(Prefab.Colors, LifeTimeLambda);
    }

    public AdvancedDecal(AdvancedDecalPrefab prefab)
    {
      Decals.Add(this);
      CreationTime = Timing.TotalTimeUnpaused;
      Rotation = Utils.Random.NextSingle() * Utils.Pi2;

      Prefab = prefab;
      Sprite = prefab.Sprites[Rand.Range(0, prefab.Sprites.Count)];
      CurrentColor = prefab.Colors[0].Color;
      LifeTime = 1.0f;

      SpriteSize = new Vector2(
        Sprite.SourceRect.Width,
        Sprite.SourceRect.Height
      );

      HalfSpriteSize = new Vector2(
        Sprite.SourceRect.Width / 2.0f,
        Sprite.SourceRect.Height / 2.0f
      );
    }

    public AdvancedDecal(AdvancedDecalPrefab prefab, float size) : this(prefab) => this.Size = size;
    public AdvancedDecal(AdvancedDecalPrefab prefab, Vector2 worldPosition, Hull hull) : this(prefab)
    {
      ConnectToHull(worldPosition, hull);
    }

    public static AdvancedDecal Create(string name, float size)
      => new AdvancedDecal(AdvancedDecalPrefab.GetPrefab(name), size);

    public void Remove()
    {
      Decals.Remove(this);
      if (Hull != null)
      {
        Mixins.GetHullMixin(Hull).AdvancedDecals.Remove(this);
      }
    }

  }
}
