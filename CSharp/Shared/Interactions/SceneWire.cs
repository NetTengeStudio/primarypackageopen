using System;
using Barotrauma.Networking;

namespace PrimaryPackage.Interactions;

internal static class SceneWire {
    internal static void Write(IWriteMessage output, SceneProgram program, in SceneBindings bindings) {
        output.WriteString(program.Name); output.WriteByte((byte)program.Trigger); output.WriteBoolean(program.Repeat); output.WriteSingle(program.Cooldown);
        output.WriteString(bindings.Target); output.WriteUInt16(bindings.Door); output.WriteUInt16(bindings.Light); output.WriteByte(bindings.LightComponent);
        output.WriteUInt16(bindings.Submarine); output.WriteSingle(bindings.X); output.WriteSingle(bindings.Y); output.WriteSingle(bindings.Radius);
        output.WriteByte((byte)program.Steps.Count);
        foreach (SceneStep step in program.Steps) {
            output.WriteByte((byte)step.Action); output.WriteByte((byte)step.Audience); output.WriteSingle(step.Delay); output.WriteSingle(step.Duration); output.WriteSingle(step.Interval);
            output.WriteString(step.Resource); output.WriteString(step.Visual); output.WriteString(step.Text); output.WriteSingle(step.Gain);
            output.WriteSingle(step.Intensity); output.WriteSingle(step.Scale); output.WriteSingle(step.Speed); output.WriteSingle(step.NearDistance); output.WriteByte((byte)step.Behavior);
        }
    }
    internal static SceneProgram Read(IReadMessage input, out SceneBindings bindings) {
        int available = input.LengthBits;
        var program = new SceneProgram { Name = input.ReadString(), Trigger = (SceneTrigger)input.ReadByte(), Repeat = input.ReadBoolean(), Cooldown = input.ReadSingle() };
        bindings = new SceneBindings(input.ReadString(), input.ReadUInt16(), input.ReadUInt16(), input.ReadByte(), input.ReadUInt16(), input.ReadSingle(), input.ReadSingle(), input.ReadSingle());
        int count = input.ReadByte();
        if (count is < 1 or > SceneProgram.MaxSteps) { throw new ArgumentException("Недопустимое число действий сцены"); }
        for (int i = 0; i < count; i++) {
            program.Steps.Add(new SceneStep { Action = (SceneAction)input.ReadByte(), Audience = (SceneAudience)input.ReadByte(), Delay = input.ReadSingle(), Duration = input.ReadSingle(), Interval = input.ReadSingle(),
                Resource = input.ReadString(), Visual = input.ReadString(), Text = input.ReadString(), Gain = input.ReadSingle(), Intensity = input.ReadSingle(),
                Scale = input.ReadSingle(), Speed = input.ReadSingle(), NearDistance = input.ReadSingle(), Behavior = (PhantomBehavior)input.ReadByte() });
        }
        if (input.BitPosition > available) { throw new ArgumentException("Пакет сцены обрезан"); }
        program.Validate(); return program;
    }
}
