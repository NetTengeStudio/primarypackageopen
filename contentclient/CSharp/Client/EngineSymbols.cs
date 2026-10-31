using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SubSeal.Client
{
    internal static class EngineSymbols
    {
        internal static MethodInfo DecompressFileToStream { get; private set; }

        internal static ConstructorInfo SubmarineConstructor { get; private set; }

        internal static MethodInfo PreviewCreate { get; private set; }

        internal static MethodInfo PreviewClose { get; private set; }

        internal static MethodInfo PreviewCard { get; private set; }

        internal static MethodInfo PreviewGenerateMeshes { get; private set; }

        internal static MethodInfo PreviewRender { get; private set; }

        internal static MethodInfo PreviewDispose { get; private set; }

        internal static MethodInfo CampaignRetry { get; private set; }
        internal static MethodInfo TabMenuPreview { get; private set; }
        internal static MethodInfo SelectionPreviewImage { get; private set; }
        internal static MethodInfo SelectionRefresh { get; private set; }
        internal static MethodInfo SelectionSelect { get; private set; }

        internal static FieldInfo PreviewBounds { get; private set; }

        internal static FieldInfo FileCorrupted { get; private set; }

        internal static string Missing { get; private set; }

        internal static bool Resolved => Missing is null;

        internal static bool PreviewUiAvailable =>
            PreviewCard is not null && PreviewGenerateMeshes is not null &&
            PreviewRender is not null && PreviewBounds is not null;

        internal static bool Resolve()
        {
            Missing = null;

            DecompressFileToStream = typeof(SaveUtil).GetMethod(
                nameof(SaveUtil.DecompressFileToStream),
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);

            if (DecompressFileToStream is null || DecompressFileToStream.ReturnType != typeof(Stream))
            {
                Missing = "SaveUtil.DecompressFileToStream";
                return false;
            }

            SubmarineConstructor = typeof(Submarine).GetConstructor(
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(SubmarineInfo), typeof(bool), typeof(Func<Submarine, System.Collections.Generic.List<MapEntity>>), typeof(IdRemap) },
                null);

            if (SubmarineConstructor is null)
            {
                Missing = "Submarine.ctor";
                return false;
            }

            PreviewCreate = typeof(SubmarinePreview).GetMethod(
                nameof(SubmarinePreview.Create),
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(SubmarineInfo) },
                null);

            if (PreviewCreate is null)
            {
                Missing = "SubmarinePreview.Create";
                return false;
            }

            PreviewClose = typeof(SubmarinePreview).GetMethod(
                nameof(SubmarinePreview.Close),
                BindingFlags.Public | BindingFlags.Static,
                null,
                System.Type.EmptyTypes,
                null);

            if (PreviewClose is null)
            {
                Missing = "SubmarinePreview.Close";
                return false;
            }

            PreviewCard = typeof(SubmarineInfo).GetMethod(
                nameof(SubmarineInfo.CreatePreviewWindow),
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(GUIComponent) },
                null);

            PreviewGenerateMeshes = typeof(SubmarinePreview).GetMethod(
                "GeneratePreviewMeshes",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                System.Type.EmptyTypes,
                null);

            if (PreviewGenerateMeshes is not null && PreviewGenerateMeshes.ReturnType != typeof(Task))
            {
                PreviewGenerateMeshes = null;
            }

            PreviewRender = typeof(SubmarinePreview).GetMethod(
                "RenderSubmarine",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(SpriteBatch), typeof(Rectangle), typeof(GUIComponent) },
                null);

            PreviewDispose = typeof(SubmarinePreview).GetMethod(
                "Dispose",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                System.Type.EmptyTypes,
                null);

            PreviewBounds = typeof(SubmarinePreview).GetField(
                "bounds",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (PreviewBounds is not null && PreviewBounds.FieldType != typeof(ValueTuple<Vector2, Vector2>))
            {
                PreviewBounds = null;
            }

            CampaignRetry = typeof(GameClient).GetMethod(
                "TryRequestMissingCampaignSubs",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                System.Type.EmptyTypes,
                null);

            // Resolve internal game UI types by exact method signatures.
            const BindingFlags guiFlags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            Assembly clientAssembly = typeof(SubmarinePreview).Assembly;
            Type tabMenuType = clientAssembly.GetType("Barotrauma.TabMenu");
            TabMenuPreview = tabMenuType?.GetMethod("CreateSubmarineInfo", guiFlags, null,
                new[] { typeof(GUIFrame), typeof(Submarine) }, null);

            Type selectionType = clientAssembly.GetType("Barotrauma.SubmarineSelection");
            SelectionPreviewImage = selectionType?.GetMethod("GetPreviewImage", guiFlags, null,
                new[] { typeof(SubmarineInfo) }, null);
            SelectionRefresh = selectionType?.GetMethod("RefreshSubmarineDisplay", guiFlags, null,
                new[] { typeof(bool), typeof(bool) }, null);
            SelectionSelect = selectionType?.GetMethod("SelectSubmarine", guiFlags, null,
                new[] { typeof(SubmarineInfo), typeof(Rectangle) }, null);

            FileCorrupted = typeof(SubmarineInfo).GetField(
                $"<{nameof(SubmarineInfo.IsFileCorrupted)}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (FileCorrupted is null || FileCorrupted.FieldType != typeof(bool)) { FileCorrupted = null; }

            return true;
        }

        internal static void SetPreviewBounds(SubmarinePreview preview)
            => PreviewBounds?.SetValue(preview, (new Vector2(-100f, -50f), new Vector2(100f, 50f)));

        internal static void MarkFileCorrupted(SubmarineInfo info)
        {
            if (FileCorrupted is null || info is null) { return; }
            FileCorrupted.SetValue(info, true);
        }
    }
}
