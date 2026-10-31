using System;
using Barotrauma.LuaCs.Data;

namespace PrimaryPackage.Core;

public interface ISettingRef {
    string Name { get; }

    void Bind();

    void Drop();
}

public sealed class SettingRef<T> : ISettingRef where T : struct, IEquatable<T>, IConvertible {
    private readonly string name;
    private readonly T fallback;
    private readonly Action<ISettingBase> onChanged;

    private ISettingBase<T> source;
    private T current;
    private int revision;

    public SettingRef(string name, T fallback) {
        this.name = name;
        this.fallback = fallback;
        current = fallback;
        onChanged = _ => Pull();
    }

    public string Name => name;

    public T Value => current;

    public T Fallback => fallback;

    public int Revision => revision;

    public bool Bound => source != null && !source.IsDisposed;

    public void Bind() {
        Drop();
        if (!ModConfig.TryGet(name, out ISettingBase<T> setting)) { return; }
        source = setting;
        setting.OnValueChanged += onChanged;
        Pull();
    }

    public void Drop() {
        if (source != null) {
            try { source.OnValueChanged -= onChanged; } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("SettingRef.1", noted); }
        }
        source = null;
        if (!current.Equals(fallback)) {
            current = fallback;
            revision++;
        }
    }

    public bool TrySet(T value) {
        if (source == null || source.IsDisposed) {
            if (current.Equals(value)) { return true; }
            current = value;
            revision++;
            return true;
        }
        if (!source.TrySetValue(value)) { return false; }
        ModConfig.Save(source);
        Pull();
        return true;
    }

    private void Pull() {
        T pulled = source == null || source.IsDisposed ? fallback : source.Value;
        if (current.Equals(pulled)) { return; }
        current = pulled;
        revision++;
    }
}
