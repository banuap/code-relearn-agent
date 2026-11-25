using System.Text;
using System.Text.RegularExpressions;
using ReLearn.Infrastructure;
using ReLearn.Model;

namespace ReLearn.Output;

public static class Mermaid
{
    private static string Id(string s) => Regex.Replace(s, @"[^A-Za-z0-9_]", "_");
    private static string Label(string s) => s.Replace("\"", "'").Replace("<", "‹").Replace(">", "›");

    public static string Sequence(BusinessFlow f, int maxSteps = 30)
    {
        var sb = new StringBuilder("sequenceDiagram\n    autonumber\n");
        var actor = f.Roles.FirstOrDefault() ?? "User";
        sb.AppendLine($"    actor U as {Label(actor)}");
        var participants = new List<string>();
        foreach (var s in f.Steps.Take(maxSteps))
            if (!participants.Contains(s.Type)) participants.Add(s.Type);
        foreach (var p in participants) sb.AppendLine($"    participant {Id(p)} as {Label(p)}");
        var stores = f.EntityOperations.Count > 0;
        if (stores) sb.AppendLine("    participant DB as Database");
        var ints = f.Integrations.Count > 0;
        if (ints) sb.AppendLine("    participant EXT as External systems");

        var byMethod = f.Steps.ToDictionary(s => s.MethodId, s => s);
        var first = f.Steps.FirstOrDefault();
        if (first != null) sb.AppendLine($"    U->>{Id(first.Type)}: {Label(f.Trigger)}");
        foreach (var s in f.Steps.Skip(1).Take(maxSteps))
        {
            var from = s.CalledFrom != null && byMethod.TryGetValue(s.CalledFrom, out var caller) ? caller.Type : first?.Type ?? "U";
            sb.AppendLine($"    {Id(from)}->>{Id(s.Type)}: {Label(s.Method)}()");
            foreach (var r in s.Rules.Take(3)) sb.AppendLine($"    Note over {Id(s.Type)}: rule {r}");
            foreach (var d in s.Data.Take(4)) sb.AppendLine($"    {Id(s.Type)}->>DB: {Label(Text.OneLine(d, 60))}");
            foreach (var i in s.Integrations.Take(3)) sb.AppendLine($"    {Id(s.Type)}-)EXT: {Label(Text.OneLine(i, 60))}");
        }
        if (first != null)
        {
            foreach (var d in first.Data.Take(4)) sb.AppendLine($"    {Id(first.Type)}->>DB: {Label(Text.OneLine(d, 60))}");
            sb.AppendLine($"    {Id(first.Type)}-->>U: result");
        }
        if (f.Steps.Count > maxSteps) sb.AppendLine($"    Note over U: … {f.Steps.Count - maxSteps} more steps (see flows.json)");
        return sb.ToString();
    }

    public static string Journey(UserJourney j, JourneyModel jm)
    {
        var sb = new StringBuilder("flowchart LR\n");
        string Name(string id) => jm.Screens.FirstOrDefault(s => s.Id == id)?.Name ?? id;
        for (var i = 0; i < j.Steps.Count; i++)
        {
            var s = j.Steps[i];
            var nodeId = $"S{i}";
            var actions = s.Flows.Count > 0 ? $"<br/><small>{s.Flows.Count} action(s)</small>" : "";
            sb.AppendLine($"    {nodeId}[\"{Label(Name(s.Screen))}{actions}\"]");
            if (i > 0) sb.AppendLine($"    S{i - 1} -->|{Label(j.Steps[i - 1].Action ?? "next")}| {nodeId}");
        }
        return sb.ToString();
    }

    public static string Er(IEnumerable<EntityModel> entities)
    {
        var list = entities.ToList();
        var sb = new StringBuilder("erDiagram\n");
        foreach (var e in list)
        {
            sb.AppendLine($"    {Id(e.Name)} {{");
            foreach (var p in e.Properties.Where(p => p.NavigationTo == null).Take(25))
                sb.AppendLine($"        {Id(Regex.Replace(p.Type, @"[<>?\[\], ]", "_"))} {Id(p.Name)}{(p.IsKey ? " PK" : "")}");
            sb.AppendLine("    }");
        }
        foreach (var e in list)
            foreach (var p in e.Properties.Where(p => p.NavigationTo != null && list.Any(x => x.Name == p.NavigationTo)))
            {
                var rel = p.IsCollection ? "||--o{" : "}o--||";
                sb.AppendLine("    " + Id(e.Name) + " " + rel + " " + Id(p.NavigationTo!) + " : " + Id(p.Name));
            }
        return sb.ToString();
    }

    public static string Projects(DependencyMap d)
    {
        var sb = new StringBuilder("flowchart LR\n");
        foreach (var p in d.Projects) sb.AppendLine($"    {Id(p.Name)}[\"{Label(p.Name)}<br/><small>{p.Kind} · {string.Join(",", p.TargetFrameworks)}</small>\"]");
        foreach (var e in d.ProjectEdges) sb.AppendLine($"    {Id(e.From)} --> {Id(e.To)}");
        return sb.ToString();
    }

    public static string Layers(DependencyMap d)
    {
        var sb = new StringBuilder("flowchart TD\n");
        foreach (var e in d.Layers.Where(e => e.From != "test" && e.To != "test").Take(60))
            sb.AppendLine($"    {Id(e.From)}[{e.From}] -->|{e.Weight}| {Id(e.To)}[{e.To}]");
        return sb.ToString();
    }
}
