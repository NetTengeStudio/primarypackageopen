using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;

namespace PrimaryPackage.Aes {
    public readonly struct AesAction {
        public readonly string Label;
        public readonly Func<Item, bool> IsSource;
        public readonly Func<Item, List<Item>> GatherTargets;
        public readonly Action<Item> Custom;

        public AesAction(string label, Func<Item, bool> isSource, Func<Item, List<Item>> gatherTargets) {
            Label = label;
            IsSource = isSource;
            GatherTargets = gatherTargets;
            Custom = null;
        }

        public AesAction(string label, Func<Item, bool> isSource, Action<Item> custom) {
            Label = label;
            IsSource = isSource;
            GatherTargets = null;
            Custom = custom;
        }
    }

    public static class AesRegistry {
        public static readonly AesAction[] Actions = {
            new AesAction(
                "Присоединить все вентиляционные входы",
                static it => it.GetComponent<OxygenGenerator>() != null,
                AesLink.GatherVents),
            new AesAction(
                "Привязать источник холода к отсеку",
                static it => it.GetComponent<PrimaryPackage.Cold.ColdSourceComponent>() != null,
                PrimaryPackage.Cold.ColdHullPicker.Begin)
        };

        public static int FindAction(Item item) {
            for (int i = 0; i < Actions.Length; i++) {
                if (Actions[i].IsSource(item)) { return i; }
            }
            return -1;
        }

        public static void Invoke(int index, Item source) {
            AesAction action = Actions[index];
            if (action.Custom != null) {
                action.Custom(source);
                return;
            }
            if (PlayerInput.IsAltDown()) {
                AesZoneSelector.Begin(source, action.GatherTargets(source));
            } else {
                AesLink.LinkAll(source, action.GatherTargets(source));
            }
        }
    }
}
