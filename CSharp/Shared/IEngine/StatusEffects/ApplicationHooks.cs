using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Compatibility;
using Microsoft.Xna.Framework;

namespace IEngine.StatusEffects;

static class ApplicationHooks
{
    sealed class SubscriptionHint
    {
        public readonly EventService Owner;
        public readonly ConcurrentDictionary<EventService.TypeStringKey, LuaCsFunc> Subscribers;

        public SubscriptionHint(EventService owner, ConcurrentDictionary<EventService.TypeStringKey, LuaCsFunc> subscribers)
        {
            Owner = owner;
            Subscribers = subscribers;
        }
    }

    sealed class EventKey
    {
        public readonly string Identifier;
        public readonly string Name;
        public readonly EventService.TypeStringKey Key;
        public volatile SubscriptionHint Hint;

        public EventKey(string identifier)
        {
            Identifier = identifier;
            Key = new EventService.TypeStringKey("statusEffect.apply." + identifier);
            // EventService normalizes this name before lookup and never passes
            // it to callbacks. Reuse that normalized string on normal dispatch.
            Name = Key.TypeName;
        }
    }

    // Names belong to loaded content for the current round; StopAll releases them.
    static readonly ConcurrentDictionary<string, EventKey> keys = new(StringComparer.Ordinal);
    static volatile EventKey last;
    public static long Skipped, Forwarded;

    public static bool? Call(ILuaCsHook hook, Identifier identifier, StatusEffect effect, float deltaTime,
        Entity entity, IReadOnlyList<ISerializableEntity> targets, Vector2? worldPosition, bool specialize)
    {
        if (!specialize || hook == null || hook.GetType() != typeof(EventService))
        {
            return Original(hook, identifier, effect, deltaTime, entity, targets, worldPosition);
        }
        string value = identifier.Value;
        EventKey key = last;
        if (key == null || !ReferenceEquals(key.Identifier, value))
        {
            if (!keys.TryGetValue(value, out key)) { key = keys.GetOrAdd(value, static id => new EventKey(id)); }
            last = key;
        }
        var service = (EventService)hook;
        // A hint can only select normal dispatch, which reads live subscriptions.
        // A nonempty bucket retained after Reset cannot resurrect a callback.
        SubscriptionHint hint = key.Hint;
        if (hint != null && ReferenceEquals(hint.Owner, service) && !hint.Subscribers.IsEmpty)
        {
            return Forward(service, key.Name, effect, deltaTime, entity, targets, worldPosition);
        }
        if (service._luaLegacyEventsSubscribers.TryGetValue(key.Key, out var current) && !current.IsEmpty)
        {
            key.Hint = new SubscriptionHint(service, current);
            return Forward(service, key.Name, effect, deltaTime, entity, targets, worldPosition);
        }
        // Writers keep this gate for their entire mutation. The absence check is
        // synchronized with Add/Remove/Reset/Dispose without an async lock token.
        // Under contention the normal Call path retains its waiting behavior.
        var gate = service._operationsLock._writeSemaphore;
        bool absent = false;
        if (gate.Wait(0))
        {
            try
            {
                absent = !service.IsDisposed &&
                    (!service._luaLegacyEventsSubscribers.TryGetValue(key.Key, out var subscribers) || subscribers.IsEmpty);
            }
            finally { gate.Release(); }
        }
        if (absent)
        {
            System.Threading.Interlocked.Increment(ref Skipped);
            return null;
        }
        return Forward(service, key.Name, effect, deltaTime, entity, targets, worldPosition);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool? Forward(EventService service, string name, StatusEffect effect, float deltaTime,
        Entity entity, IReadOnlyList<ISerializableEntity> targets, Vector2? worldPosition)
    {
        System.Threading.Interlocked.Increment(ref Forwarded);
        return service.Call<bool?>(name, effect, deltaTime, entity, targets, worldPosition);
    }

    static bool? Original(ILuaCsHook hook, Identifier identifier, StatusEffect effect, float deltaTime,
        Entity entity, IReadOnlyList<ISerializableEntity> targets, Vector2? worldPosition) =>
        hook.Call<bool?>("statusEffect.apply." + identifier.ToString(), effect, deltaTime, entity, targets, worldPosition);

    public static void Reset()
    {
        last = null;
        keys.Clear();
    }
}
