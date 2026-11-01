using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
namespace PrimaryPackage.Economy {
    public sealed class OrderSetsPlugin : PrimaryPackage.Core.IModule, PrimaryPackage.Core.ILoadCompleted {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            ReflectionUtils.AddNonAbstractAssemblyTypes(GetType().Assembly, overwrite: true);
        }
        public void LoadCompleted() => OrderSets.Load();
        public void Release() {}
    }
}
