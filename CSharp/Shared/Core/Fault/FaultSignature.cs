using System;
using System.Diagnostics;
using System.Text;

namespace PrimaryPackage.Core.Fault;

public static class FaultSignature {
    private const int MessageLimit = 96;

    public static string Of(Exception error) {
        Exception root = FaultClassifier.Unwrap(error);
        StringBuilder text = new StringBuilder(160);
        text.Append(root.GetType().Name).Append(" @ ").Append(TopFrame(root)).Append(" : ");
        AppendMessage(text, root.Message);
        return text.ToString();
    }

    public static string TopFrame(Exception error) {
        try {
            StackFrame[] frames = new StackTrace(error, false).GetFrames();
            for (int i = 0; i < frames.Length; i++) {
                var method = frames[i].GetMethod();
                if (method == null) { continue; }
                string owner = method.DeclaringType?.Name ?? "?";
                return owner + "." + method.Name;
            }
        } catch (Exception) {
            return "?";
        }
        return "?";
    }

    public static string Trace(Exception error, int frames) {
        Exception root = FaultClassifier.Unwrap(error);
        string[] lines = (root.StackTrace ?? "").Split('\n');
        StringBuilder text = new StringBuilder();
        int taken = 0;
        for (int i = 0; i < lines.Length && taken < frames; i++) {
            string line = lines[i].Trim();
            if (line.Length == 0) { continue; }
            if (text.Length > 0) { text.Append(" <- "); }
            text.Append(Shorten(line));
            taken++;
        }
        return text.ToString();
    }

    private static string Shorten(string frame) {
        int location = frame.IndexOf(" in ", StringComparison.Ordinal);
        if (location > 0) { frame = frame.Substring(0, location); }
        return frame.StartsWith("at ", StringComparison.Ordinal) ? frame.Substring(3) : frame;
    }

    private static void AppendMessage(StringBuilder text, string message) {
        if (message == null) { return; }
        int written = 0;
        bool digits = false;
        for (int i = 0; i < message.Length && written < MessageLimit; i++) {
            char c = message[i];
            if (c == '\r' || c == '\n') { c = ' '; }
            if (char.IsDigit(c)) {
                if (!digits) { text.Append('#'); written++; }
                digits = true;
                continue;
            }
            digits = false;
            text.Append(c);
            written++;
        }
    }
}
