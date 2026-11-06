using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Barotrauma;
using Barotrauma.LuaCs;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PrimaryPackage.Core;

public static class ModCompiler {
    public readonly record struct CompileResult(bool Success, int ErrorCount, int WarningCount, string Log);
#if CLIENT
    private const string Target = "Client";
#else
    private const string Target = "Server";
#endif

    public static void Initialize() => ModCommands.Register("pp_verify",
        "pp_verify: validate current LuaCs compilation, startup failures and isolated contracts", _ => {
            CompileResult result = Verify();
            DebugConsole.NewMessage(result.Log, result.Success ? Microsoft.Xna.Framework.Color.LightGreen : Microsoft.Xna.Framework.Color.Red);
        }, localOnClient: true, relayToServer: false);

    // LuaCs has already compiled this target before activating the plugin.
    // Keep automatic startup checks small; explicit pp_verify recompiles it.
    public static CompileResult ReportStartup() {
        var log = new StringBuilder($"=== PrimaryPackage {Target}: LuaCs activation ===\n");
        int errors = AppendStartupFailures(log);
        log.Append("Source compiled by LuaCs before activation. pp_verify rechecks this process; RunSimulator checks both targets.\n");
        return SaveReport(log, errors, 0);
    }

    public static CompileResult Verify() {
        var log = new StringBuilder($"=== PrimaryPackage {Target}: native LuaCs verification ===\n");
        int errors = 0, warnings = 0;
        try {
            CompileResult compilation = CompileCurrentTarget();
            errors += compilation.ErrorCount;
            warnings += compilation.WarningCount;
            log.Append(compilation.Log);
        } catch (Exception error) {
            errors++;
            log.Append("[ERR] Verification context failed: ").Append(error).Append('\n');
        }
        errors += AppendStartupFailures(log);
        bool passed = PrimaryPackageTests.RunAllTests(out string tests);
        log.Append("=== Isolated contracts ===\n").Append(tests);
        if (!passed) { errors++; }
        return SaveReport(log, errors, warnings);
    }

    private static int AppendStartupFailures(StringBuilder log) {
        IReadOnlyList<string> failures = ModuleHost.StartupFailures;
        log.Append("Module/patch startup failures: ").Append(failures.Count).Append('\n');
        foreach (string failure in failures) { log.Append("[ERR] ").Append(failure).Append('\n'); }
        int count = failures.Count;
        foreach (IEngine.Feature feature in IEngine.Runtime.Features) {
            if (!feature.Enabled || feature.Installed) { continue; }
            count++;
            log.Append("[ERR] IEngine ").Append(feature.Name).Append(": ").Append(feature.Fault).Append('\n');
        }
        return count;
    }

    private static CompileResult SaveReport(StringBuilder log, int errors, int warnings) {
        log.Append("VERIFICATION: ").Append(errors == 0 ? "PASSED" : "FAILED")
            .Append("; errors=").Append(errors).Append(" warnings=").Append(warnings).Append('\n');
        try {
            if (string.IsNullOrEmpty(ModAssets.Root)) { throw new InvalidOperationException("Mod package is not mounted"); }
            File.WriteAllText(Path.Combine(ModAssets.Root, "mod_build_test_report." + Target.ToLowerInvariant() + ".log"), log.ToString());
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) {
            errors++;
            log.Append("[ERR] Cannot save verification report: ").Append(error).Append('\n');
        }
        string report = log.ToString();
        if (errors != 0) { ModLog.Error(report); }
        return new CompileResult(errors == 0, errors, warnings, report);
    }

    private static T StaticContract<T>(Type service, string name) {
        FieldInfo field = service.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        return field?.GetValue(null) is T value ? value : throw new MissingFieldException(service.FullName, name);
    }

    private static CompileResult CompileCurrentTarget() {
        string root = ModAssets.Root;
        ContentPackage package = ModAssets.Package;
        if (package == null || string.IsNullOrEmpty(root)) { throw new InvalidOperationException("Mod package is not mounted"); }
        object service = LuaCsSetup.Instance.PluginManagementService;
        Type type = service.GetType();
        // Reuse the actual LuaCs parse options, imports, compatibility pass and
        // metadata views, including in-memory publicized assembly references.
        var parse = StaticContract<CSharpParseOptions>(type, "ScriptParseOptions");
        var options = StaticContract<CSharpCompilationOptions>(type, "CompilationOptions").WithConcurrentBuild(false);
        var imports = StaticContract<SyntaxTree>(type, "BaseAssemblyImports");
        PropertyInfo baseReferences = type.GetProperty("BaseMetadataReferences", BindingFlags.Instance | BindingFlags.NonPublic);
        if (baseReferences?.GetValue(service) is not IEnumerable<MetadataReference> nativeReferences) {
            throw new MissingMemberException(type.FullName, "BaseMetadataReferences");
        }
        FieldInfo loadersField = type.GetField("_assemblyLoaders", BindingFlags.Instance | BindingFlags.NonPublic);
        if (loadersField?.GetValue(service) is not IEnumerable<KeyValuePair<ContentPackage, IAssemblyLoaderService>> loaders) {
            throw new MissingFieldException(type.FullName, "_assemblyLoaders");
        }
        var references = new List<MetadataReference>(nativeReferences);
        foreach (var loader in loaders) {
            if (ReferenceEquals(loader.Key, package)) { continue; }
            foreach (MetadataReference reference in loader.Value.AssemblyReferences) {
                if (reference != null) { references.Add(reference); }
            }
        }
        MethodInfo compatibility = type.GetMethod("DoSourceCodeTextCompatibilityPass", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, "DoSourceCodeTextCompatibilityPass");
        var convert = compatibility.CreateDelegate<Func<string, string>>(service);
        var trees = new List<SyntaxTree> { imports };
        foreach (string directory in new[] { "Shared", Target }) {
            string folder = Path.Combine(root, "CSharp", directory);
            if (!Directory.Exists(folder)) { throw new DirectoryNotFoundException(folder); }
            foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)) {
                trees.Add(CSharpSyntaxTree.ParseText(convert(File.ReadAllText(file)), parse, file, Encoding.Default));
            }
        }
        if (trees.Count == 1) { throw new InvalidOperationException("No source files found; verification cannot pass"); }
        string name = typeof(ModCompiler).Assembly.GetName().Name;
        var compilation = CSharpCompilation.Create(name, trees, references, options);
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        var log = new StringBuilder($"{Target}: {trees.Count - 1} files; actual LuaCs compiler context\n");
        int errors = 0, warnings = 0;
        foreach (Diagnostic diagnostic in result.Diagnostics) {
            if (diagnostic.Severity == DiagnosticSeverity.Error) { errors++; }
            else if (diagnostic.Severity == DiagnosticSeverity.Warning) { warnings++; }
            else { continue; }
            log.Append(diagnostic.Severity == DiagnosticSeverity.Error ? "[ERR] " : "[WARN] ").Append(diagnostic).Append('\n');
        }
        log.Append("Compilation: ").Append(result.Success ? "PASSED" : "FAILED").Append('\n');
        return new CompileResult(result.Success, errors, warnings, log.ToString());
    }
}
