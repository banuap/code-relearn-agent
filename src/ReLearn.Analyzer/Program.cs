using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;
using ReLearn.Analysis;
using ReLearn.Generators;

namespace ReLearn;

public static class Program
{
    private const string Help = """
        relearn — Roslyn-based legacy .NET knowledge extractor for modernization agents

        USAGE
          relearn analyze <solution|project|folder> [options]      extract the knowledge pack
          relearn speckit --pack <dir> --target <profile> --out <repo> [--only CAP-001,orders]
          relearn bmad    --pack <dir> --target <profile> --out <repo> [--docs docs]
          relearn all     <solution> --target <profile> --out <repo> [analyze options]
          relearn targets                                           list built-in target profiles

        ANALYZE OPTIONS
          --out <dir>            output folder for the knowledge pack (default: ./relearn-out)
          --ast <mode>           none | outline (default) | full
          --include-tests        analyze test projects too (useful to mine rules from test names)
          --include-trivial      keep trivial argument-null guards as rules
          --adhoc                skip MSBuild; parse sources with framework references only
          --max-depth <n>        max call depth per flow (default 12)
          --max-steps <n>        max steps per flow (default 250)
          -v | --verbose         verbose logging

        TARGET PROFILES
          java-spring            Java 21 + Spring Boot 3 services (web/API/backend)
          node-electron          Node.js + TypeScript + Electron desktop (for WinForms/WPF)
          <path.json>            any custom profile with the same schema
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Help); return 0; }
        var cmd = args[0];
        var opts = Parse(args.Skip(1).ToArray(), out var positional);

        try
        {
            switch (cmd)
            {
                case "targets":
                    foreach (var t in TargetProfile.BuiltIn()) Console.WriteLine(t);
                    return 0;
                case "analyze":
                    RegisterMsBuild();
                    await Analyze(positional.FirstOrDefault() ?? ".", opts, opts.GetValueOrDefault("out") ?? "relearn-out");
                    return 0;
                case "speckit":
                case "bmad":
                {
                    var pack = Require(opts, "pack");
                    var target = Require(opts, "target");
                    var outDir = opts.GetValueOrDefault("out") ?? ".";
                    Generate(cmd, pack, target, outDir, opts);
                    return 0;
                }
                case "all":
                {
                    RegisterMsBuild();
                    var target = Require(opts, "target");
                    var outDir = opts.GetValueOrDefault("out") ?? ".";
                    var packDir = opts.GetValueOrDefault("pack") ?? Path.Combine(outDir, ".relearn");
                    await Analyze(positional.FirstOrDefault() ?? ".", opts, packDir);
                    Generate("speckit", packDir, target, outDir, opts);
                    Generate("bmad", packDir, target, outDir, opts);
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"Unknown command '{cmd}'.\n");
                    Console.WriteLine(Help);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (opts.ContainsKey("verbose")) Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RegisterMsBuild()
    {
        try
        {
            if (!MSBuildLocator.IsRegistered)
            {
                var instance = MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(i => i.Version).FirstOrDefault();
                if (instance != null) MSBuildLocator.RegisterInstance(instance);
                else MSBuildLocator.RegisterDefaults();
                Console.Error.WriteLine($"MSBuild: {instance?.Name} {instance?.Version} ({instance?.MSBuildPath})");
            }
        }
        catch (Exception ex)
        {
            // Roslyn 4.11's out-of-process BuildHost locates MSBuild itself; registration is best effort.
            Console.Error.WriteLine($"MSBuildLocator: {ex.Message} (continuing)");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] // keep MSBuild-dependent types out of Main's JIT
    private static async Task Analyze(string input, Dictionary<string, string?> o, string outDir)
    {
        var options = new AnalyzerOptions
        {
            Input = input,
            Output = outDir,
            AstMode = o.GetValueOrDefault("ast") ?? "outline",
            IncludeTests = o.ContainsKey("include-tests"),
            IncludeTrivialRules = o.ContainsKey("include-trivial"),
            ForceAdhoc = o.ContainsKey("adhoc"),
            MaxFlowDepth = int.TryParse(o.GetValueOrDefault("max-depth"), out var d) ? d : 12,
            MaxFlowSteps = int.TryParse(o.GetValueOrDefault("max-steps"), out var s) ? s : 250,
            Verbose = o.ContainsKey("verbose") || o.ContainsKey("v"),
        };
        var pack = await new Analyzer(options).RunAsync();
        Console.WriteLine();
        foreach (var (k, v) in pack.Manifest.Counts) Console.WriteLine($"  {k,-18} {v,8}");
        Console.WriteLine($"\nKnowledge pack: {Path.GetFullPath(outDir)}  (start with summary.md)");
    }

    private static void Generate(string kind, string packDir, string target, string outDir, Dictionary<string, string?> o)
    {
        var pack = PackLoader.Load(packDir);
        var profile = TargetProfile.Load(target);
        var view = new PackView(pack, profile);
        if (kind == "speckit")
        {
            var only = o.GetValueOrDefault("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var files = new SpecKitGenerator(view, outDir).Run(only);
            Console.WriteLine($"Spec Kit: wrote {files.Count} files under {Path.GetFullPath(outDir)} (see .specify/relearn/feature-index.md)");
        }
        else
        {
            var docs = Path.Combine(outDir, o.GetValueOrDefault("docs") ?? "docs");
            var files = new BmadGenerator(view, docs).Run();
            Console.WriteLine($"BMAD: wrote {files.Count} files under {Path.GetFullPath(docs)} (start with index.md)");
        }
    }

    private static string Require(Dictionary<string, string?> o, string key) =>
        o.GetValueOrDefault(key) ?? throw new ArgumentException($"--{key} is required");

    private static Dictionary<string, string?> Parse(string[] args, out List<string> positional)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--") || (a.StartsWith("-") && a.Length == 2))
            {
                var key = a.TrimStart('-');
                if (key == "v") key = "verbose";
                var flag = key is "include-tests" or "include-trivial" or "adhoc" or "verbose";
                if (!flag && i + 1 < args.Length && !args[i + 1].StartsWith("--")) d[key] = args[++i];
                else d[key] = null;
            }
            else positional.Add(a);
        }
        return d;
    }
}
