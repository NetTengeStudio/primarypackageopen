using System;
using System.Collections.Generic;
using System.Reflection;

namespace PrimaryPackage.Core;

public static class ItemUpdatePolicy {
    public sealed class Registration : IDisposable {
        internal readonly MethodInfo[] Observers;
        internal readonly Func<object, bool> CanSkipIdle;
        private bool disposed;

        internal Registration(MethodInfo[] observers, Func<object, bool> canSkipIdle) {
            Observers = observers;
            CanSkipIdle = canSkipIdle;
        }

        public void Invalidate(object entity) {
            ObjectDisposedException.ThrowIf(disposed, this);
            Invalidated?.Invoke(entity);
        }

        public void Dispose() {
            if (disposed) { return; }
            disposed = true;
            for (int i = 0; i < Observers.Length; i++) { byMethod.Remove(Observers[i]); }
            Registration[] before = registrations;
            var after = new Registration[before.Length - 1];
            int index = Array.IndexOf(before, this);
            before.AsSpan(0, index).CopyTo(after);
            before.AsSpan(index + 1).CopyTo(after.AsSpan(index));
            registrations = after;
            revision++;
            Invalidated?.Invoke(null);
        }
    }

    private static readonly Dictionary<MethodInfo, Registration> byMethod = new Dictionary<MethodInfo, Registration>();
    private static Registration[] registrations = Array.Empty<Registration>();
    private static int revision;

    public static event Action<object> Invalidated;

    public static Registration Register(ReadOnlySpan<MethodInfo> observers, Func<object, bool> canSkipIdle) {
        if (observers.IsEmpty) { throw new ArgumentException("At least one observer is required.", nameof(observers)); }
        ArgumentNullException.ThrowIfNull(canSkipIdle);
        var unique = new HashSet<MethodInfo>();
        foreach (MethodInfo method in observers) {
            if (method == null || byMethod.ContainsKey(method) || !unique.Add(method)) {
                throw new ArgumentException("Observers must be non-null, distinct and unregistered.", nameof(observers));
            }
        }
        var registration = new Registration(observers.ToArray(), canSkipIdle);
        Registration[] before = registrations;
        var after = new Registration[before.Length + 1];
        before.CopyTo(after, 0);
        after[before.Length] = registration;
        for (int i = 0; i < registration.Observers.Length; i++) { byMethod.Add(registration.Observers[i], registration); }
        registrations = after;
        revision++;
        Invalidated?.Invoke(null);
        return registration;
    }

    public static bool CanSkipInactive(MethodInfo observer) => byMethod.ContainsKey(observer);

    public static bool CanSkipIdle(object entity) {
        int version = revision;
        Registration[] current = registrations;
        for (int i = 0; i < current.Length; i++) {
            if (!current[i].CanSkipIdle(entity)) { return false; }
        }
        return version == revision;
    }
}
