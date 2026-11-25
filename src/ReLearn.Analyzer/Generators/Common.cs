using System.Text;
using ReLearn.Infrastructure;
using ReLearn.Model;

namespace ReLearn.Generators;

public sealed class TargetMapping
{
    public string Match { get; set; } = "";     // fact key, e.g. "stereotype:controller", "integration:kafka", "data:ef-core", "entry:winforms-event", "always"
    public string Legacy { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Rationale { get; set; }
    public string? Alternatives { get; set; }
    public string? Notes { get; set; }
}

public sealed class TargetProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Summary { get; set; } = "";
    public Dictionary<string, string> Stack { get; set; } = new();
    public List<string> ProjectLayout { get; set; } = new();
    public List<string> Conventions { get; set; } = new();
    public List<string> Testing { get; set; } = new();
    public List<string> NonFunctional { get; set; } = new();
    public List<TargetMapping> Mappings { get; set; } = new();

    public static TargetProfile Load(string idOrPath)
    {
        string path;
        if (File.Exists(idOrPath)) path = idOrPath;
        else
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "targets", idOrPath + ".json"),
                Path.Combine(Directory.GetCurrentDirectory(), "targets", idOrPath + ".json"),
            };
            path = candidates.FirstOrDefault(File.Exists)
                   ?? throw new FileNotFoundException($"Target profile '{idOrPath}' not found. Built-in: {string.Join(", ", BuiltIn())}");
        }
        return Json.Read<TargetProfile>(path);
    }

    public static IEnumerable<string> BuiltIn()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "targets");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)) : Array.Empty<string>();
    }
}

/// Reads a knowledge pack written by `relearn analyze`.
public static class PackLoader
{
    public static KnowledgePack Load(string dir)
    {
        string P(string f) => Path.Combine(dir, f);
        if (!File.Exists(P("manifest.json"))) throw new FileNotFoundException($"No knowledge pack in {dir} (manifest.json missing). Run `relearn analyze` first.");
        var pack = new KnowledgePack
        {
            Manifest = Json.Read<Manifest>(P("manifest.json")),
            Dependencies = Json.Read<DependencyMap>(P("dependencies.json")),
            CallGraph = Json.Read<CallGraph>(P("callgraph.json")),
            EntryPoints = Json.Read<List<EntryPoint>>(P("entrypoints.json")),
            Data = Json.Read<DataModel>(P("data.json")),
            Integrations = Json.Read<IntegrationModel>(P("integrations.json")),
            Rules = Json.Read<List<BusinessRule>>(P("rules.json")),
            Flows = Json.Read<List<BusinessFlow>>(P("flows.json")),
            Journeys = Json.Read<JourneyModel>(P("journeys.json")),
            Capabilities = Json.Read<List<Capability>>(P("capabilities.json")),
        };
        if (File.Exists(P("enrichment.json"))) Apply(pack, Json.Read<Enrichment>(P("enrichment.json")));
        return pack;
    }

    /// Overlay the agent's business-language enrichment onto the extracted facts.
    public static void Apply(KnowledgePack pack, Enrichment e)
    {
        foreach (var c in pack.Capabilities)
            if (e.Capabilities.TryGetValue(c.Id, out var ce))
            {
                c.Name = ce.Name ?? c.Name;
                c.Description = ce.Description ?? c.Description;
                c.Priority = ce.Priority ?? c.Priority;
            }
        var dropped = new HashSet<string>();
        foreach (var r in pack.Rules)
            if (e.Rules.TryGetValue(r.Id, out var re))
            {
                if (re.Drop) { dropped.Add(r.Id); continue; }
                r.BusinessStatement = re.Statement ?? r.BusinessStatement;
                r.Title = re.Title ?? r.Title;
                r.Confidence = re.Confidence ?? r.Confidence;
                r.Questions = re.Questions ?? r.Questions;
            }
        pack.Rules.RemoveAll(r => dropped.Contains(r.Id));
        foreach (var c in pack.Capabilities) c.Rules.RemoveAll(dropped.Contains);
        foreach (var f in pack.Flows)
        {
            f.Rules.RemoveAll(dropped.Contains);
            foreach (var s in f.Steps) s.Rules.RemoveAll(dropped.Contains);
            if (e.Flows.TryGetValue(f.Id, out var fe))
            {
                f.BusinessName = fe.Name ?? f.BusinessName;
                f.Goal = fe.Goal ?? f.Goal;
                if (fe.Actor != null && !f.Roles.Contains(fe.Actor)) f.Roles.Insert(0, fe.Actor);
            }
        }
        foreach (var j in pack.Journeys.Journeys)
            if (e.Journeys.TryGetValue(j.Id, out var je)) { j.Name = je.Name ?? j.Name; j.Actor = je.Actor ?? j.Actor; }
    }
}

/// Indexed, read-only view of a pack plus target-mapping facts.
public sealed class PackView
{
    public KnowledgePack Pack { get; }
    public TargetProfile Target { get; }
    public Dictionary<string, BusinessRule> Rules { get; }
    public Dictionary<string, BusinessFlow> Flows { get; }
    public Dictionary<string, EntryPoint> EntryPoints { get; }
    public Dictionary<string, MethodNode> Methods { get; }
    public Dictionary<string, TypeNode> Types { get; }
    public Dictionary<string, IntegrationPoint> Integrations { get; }

    public PackView(KnowledgePack pack, TargetProfile target)
    {
        Pack = pack;
        Target = target;
        Rules = pack.Rules.ToDictionary(r => r.Id);
        Flows = pack.Flows.ToDictionary(f => f.Id);
        EntryPoints = pack.EntryPoints.ToDictionary(e => e.Id);
        Methods = pack.CallGraph.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        Types = pack.Dependencies.Types.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        Integrations = pack.Integrations.Points.ToDictionary(i => i.Id);
    }

    /// Facts present in (a slice of) the legacy system, used to select target mappings.
    public HashSet<string> Facts(Capability? cap = null)
    {
        var f = new HashSet<string> { "always" };
        IEnumerable<BusinessFlow> flows = cap == null ? Pack.Flows : cap.Flows.Where(Flows.ContainsKey).Select(id => Flows[id]);
        var flowList = flows.ToList();
        var methodIds = flowList.SelectMany(x => x.Steps.Select(s => s.MethodId)).ToHashSet();

        var types = cap == null ? Pack.Dependencies.Types : Pack.Dependencies.Types.Where(t => cap.Types.Contains(t.Name));
        foreach (var t in types) f.Add("stereotype:" + t.Stereotype);
        var eps = cap == null ? Pack.EntryPoints : Pack.EntryPoints.Where(e => cap.EntryPoints.Contains(e.Id));
        foreach (var e in eps) f.Add("entry:" + e.Kind);
        var ints = cap == null ? Pack.Integrations.Points : Pack.Integrations.Points.Where(i => methodIds.Contains(i.MethodId));
        foreach (var i in ints) { f.Add("integration:" + i.Technology); f.Add("integration-category:" + i.Category); }
        var data = cap == null ? Pack.Data.Operations : Pack.Data.Operations.Where(d => methodIds.Contains(d.MethodId));
        foreach (var d in data) f.Add("data:" + d.Technology);
        if (data.Any(d => d.DynamicSql)) f.Add("data:dynamic-sql");
        var rules = cap == null ? Pack.Rules : Pack.Rules.Where(r => cap.Rules.Contains(r.Id));
        foreach (var r in rules) f.Add("rule:" + r.Kind);
        if (cap == null)
        {
            foreach (var p in Pack.Dependencies.Projects) f.Add("project:" + p.Kind);
            foreach (var o in Pack.Data.DbObjects) f.Add("dbobject:" + o.Kind);
            foreach (var c in Pack.Data.Contexts) f.Add("data:" + c.Technology);
            foreach (var d in Pack.Dependencies.DiRegistrations) f.Add("di:" + d.Container);
            foreach (var p in Pack.Data.Connections) f.Add("db:" + p.Provider.TrimEnd('?'));
        }
        else
        {
            foreach (var o in Pack.Data.DbObjects.Where(o => o.ReferencedBy.Any(methodIds.Contains))) f.Add("dbobject:" + o.Kind);
        }
        if (Methods.Values.Any(m => m.IsAsync)) f.Add("async");
        return f;
    }

    public List<TargetMapping> MappingsFor(Capability? cap = null)
    {
        var facts = Facts(cap);
        return Target.Mappings.Where(m => facts.Contains(m.Match)).ToList();
    }

    public string Evidence(Location? l) => l == null ? "" : $"`{l.File}:{l.Line}`";

    public string MethodName(string id) => Methods.TryGetValue(id, out var m)
        ? (Types.TryGetValue(m.ContainingType, out var t) ? $"{t.Name}.{m.Name}" : m.Name)
        : id;

    public static string Actor(BusinessFlow f) =>
        f.Roles.Where(r => !r.StartsWith("policy:")).DefaultIfEmpty("user").First();
}

public sealed class Md
{
    private readonly StringBuilder _sb = new();
    public Md H(int level, string text) { _sb.AppendLine().Append(new string('#', level)).Append(' ').AppendLine(text).AppendLine(); return this; }
    public Md P(string text) { _sb.AppendLine(text).AppendLine(); return this; }
    public Md L(string text) { _sb.AppendLine(text); return this; }
    public Md Bullet(string text) { _sb.Append("- ").AppendLine(text); return this; }
    public Md Nl() { _sb.AppendLine(); return this; }
    public Md Table(IEnumerable<string> headers, IEnumerable<IEnumerable<string?>> rows)
    {
        var h = headers.ToList();
        _sb.Append("| ").Append(string.Join(" | ", h)).AppendLine(" |");
        _sb.Append("|").Append(string.Concat(h.Select(_ => "---|"))).AppendLine();
        foreach (var r in rows) _sb.Append("| ").Append(string.Join(" | ", r.Select(c => Text.MdEscape(c)))).AppendLine(" |");
        _sb.AppendLine();
        return this;
    }
    public Md Code(string lang, string code) { _sb.Append("```").AppendLine(lang).AppendLine(code.TrimEnd()).AppendLine("```").AppendLine(); return this; }
    public override string ToString() => _sb.ToString();

    public static void Save(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}
