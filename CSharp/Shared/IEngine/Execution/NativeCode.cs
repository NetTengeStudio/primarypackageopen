using System;
using Barotrauma;

namespace IEngine.Execution;

static class NativeCode
{
#if CLIENT
    static readonly Guid module = new("53d1dc7e-9565-4faa-bdce-cea10ef771ef");
#else
    static readonly Guid module = new("94340cc3-bb99-4d8b-bd39-f8404b4701b1");
#endif
    public static bool Verified => typeof(Item).Module.ModuleVersionId == module;
}
