using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using ReLearn.Infrastructure;

namespace ReLearn.Loading;

public sealed class LoadedSolution
{
    public required Solution Solution { get; init; }
    public required string RootDirectory { get; init; }
    public string Mode { get; init; } = "msbuild";
    public List<string> Warnings { get; } = new();
    public Workspace? Workspace { get; init; }
}

/// Loads a .sln / .slnx / .csproj / .vbproj / folder with MSBuildWorkspace, falling back to an
/// ad-hoc workspace (source files + framework references) when MSBuild cannot evaluate the projects
/// (typical for old .NET Framework projects on a machine without the targeting packs).
public static class SolutionLoader
{
    public static async Task<LoadedSolution> LoadAsync(string input, bool forceAdhoc, Action<string> log)
    {
        var full = Path.GetFullPath(input);
        string root;
        string? target = null;

        if (Directory.Exists(full))
        {
            root = full;
            target = Directory.EnumerateFiles(full, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault()
                     ?? Directory.EnumerateFiles(full, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault()
                     ?? Directory.EnumerateFiles(full, "*.*proj", SearchOption.TopDirectoryOnly).FirstOrDefault(f => f.EndsWith(".csproj") || f.EndsWith(".vbproj"));
        }
        else if (File.Exists(full))
        {
            root = Path.GetDirectoryName(full)!;
            target = full;
        }
        else throw new FileNotFoundException($"Input not found: {input}");

        if (!forceAdhoc && target != null)
        {
            try
            {
                var props = new Dictionary<string, string>
                {
                    ["Configuration"] = "Debug",
                    ["DesignTimeBuild"] = "true",
                    ["BuildingInsideVisualStudio"] = "true",
                    ["SkipCompilerExecution"] = "true",
                    ["ProvideCommandLineArgs"] = "true",
                    ["AlwaysCompileMarkupFilesSynchronously"] = "true",
                };
                var ws = MSBuildWorkspace.Create(props); // kept alive for the whole run
                var warnings = new List<string>();
                ws.WorkspaceFailed += (_, e) => warnings.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}");
                log($"Loading with MSBuildWorkspace: {target}");

                Solution sol;
                if (target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                    sol = await ws.OpenSolutionAsync(target);
                else
                    sol = (await ws.OpenProjectAsync(target)).Solution;

                var docs = sol.Projects.Sum(p => p.DocumentIds.Count);
                if (docs > 0)
                {
                    var loaded = new LoadedSolution { Solution = sol, RootDirectory = root, Mode = "msbuild", Workspace = ws };
                    loaded.Warnings.AddRange(warnings.Distinct().Take(200));
                    return loaded;
                }
                log("MSBuildWorkspace produced no documents; falling back to ad-hoc load.");
                var adhoc = LoadAdhoc(root, log);
                adhoc.Warnings.AddRange(warnings.Distinct().Take(200));
                return adhoc;
            }
            catch (Exception ex)
            {
                log($"MSBuildWorkspace failed ({ex.GetType().Name}: {ex.Message}); falling back to ad-hoc load.");
                var adhoc = LoadAdhoc(root, log);
                adhoc.Warnings.Add($"MSBuild load failed: {ex.Message}");
                return adhoc;
            }
        }

        return LoadAdhoc(root, log);
    }

    /// Ad-hoc: one Roslyn project per *.csproj/*.vbproj folder (or one for the whole tree), referencing
    /// the running runtime's assemblies plus any DLLs found under bin/ or packages/ folders.
    public static LoadedSolution LoadAdhoc(string root, Action<string> log)
    {
        log($"Ad-hoc load of {root}");
        var ws = new AdhocWorkspace();
        var solution = ws.CurrentSolution;

        var frameworkRefs = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        // Third-party DLLs that a legacy build left behind help the semantic model resolve types.
        var extraDlls = SafeEnumerate(root, "*.dll")
            .Where(p => p.Replace('\\', '/').Contains("/packages/") || p.Replace('\\', '/').Contains("/bin/"))
            .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(800)
            .ToList();
        var extraRefs = new List<MetadataReference>();
        foreach (var dll in extraDlls)
        {
            try { extraRefs.Add(MetadataReference.CreateFromFile(dll)); } catch { /* not a managed assembly */ }
        }

        var projectFiles = SafeEnumerate(root, "*.csproj").Concat(SafeEnumerate(root, "*.vbproj")).Where(p => !Paths.IsExcluded(p)).ToList();
        var groups = new List<(string name, string dir, string? projFile, string lang)>();
        if (projectFiles.Count == 0)
            groups.Add((new DirectoryInfo(root).Name, root, null, LanguageNames.CSharp));
        else
            foreach (var pf in projectFiles)
                groups.Add((Path.GetFileNameWithoutExtension(pf), Path.GetDirectoryName(pf)!, pf, pf.EndsWith(".vbproj") ? LanguageNames.VisualBasic : LanguageNames.CSharp));

        var projectDirs = groups.Select(g => g.dir).OrderByDescending(d => d.Length).ToList();
        foreach (var g in groups)
        {
            var ext = g.lang == LanguageNames.VisualBasic ? "*.vb" : "*.cs";
            var files = SafeEnumerate(g.dir, ext)
                .Where(f => !Paths.IsExcluded(f))
                // a file belongs to the deepest project folder containing it
                .Where(f => projectDirs.First(d => f.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(f) == d) == g.dir)
                .ToList();

            var pid = ProjectId.CreateNewId(g.name);
            CompilationOptions copts = g.lang == LanguageNames.VisualBasic
                ? new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                : new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable);
            ParseOptions popts = g.lang == LanguageNames.VisualBasic
                ? new VisualBasicParseOptions(Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.Latest)
                : new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest);

            var info = ProjectInfo.Create(pid, VersionStamp.Create(), g.name, g.name, g.lang,
                filePath: g.projFile, compilationOptions: copts, parseOptions: popts,
                metadataReferences: frameworkRefs.Concat(extraRefs));
            solution = solution.AddProject(info);
            foreach (var f in files)
            {
                var did = DocumentId.CreateNewId(pid);
                solution = solution.AddDocument(did, Path.GetFileName(f), SourceText.From(File.ReadAllText(f)), filePath: f);
            }
        }

        var result = new LoadedSolution { Solution = solution, RootDirectory = root, Mode = "adhoc", Workspace = ws };
        result.Warnings.Add("Ad-hoc load: external types may be unresolved; semantic fidelity is partial.");
        return result;
    }

    public static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            string[] files = Array.Empty<string>(), subs = Array.Empty<string>();
            try { files = Directory.GetFiles(d, pattern); } catch { }
            try { subs = Directory.GetDirectories(d); } catch { }
            foreach (var f in files) yield return f;
            foreach (var s in subs)
            {
                var name = Path.GetFileName(s);
                if (name is "node_modules" or ".git" or ".vs") continue;
                var buildOutput = name is "bin" or "obj" or "TestResults";
                if (buildOutput && pattern != "*.dll") continue;
                stack.Push(s);
            }
        }
    }
}

/// Reads project-file facts Roslyn does not expose (SDK, TFMs, PackageReferences, packages.config).
public static class ProjectFileReader
{
    public static void Enrich(Model.ProjectInfo p, string? projectFile)
    {
        if (projectFile == null || !File.Exists(projectFile)) return;
        try
        {
            var doc = XDocument.Load(projectFile);
            var rootEl = doc.Root!;
            p.Sdk = (string?)rootEl.Attribute("Sdk");
            var all = rootEl.Descendants().ToList();
            string? Prop(string name) => all.FirstOrDefault(e => e.Name.LocalName == name)?.Value;

            var tfm = Prop("TargetFramework") ?? Prop("TargetFrameworks") ?? Prop("TargetFrameworkVersion");
            if (tfm != null) p.TargetFrameworks = tfm.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

            foreach (var pr in all.Where(e => e.Name.LocalName == "PackageReference"))
            {
                var name = (string?)pr.Attribute("Include") ?? (string?)pr.Attribute("Update");
                if (name == null) continue;
                var ver = (string?)pr.Attribute("Version") ?? pr.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
                p.Packages.Add(new Model.PackageRef { Name = name, Version = ver });
            }
            // old-style <Reference Include="Newtonsoft.Json, Version=..."> with HintPath into packages/
            foreach (var r in all.Where(e => e.Name.LocalName == "Reference"))
            {
                var inc = (string?)r.Attribute("Include");
                if (inc == null) continue;
                var name = inc.Split(',')[0].Trim();
                if (!p.AssemblyReferences.Contains(name)) p.AssemblyReferences.Add(name);
            }

            var pkgConfig = Path.Combine(Path.GetDirectoryName(projectFile)!, "packages.config");
            if (File.Exists(pkgConfig))
            {
                foreach (var e in XDocument.Load(pkgConfig).Descendants("package"))
                    p.Packages.Add(new Model.PackageRef { Name = (string?)e.Attribute("id") ?? "", Version = (string?)e.Attribute("version") });
            }

            var sdk = p.Sdk ?? "";
            var pkgs = p.Packages.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var useWinForms = string.Equals(Prop("UseWindowsForms"), "true", StringComparison.OrdinalIgnoreCase) || p.AssemblyReferences.Contains("System.Windows.Forms");
            var useWpf = string.Equals(Prop("UseWPF"), "true", StringComparison.OrdinalIgnoreCase) || p.AssemblyReferences.Contains("PresentationFramework");
            var isTest = pkgs.Any(x => x.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) || x.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase) || x.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase) || x == "Microsoft.NET.Test.Sdk")
                         || string.Equals(Prop("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
                         || p.AssemblyReferences.Contains("Microsoft.VisualStudio.QualityTools.UnitTestFramework");
            var projTypeGuids = Prop("ProjectTypeGuids") ?? "";

            p.IsTest = isTest;
            p.Kind =
                isTest ? "test" :
                useWpf ? "desktop-wpf" :
                useWinForms ? "desktop-winforms" :
                pkgs.Any(x => x.StartsWith("Microsoft.Azure.Functions", StringComparison.OrdinalIgnoreCase) || x.StartsWith("Microsoft.Azure.WebJobs", StringComparison.OrdinalIgnoreCase)) ? "function" :
                sdk.Contains("Microsoft.NET.Sdk.Worker") ? "worker" :
                sdk.Contains("Microsoft.NET.Sdk.BlazorWebAssembly") ? "blazor-wasm" :
                sdk.Contains("Microsoft.NET.Sdk.Web") || sdk.Contains("Razor") ? "web" :
                projTypeGuids.Contains("349c5851-65df-11da-9384-00065b846f21", StringComparison.OrdinalIgnoreCase) ? "web-legacy" : // ASP.NET (MVC/WebForms/WebAPI) on .NET Framework
                p.OutputKind is "ConsoleApplication" ? "console" :
                p.OutputKind is "WindowsApplication" ? "desktop" :
                "library";
        }
        catch (Exception ex)
        {
            p.Kind = p.Kind == "" ? "library" : p.Kind;
            Console.Error.WriteLine($"warn: could not read {projectFile}: {ex.Message}");
        }
    }
}
