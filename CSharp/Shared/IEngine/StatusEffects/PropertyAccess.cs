using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using IEngine.Execution;

namespace IEngine.StatusEffects;

// Simulation-thread bindings. SerializableProperty remains the source of truth;
// native fast paths run before typed delegates, and setters run exactly once.
static class PropertyAccess
{
    static readonly Dictionary<SerializableProperty, Binding> bindings = new(ReferenceEqualityComparer.Instance);
    static readonly Dictionary<Type, bool> mapSafety = new();
    static readonly List<MethodInfo> nativeReaders = NativeReaders();
    static Binding last;
    static Type lastMapType;
    static bool lastMapSafe;
    public static long Compiled, Faults;

    public static Binding Of(SerializableProperty property)
    {
        Binding cached = last;
        if (cached != null && ReferenceEquals(cached.Source, property)) { return cached; }
        if (!bindings.TryGetValue(property, out Binding binding))
        {
            binding = new Binding(property);
            bindings.Add(property, binding);
        }
        last = binding;
        return binding;
    }

    public static void Clear()
    {
        last = null;
        bindings.Clear();
        lastMapType = null;
        mapSafety.Clear();
    }

    public static bool CanCacheMap(Type type)
    {
        if (lastMapType == type) { return lastMapSafe; }
        if (!mapSafety.TryGetValue(type, out bool safe))
        {
            if (!type.IsValueType)
            {
                InterfaceMapping map = type.GetInterfaceMap(typeof(ISerializableEntity));
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    if (map.InterfaceMethods[i].Name == "get_SerializableProperties")
                    {
                        safe = TryFieldGetter(map.TargetMethods[i], out _);
                        break;
                    }
                }
            }
            mapSafety.Add(type, safe);
        }
        lastMapType = type;
        lastMapSafe = safe;
        return safe;
    }

    static List<MethodInfo> NativeReaders()
    {
        var result = new List<MethodInfo>();
        foreach (string name in new[] { "TryGetFloatValueWithoutReflection", "TryGetBoolValueWithoutReflection" })
        {
            foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(SerializableProperty), name)))
            {
                if (instruction.operand is MethodInfo method && method.IsSpecialName && method.Name.StartsWith("get_", StringComparison.Ordinal)) { result.Add(method); }
            }
        }
        return result;
    }

    public static bool TryFieldGetter(MethodInfo getter, out FieldInfo field)
    {
        field = null;
        if (getter == null || getter.IsStatic || getter.IsVirtual && !getter.IsFinal || PatchCensus.Of(getter).Any) { return false; }
        byte[] body = getter.GetMethodBody()?.GetILAsByteArray();
        if (body == null || body.Length != 7 || body[0] != 0x02 || body[1] != 0x7B || body[6] != 0x2A) { return false; }
        field = getter.Module.ResolveField(BitConverter.ToInt32(body, 2), getter.DeclaringType.GetGenericArguments(), null);
        return true;
    }

    public sealed class Binding
    {
        public readonly SerializableProperty Source;
        readonly PropertyInfo info;
        readonly Func<object, float> floatReader;
        readonly Func<object, bool> boolReader;
        readonly Action<object, float> floatWriter;
        readonly Action<object, int> intWriter;
        readonly Action<object, bool> boolWriter;
        public readonly string Fault;

        public Binding(SerializableProperty source)
        {
            Source = source;
            info = source.PropertyInfo;
            if (info == null || info.DeclaringType == null || info.DeclaringType.IsValueType || info.GetIndexParameters().Length != 0 || info.PropertyType != source.PropertyType) { return; }
            try
            {
                MethodInfo getter = info.GetGetMethod(true), setter = info.GetSetMethod(true);
                Type type = source.PropertyType;
                if (getter != null && !getter.IsStatic && !getter.ContainsGenericParameters)
                {
                    if (type == typeof(float) || type == typeof(int)) { floatReader = MakeReader<float>(getter, type == typeof(int)); }
                    else if (type == typeof(bool)) { boolReader = MakeReader<bool>(getter, false); }
                }
                if (setter != null && !setter.IsStatic && !setter.ContainsGenericParameters)
                {
                    if (type == typeof(float)) { floatWriter = MakeWriter<float>(setter); }
                    else if (type == typeof(int)) { intWriter = MakeWriter<int>(setter); }
                    else if (type == typeof(bool)) { boolWriter = MakeWriter<bool>(setter); }
                }
                Compiled++;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or MemberAccessException or InvalidProgramException)
            {
                Fault = $"{info.DeclaringType.FullName}.{info.Name}: {exception.Message}";
                Faults++;
            }
        }

        public bool TryGetFloat(object target, SerializableProperty property, out float value)
        {
            value = 0f;
            if (target == null || info == null) { return false; }
            if (Source.TryGetFloatValueWithoutReflection(target, out value)) { return true; }
            if (floatReader != null && info.DeclaringType.IsInstanceOfType(target))
            {
                try { value = floatReader(target); return true; }
                catch { return false; }
            }
            object objVal = Source.GetValue(target);
            if (objVal is float f) { value = f; return true; }
            if (objVal is int i) { value = i; return true; }
            return false;
        }

        public bool TryGetBool(object target, SerializableProperty property, out bool value)
        {
            value = false;
            if (target == null || info == null) { return false; }
            if (Source.TryGetBoolValueWithoutReflection(target, out value)) { return true; }
            if (boolReader != null && info.DeclaringType.IsInstanceOfType(target))
            {
                try { value = boolReader(target); return true; }
                catch { return false; }
            }
            object objVal = Source.GetValue(target);
            if (objVal is bool b) { value = b; return true; }
            return false;
        }

        public float ReadFloat(object target)
        {
            if (target == null || info == null) { return 0f; }
            if (Source.TryGetFloatValueWithoutReflection(target, out float value)) { return value; }
            if (floatReader == null || !info.DeclaringType.IsInstanceOfType(target)) { return Source.GetFloatValue(target); }
            try { return floatReader(target); }
            catch (Exception exception)
            {
                DebugConsole.ThrowError("Exception thrown by the target of SerializableProperty.GetValue", exception);
                return 0f;
            }
        }

        public bool ReadBool(object target)
        {
            if (target == null || info == null) { return false; }
            if (Source.TryGetBoolValueWithoutReflection(target, out bool value)) { return value; }
            if (boolReader == null || !info.DeclaringType.IsInstanceOfType(target)) { return Source.GetBoolValue(target); }
            try { return boolReader(target); }
            catch (Exception exception)
            {
                DebugConsole.ThrowError("Exception thrown by the target of SerializableProperty.GetValue", exception);
                return false;
            }
        }

        public bool WriteFloat(object target, float value)
        {
            try
            {
                if (Source.TrySetFloatValueWithoutReflection(target, value)) { return true; }
                if (floatWriter == null || !info.DeclaringType.IsInstanceOfType(target)) { info.SetValue(target, value, null); }
                else { Invoke(floatWriter, target, value); }
                return true;
            }
            catch (TargetInvocationException exception) { WriteTargetError(exception.InnerException); return false; }
            catch (Exception exception) { WriteBindingError(exception); return false; }
        }

        public bool WriteInt(object target, int value)
        {
            try
            {
                if (Source.TrySetFloatValueWithoutReflection(target, value)) { return true; }
                if (intWriter == null || !info.DeclaringType.IsInstanceOfType(target)) { info.SetValue(target, value, null); }
                else { Invoke(intWriter, target, value); }
                return true;
            }
            catch (TargetInvocationException exception) { WriteTargetError(exception.InnerException); return false; }
            catch (Exception exception) { WriteBindingError(exception); return false; }
        }

        public bool WriteBool(object target, bool value)
        {
            try
            {
                if (Source.TrySetBoolValueWithoutReflection(target, value)) { return true; }
                if (boolWriter == null || !info.DeclaringType.IsInstanceOfType(target)) { info.SetValue(target, value, null); }
                else { Invoke(boolWriter, target, value); }
                return true;
            }
            catch (TargetInvocationException exception) { WriteTargetError(exception.InnerException); return false; }
            catch (Exception exception) { WriteBindingError(exception); return false; }
        }

        void WriteBindingError(Exception exception) => DebugConsole.ThrowError($"Error in SerializableProperty.TrySetValue (Property: {Source.PropertyInfo.Name})", exception);
        static void WriteTargetError(Exception exception) => DebugConsole.ThrowError("Exception thrown by the target of SerializableProperty.TrySetValue", exception);

        static void Invoke<T>(Action<object, T> setter, object target, T value)
        {
            try { setter(target, value); }
            catch (Exception exception) { throw new TargetInvocationException(exception); }
        }

        public bool TryField(Type targetType, out FieldInfo field)
        {
            field = null;
            MethodInfo getter = info?.GetGetMethod(true);
            if (getter == null || getter.IsStatic || getter.IsVirtual && !getter.IsFinal || !getter.DeclaringType.IsAssignableFrom(targetType) ||
                info.Name != Source.Name || info.PropertyType != Source.PropertyType || info.PropertyType != typeof(float) && info.PropertyType != typeof(int) && info.PropertyType != typeof(bool) ||
                PatchCensus.Of(getter).Any) { return false; }
            foreach (MethodInfo native in nativeReaders)
            {
                if (native.Name.AsSpan(4).SequenceEqual(Source.Name.AsSpan()) && native.DeclaringType.IsAssignableFrom(targetType) && native != getter) { return false; }
            }
            return TryFieldGetter(getter, out field) && field.FieldType == info.PropertyType;
        }
    }

    static Func<object, T> MakeReader<T>(MethodInfo getter, bool convertInt)
    {
        var method = new DynamicMethod("ie_read_" + getter.Name, typeof(T), new[] { typeof(object) }, typeof(PropertyAccess).Module, true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, getter.DeclaringType);
        il.Emit(OpCodes.Callvirt, getter);
        if (convertInt) { il.Emit(OpCodes.Conv_R4); }
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<object, T>>();
    }

    static Action<object, T> MakeWriter<T>(MethodInfo setter)
    {
        var method = new DynamicMethod("ie_write_" + setter.Name, null, new[] { typeof(object), typeof(T) }, typeof(PropertyAccess).Module, true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, setter.DeclaringType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, setter);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Action<object, T>>();
    }
}
