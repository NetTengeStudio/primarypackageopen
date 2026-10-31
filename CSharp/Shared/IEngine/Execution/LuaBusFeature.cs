using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace IEngine.Execution;

sealed class LuaBusFeature : Feature
{
    sealed class ReaderToken : IDisposable
    {
        readonly Action release;
        public ReaderToken(Action release) => this.release = release;
        public void Dispose() => release();
    }

    static LuaBusFeature instance;
    static readonly ConditionalWeakTable<AsyncReaderWriterLock, Task<IDisposable>> tokens = new();
    sealed record Cached(AsyncReaderWriterLock Owner, Task<IDisposable> Token);
    static Cached last;
    static long fast;

    public override string Name => "luabus";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        var acquire = AccessTools.Method(typeof(AsyncReaderWriterLock), nameof(AsyncReaderWriterLock.AcquireReaderLock));
        if (acquire == null || AccessTools.Method(typeof(AsyncReaderWriterLock), "ReleaseReaderLock") == null) { return "LuaCs AsyncReaderWriterLock has a different shape in this build"; }
        harmony.Patch(acquire, prefix: new HarmonyMethod(typeof(LuaBusFeature), nameof(AcquireReader)));
        return null;
    }

    static bool AcquireReader(AsyncReaderWriterLock __instance, CancellationToken token, ref Task<IDisposable> __result,
        SemaphoreSlim ____writeSemaphore, SemaphoreSlim ____readSemaphore, ref int ____readerCount)
    {
        if (!instance.Active) { return true; }
        ____writeSemaphore.Wait(token);
        if (Interlocked.Increment(ref ____readerCount) == 1)
        {
            try { ____readSemaphore.Wait(token); }
            catch
            {
                Interlocked.Decrement(ref ____readerCount);
                ____writeSemaphore.Release();
                throw;
            }
        }
        ____writeSemaphore.Release();
        __result = TokenOf(__instance);
        fast++;
        return false;
    }

    static Task<IDisposable> TokenOf(AsyncReaderWriterLock owner)
    {
        Cached c = last;
        if (c != null && ReferenceEquals(c.Owner, owner)) { return c.Token; }
        Task<IDisposable> t = tokens.GetValue(owner, static o => Task.FromResult<IDisposable>(new ReaderToken(AccessTools.MethodDelegate<Action>(AccessTools.Method(typeof(AsyncReaderWriterLock), "ReleaseReaderLock"), o))));
        last = new Cached(owner, t);
        return t;
    }

    public override string Report() => $"syncReaderLocks={fast}";
}
