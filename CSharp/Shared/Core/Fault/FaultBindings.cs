using System;
using System.Collections.Generic;
using System.Reflection;

namespace PrimaryPackage.Core.Fault;

public sealed class FaultBindings {
    private sealed class Snapshot {
        public readonly Dictionary<MethodBase, FaultSite> Methods;
        public readonly Dictionary<Type, FaultSite> Entities;

        public Snapshot(Dictionary<MethodBase, FaultSite> methods, Dictionary<Type, FaultSite> entities) {
            Methods = methods;
            Entities = entities;
        }
    }

    private static readonly Snapshot empty = new Snapshot(new Dictionary<MethodBase, FaultSite>(), new Dictionary<Type, FaultSite>());
    private readonly object gate = new object();
    private volatile Snapshot current = empty;

    public void Bind(MethodBase method, FaultSite site) {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(site);
        lock (gate) {
            Snapshot before = current;
            if (before.Methods.TryGetValue(method, out FaultSite existing)) {
                if (!ReferenceEquals(existing, site)) { throw new InvalidOperationException("The method already has a fault site."); }
                return;
            }
            Dictionary<Type, FaultSite> entities = before.Entities;
            if (site.PerEntity) {
                Type root = method.DeclaringType;
                if (method.IsStatic || root == null || !root.IsClass || method.ContainsGenericParameters || root.ContainsGenericParameters) {
                    throw new ArgumentException("An entity fault site requires a closed instance method on a class.", nameof(method));
                }
                foreach (Type bound in entities.Keys) {
                    if (bound.IsAssignableFrom(root) || root.IsAssignableFrom(bound)) {
                        throw new InvalidOperationException("Entity fault sites must have disjoint class hierarchies.");
                    }
                }
                entities = Copy(entities);
                entities.Add(root, site);
            }
            Dictionary<MethodBase, FaultSite> methods = Copy(before.Methods);
            methods.Add(method, site);
            current = new Snapshot(methods, entities);
        }
    }

    public bool TryGet(MethodBase method, out FaultSite site) {
        site = null;
        return method != null && current.Methods.TryGetValue(method, out site);
    }

    public bool TryGetEntity(object entity, out FaultSite site) {
        site = null;
        if (entity == null) { return false; }
        Dictionary<Type, FaultSite> entities = current.Entities;
        for (Type type = entity.GetType(); type != null; type = type.BaseType) {
            if (entities.TryGetValue(type, out site)) { return true; }
        }
        return false;
    }

    public void Clear() {
        lock (gate) { current = empty; }
    }

    private static Dictionary<TKey, FaultSite> Copy<TKey>(Dictionary<TKey, FaultSite> source) where TKey : notnull {
        var copy = new Dictionary<TKey, FaultSite>(source.Count + 1, source.Comparer);
        foreach (KeyValuePair<TKey, FaultSite> pair in source) { copy.Add(pair.Key, pair.Value); }
        return copy;
    }
}
