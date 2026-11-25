using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ReLearn.Infrastructure;
using ReLearn.Loading;
using ReLearn.Model;

namespace ReLearn.Analysis;

/// Scans non-code artifacts that carry business and integration knowledge:
/// SQL scripts, config files, MVC/Razor views, Web Forms markup, XAML, EDMX, .proto, WSDL.
public sealed class ArtifactScanner
{
    private readonly AnalysisContext _ctx;
    public ArtifactScanner(AnalysisContext ctx) => _ctx = ctx;

    private static readonly Regex CreateObj = new(@"\bCREATE\s+(?:OR\s+ALTER\s+|OR\s+REPLACE\s+)?(?:UNIQUE\s+)?(?:CLUSTERED\s+|NONCLUSTERED\s+)?(TABLE|VIEW|PROCEDURE|PROC|FUNCTION|TRIGGER|INDEX|SEQUENCE|TYPE|SYNONYM|PACKAGE(?:\s+BODY)?)\s+((?:\[?[\w$#]+\]?\.){0,2}\[?[\w$#]+\]?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AspAction = new(@"asp-(?:controller|action|page)\s*=\s*""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex ActionLink = new(@"(?:Html\.ActionLink|Html\.BeginForm|Url\.Action|Html\.Action|Html\.RenderAction|Html\.RouteLink)\s*\(([^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex Href = new(@"(?:href|action|NavigateUrl|PostBackUrl|Source)\s*=\s*""(~?/[^""#{}@]*|[\w/]+\.aspx[^""]*|[\w/]+\.xaml)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AspFor = new(@"(?:asp-for\s*=\s*""([\w.]+)""|Html\.\w+For\s*\(\s*\w+\s*=>\s*\w+\.([\w.]+)|<input[^>]*\bname\s*=\s*""([\w.\[\]]+)"")", RegexOptions.Compiled);
    private static readonly Regex PageDirective = new(@"^\s*@page\s+""([^""]+)""", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex AspxInherits = new(@"<%@\s*(?:Page|Control|Master)[^%]*Inherits\s*=\s*""([\w.]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AspxControl = new(@"<asp:(\w+)[^>]*\bID\s*=\s*""(\w+)""[^>]*?(?:On(?:Click|Command|SelectedIndexChanged|TextChanged)\s*=\s*""(\w+)"")?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex XamlClass = new(@"x:Class\s*=\s*""([\w.]+)""", RegexOptions.Compiled);
    private static readonly Regex XamlHandler = new(@"<(\w+)[^>]*?\b(?:Click|Checked|SelectionChanged|Loaded|TextChanged|MouseDoubleClick)\s*=\s*""(\w+)""", RegexOptions.Compiled);
    private static readonly Regex XamlBinding = new(@"<(\w+)[^>]*?\b(Command|Text|SelectedItem|SelectedValue|ItemsSource|IsChecked|Value)\s*=\s*""\{Binding\s+(?:Path=)?([\w.]+)", RegexOptions.Compiled);
    private static readonly Regex ProtoService = new(@"service\s+(\w+)\s*\{([^}]*)\}", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ProtoRpc = new(@"rpc\s+(\w+)\s*\(\s*(stream\s+)?(\w+)\s*\)\s*returns\s*\(\s*(stream\s+)?(\w+)\s*\)", RegexOptions.Compiled);

    public void Run()
    {
        foreach (var f in SolutionLoader.SafeEnumerate(_ctx.Root, "*.*"))
        {
            if (Paths.IsExcluded(f)) continue;
            var ext = Path.GetExtension(f).ToLowerInvariant();
            var name = Path.GetFileName(f).ToLowerInvariant();
            try
            {
                if (new FileInfo(f).Length > 8_000_000) continue;
                switch (ext)
                {
                    case ".sql": Sql(f); break;
                    case ".json" when name.StartsWith("appsettings") || name is "local.settings.json" or "launchsettings.json": JsonConfig(f); break;
                    case ".config" when name != "packages.config": XmlConfig(f); break;
                    case ".cshtml" or ".vbhtml" or ".razor": RazorView(f); break;
                    case ".aspx" or ".ascx" or ".master": WebForm(f); break;
                    case ".xaml": Xaml(f); break;
                    case ".edmx": Edmx(f); break;
                    case ".proto": Proto(f); break;
                    case ".wsdl" or ".svc" or ".asmx": _ctx.Pack.Integrations.ServiceEndpoints.Add($"{ext.TrimStart('.')}: {Rel(f)}"); break;
                }
            }
            catch (Exception ex)
            {
                _ctx.Pack.Manifest.Warnings.Add($"artifact scan failed for {Rel(f)}: {ex.Message}");
            }
        }
    }

    private string Rel(string f) => Paths.Relative(_ctx.Root, f);

    private void Sql(string f)
    {
        var text = File.ReadAllText(f);
        foreach (Match m in CreateObj.Matches(text))
        {
            var kind = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "proc" => "procedure",
                var k when k.StartsWith("package") => "package",
                var k => k,
            };
            var objName = m.Groups[2].Value.Replace("[", "").Replace("]", "");
            var line = text[..m.Index].Count(c => c == '\n') + 1;
            var obj = _ctx.Pack.Data.DbObjects.FirstOrDefault(o => string.Equals(o.Name, objName, StringComparison.OrdinalIgnoreCase) && o.Kind == kind);
            if (obj == null)
                _ctx.Pack.Data.DbObjects.Add(obj = new DbObject { Name = objName, Kind = kind, Source = "sql-file", Location = new Location { File = Rel(f), Line = line } });
            else { obj.Source = "sql-file"; obj.Location = new Location { File = Rel(f), Line = line }; }

            if (kind == "table")
            {
                var open = text.IndexOf('(', m.Index + m.Length);
                if (open < 0) continue;
                var depth = 0; var start = open + 1; var cols = new List<string>();
                for (var i = open; i < text.Length; i++)
                {
                    var c = text[i];
                    if (c == '(') depth++;
                    else if (c == ')') { depth--; if (depth == 0) { cols.Add(text[start..i]); break; } }
                    else if (c == ',' && depth == 1) { cols.Add(text[start..i]); start = i + 1; }
                }
                foreach (var col in cols.Select(c => c.Trim()).Where(c => c.Length > 0))
                {
                    if (Regex.IsMatch(col, @"^(CONSTRAINT|PRIMARY|FOREIGN|UNIQUE|INDEX|CHECK|KEY)\b", RegexOptions.IgnoreCase))
                    {
                        obj.Columns.Add("§" + Text.OneLine(col, 200)); // table-level constraint
                        if (col.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase) || col.Contains(" CHECK", StringComparison.OrdinalIgnoreCase))
                            _ctx.AddRule(new BusinessRule { Kind = "data-constraint", Title = $"{objName}: check constraint", Condition = Text.OneLine(col, 400), Confidence = "high", Location = new Location { File = Rel(f), Line = line } });
                        continue;
                    }
                    obj.Columns.Add(Text.OneLine(col, 200));
                }
            }
            else if (kind is "procedure" or "function" or "trigger")
            {
                // stored-procedure logic often holds business rules: flag RAISERROR/THROW conditions
                var end = text.IndexOf("\nGO", m.Index, StringComparison.OrdinalIgnoreCase);
                var body = end > 0 ? text[m.Index..end] : text[m.Index..Math.Min(text.Length, m.Index + 20000)];
                foreach (Match e in Regex.Matches(body, @"IF\s+(.{3,300}?)\s+(?:BEGIN\s+)?(?:RAISERROR|THROW)\s*\(?\s*(?:\d+\s*,\s*)?N?'([^']*)'", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    _ctx.AddRule(new BusinessRule
                    {
                        Kind = "validation",
                        Title = Text.OneLine(e.Groups[2].Value, 140),
                        Condition = Text.OneLine(e.Groups[1].Value, 300),
                        Outcome = "database raises error",
                        Message = e.Groups[2].Value,
                        ContainingType = $"db:{objName}",
                        Confidence = "high",
                        Location = new Location { File = Rel(f), Line = line + body[..e.Index].Count(c => c == '\n') },
                    });
                }
                foreach (var t in Classifiers.SqlTables(body)) if (!string.Equals(t, objName, StringComparison.OrdinalIgnoreCase) && !obj.Columns.Contains("uses:" + t)) obj.Columns.Add("uses:" + t);
            }
        }
    }

    private void JsonConfig(string f)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(f), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        void Walk(JsonElement e, string prefix)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, prefix.Length == 0 ? p.Name : prefix + ":" + p.Name);
                    break;
                case JsonValueKind.Array:
                    var i = 0;
                    foreach (var item in e.EnumerateArray()) Walk(item, $"{prefix}:{i++}");
                    break;
                default:
                    var v = e.ToString();
                    if (prefix.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase))
                    {
                        _ctx.Pack.Data.Connections.Add(new ConnectionInfo { Name = prefix[18..], Redacted = Text.RedactConnectionString(v), Provider = GuessProvider(v), File = Rel(f) });
                        v = Text.RedactConnectionString(v);
                    }
                    else if (Regex.IsMatch(prefix, "(?i)password|secret|key$|token|pwd|apikey|connectionstring")) v = "***";
                    _ctx.Pack.Integrations.Configuration.Add(new ConfigEntry { Key = prefix, Value = Text.OneLine(v, 200), File = Rel(f) });
                    if (Regex.IsMatch(v, @"^(https?|net\.tcp|amqps?|kafka|redis|sb)://", RegexOptions.IgnoreCase))
                        _ctx.Pack.Integrations.ServiceEndpoints.Add($"{prefix} = {Text.OneLine(v, 200)}");
                    break;
            }
        }
        Walk(doc.RootElement, "");
    }

    private void XmlConfig(string f)
    {
        var doc = XDocument.Load(f);
        foreach (var add in doc.Descendants().Where(e => e.Name.LocalName == "add" && e.Parent?.Name.LocalName == "connectionStrings"))
        {
            var cs = (string?)add.Attribute("connectionString") ?? "";
            _ctx.Pack.Data.Connections.Add(new ConnectionInfo
            {
                Name = (string?)add.Attribute("name") ?? "",
                Provider = (string?)add.Attribute("providerName") ?? GuessProvider(cs),
                Redacted = Text.RedactConnectionString(cs),
                File = Rel(f),
            });
        }
        foreach (var add in doc.Descendants().Where(e => e.Name.LocalName == "add" && e.Parent?.Name.LocalName == "appSettings"))
        {
            var key = (string?)add.Attribute("key") ?? "";
            var val = (string?)add.Attribute("value") ?? "";
            if (Regex.IsMatch(key, "(?i)password|secret|key$|token|pwd")) val = "***";
            _ctx.Pack.Integrations.Configuration.Add(new ConfigEntry { Key = "appSettings:" + key, Value = Text.OneLine(val, 200), File = Rel(f) });
        }
        foreach (var ep in doc.Descendants().Where(e => e.Name.LocalName == "endpoint"))
        {
            var addr = (string?)ep.Attribute("address");
            var contract = (string?)ep.Attribute("contract");
            var binding = (string?)ep.Attribute("binding");
            var role = ep.Parent?.Name.LocalName == "client" ? "wcf-client" : "wcf-service";
            _ctx.Pack.Integrations.ServiceEndpoints.Add($"{role}: contract={contract} binding={binding} address={addr} ({Rel(f)})");
        }
    }

    private static string GuessProvider(string cs)
    {
        var l = cs.ToLowerInvariant();
        if (l.Contains("data source=") && l.Contains(".db")) return "sqlite";
        if (l.Contains("host=") && (l.Contains("username=") || l.Contains("port=5432"))) return "postgresql";
        if (l.Contains("user id=") && l.Contains("data source=") && !l.Contains("initial catalog")) return "oracle?";
        if (l.Contains("server=") && l.Contains("uid=")) return "mysql?";
        if (l.Contains("initial catalog") || l.Contains("database=") || l.Contains("server=")) return "sqlserver";
        if (l.StartsWith("mongodb")) return "mongodb";
        if (l.Contains("accountendpoint=")) return "cosmosdb";
        return "unknown";
    }

    private void RazorView(string f)
    {
        var text = File.ReadAllText(f);
        var rel = Rel(f);
        string? screenId = null; string kind;
        var page = PageDirective.Match(text);
        if (page.Success)
        {
            var route = page.Groups[1].Value;
            kind = f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ? "blazor" : "razor-page";
            screenId = kind == "blazor" ? $"blazor:{Path.GetFileNameWithoutExtension(f)}" : $"page:{route}";
            var pagesIdx = rel.IndexOf("Pages/", StringComparison.OrdinalIgnoreCase);
            if (kind == "razor-page" && pagesIdx >= 0)
            {
                var pr = "/" + rel[(pagesIdx + 6)..].Replace(".cshtml", "");
                if (pr.EndsWith("/Index")) pr = pr[..^6] + "/";
                screenId = $"page:{pr}";
            }
            EnsureScreen(screenId, Text.Humanize(Path.GetFileNameWithoutExtension(f)) + $" ({route})", kind, rel);
        }
        else
        {
            var parts = rel.Split('/');
            var vIdx = Array.FindLastIndex(parts, p => p.Equals("Views", StringComparison.OrdinalIgnoreCase));
            if (vIdx >= 0 && parts.Length > vIdx + 2)
            {
                var ctrl = parts[vIdx + 1];
                var action = Path.GetFileNameWithoutExtension(parts[^1]);
                if (ctrl.Equals("Shared", StringComparison.OrdinalIgnoreCase) || action.StartsWith("_")) kind = "mvc-partial";
                else kind = "mvc-view";
                screenId = $"mvc:{ctrl}/{action}";
                EnsureScreen(screenId, $"{Text.Humanize(ctrl)} {Text.Humanize(action)}", kind, rel);
            }
            else if (f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            {
                screenId = $"blazor:{Path.GetFileNameWithoutExtension(f)}";
                EnsureScreen(screenId, Text.Humanize(Path.GetFileNameWithoutExtension(f)), "blazor-component", rel);
            }
        }
        if (screenId == null) return;
        var screen = _ctx.Pack.Journeys.Screens.First(s => s.Id == screenId);
        var currentCtrl = screenId.StartsWith("mvc:") ? screenId[4..].Split('/')[0] : null;

        foreach (Match m in AspFor.Matches(text))
        {
            var field = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (field.Length > 0 && !screen.Fields.Contains(field)) screen.Fields.Add(field);
        }

        // tag helpers: collect asp-controller / asp-action per element
        foreach (Match tag in Regex.Matches(text, @"<(a|form|button)\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var t = tag.Value;
            var ctrl = Regex.Match(t, @"asp-controller\s*=\s*""([^""]+)""").Groups[1].Value;
            var act = Regex.Match(t, @"asp-action\s*=\s*""([^""]+)""").Groups[1].Value;
            var pg = Regex.Match(t, @"asp-page\s*=\s*""([^""]+)""").Groups[1].Value;
            if (act.Length > 0) Nav(screenId, $"mvc:{(ctrl.Length > 0 ? ctrl : currentCtrl)}/{act}", tag.Groups[1].Value.ToLowerInvariant() == "form" ? "submit" : "link", screen, act);
            else if (pg.Length > 0) Nav(screenId, $"page:{pg}", "link", screen, pg);
        }
        foreach (Match m in ActionLink.Matches(text))
        {
            var args = Regex.Matches(m.Groups[1].Value, "\"([^\"]*)\"").Select(x => x.Groups[1].Value).ToList();
            var isLink = m.Value.StartsWith("Html.ActionLink") || m.Value.StartsWith("Html.RouteLink");
            var action = isLink ? args.ElementAtOrDefault(1) : args.ElementAtOrDefault(0);
            var ctrl = isLink ? args.ElementAtOrDefault(2) : args.ElementAtOrDefault(1);
            if (!string.IsNullOrEmpty(action)) Nav(screenId, $"mvc:{ctrl ?? currentCtrl}/{action}", m.Value.Contains("BeginForm") ? "submit" : "link", screen, action);
        }
        foreach (Match m in Href.Matches(text))
            Nav(screenId, "url:" + m.Groups[1].Value.TrimStart('~'), "link", screen, null);
    }

    private void WebForm(string f)
    {
        var text = File.ReadAllText(f);
        var inh = AspxInherits.Match(text);
        var cls = inh.Success ? inh.Groups[1].Value.Split('.').Last() : Path.GetFileNameWithoutExtension(f);
        var screenId = $"webforms:{cls}";
        EnsureScreen(screenId, Text.Humanize(Path.GetFileNameWithoutExtension(f)), "webforms", Rel(f));
        var screen = _ctx.Pack.Journeys.Screens.First(s => s.Id == screenId);
        foreach (Match m in AspxControl.Matches(text))
        {
            var ctl = m.Groups[1].Value; var id = m.Groups[2].Value;
            if (ctl is "Button" or "LinkButton" or "ImageButton" or "HyperLink" or "Menu") screen.Actions.Add($"{id}:{ctl}" + (m.Groups[3].Success ? $"->{m.Groups[3].Value}" : ""));
            else if (ctl is not ("Label" or "Literal" or "Panel" or "PlaceHolder" or "ScriptManager" or "UpdatePanel" or "Content" or "ContentPlaceHolder")) screen.Fields.Add($"{id}:{ctl}");
            // validators are declarative business rules
            if (ctl.EndsWith("Validator"))
            {
                var msg = Regex.Match(m.Value + text[m.Index..Math.Min(text.Length, m.Index + 400)], @"ErrorMessage\s*=\s*""([^""]*)""").Groups[1].Value;
                _ctx.AddRule(new BusinessRule
                {
                    Kind = "declarative-validation", Title = msg.Length > 0 ? msg : $"{cls}: {ctl} {id}",
                    Condition = Text.OneLine(text[m.Index..Math.Min(text.Length, m.Index + 300)].Split('>')[0], 300),
                    Message = msg.Length > 0 ? msg : null, Outcome = "reject invalid input", ContainingType = screenId, Confidence = "high",
                    Location = new Location { File = Rel(f), Line = text[..m.Index].Count(c => c == '\n') + 1 },
                });
            }
        }
        foreach (Match m in Href.Matches(text))
        {
            var target = m.Groups[1].Value;
            if (target.Contains(".aspx", StringComparison.OrdinalIgnoreCase))
                Nav(screenId, "webforms:" + Path.GetFileNameWithoutExtension(target.Split('?')[0]), "link", screen, null);
        }
    }

    private void Xaml(string f)
    {
        var text = File.ReadAllText(f);
        var cls = XamlClass.Match(text);
        if (!cls.Success) return;
        var shortName = cls.Groups[1].Value.Split('.').Last();
        if (shortName is "App") return;
        var screenId = $"wpf:{shortName}";
        EnsureScreen(screenId, Text.Humanize(shortName.Replace("Window", "").Replace("View", "").Replace("Page", "")), "wpf", Rel(f));
        var screen = _ctx.Pack.Journeys.Screens.First(s => s.Id == screenId);
        foreach (Match m in XamlHandler.Matches(text)) screen.Actions.Add($"{m.Groups[1].Value}->{m.Groups[2].Value}");
        foreach (Match m in XamlBinding.Matches(text))
        {
            if (m.Groups[2].Value == "Command") screen.Actions.Add($"{m.Groups[1].Value}->{m.Groups[3].Value}");
            else screen.Fields.Add($"{m.Groups[3].Value}:{m.Groups[1].Value}");
        }
        foreach (Match m in Href.Matches(text))
            if (m.Groups[1].Value.EndsWith(".xaml")) Nav(screenId, "wpf:" + Path.GetFileNameWithoutExtension(m.Groups[1].Value), "navigate", screen, null);
        // view <-> view-model link via DataContext / d:DataContext
        var vm = Regex.Match(text, @"(?:DataContext|d:DataContext)[^>]*?(?:Type|x:Type)\s*[=\s]\s*(?:\w+:)?(\w+ViewModel)");
        if (vm.Success) Nav(screenId, $"vm:{vm.Groups[1].Value}", "binds", screen, null);
    }

    private void Edmx(string f)
    {
        var doc = XDocument.Load(f);
        foreach (var es in doc.Descendants().Where(e => e.Name.LocalName == "EntitySet"))
        {
            var table = (string?)es.Attribute("Table") ?? (string?)es.Attribute("Name") ?? "";
            if (table.Length == 0) continue;
            if (!_ctx.Pack.Data.DbObjects.Any(o => string.Equals(o.Name, table, StringComparison.OrdinalIgnoreCase)))
                _ctx.Pack.Data.DbObjects.Add(new DbObject { Name = table, Kind = "table", Source = "edmx", Location = new Location { File = Rel(f), Line = 1 } });
        }
        foreach (var fn in doc.Descendants().Where(e => e.Name.LocalName == "Function" && e.Attribute("StoreFunctionName") == null && e.Attribute("Aggregate") != null))
        {
            var n = (string?)fn.Attribute("Name") ?? "";
            if (n.Length > 0 && !_ctx.Pack.Data.DbObjects.Any(o => o.Name == n))
                _ctx.Pack.Data.DbObjects.Add(new DbObject { Name = n, Kind = "procedure", Source = "edmx", Location = new Location { File = Rel(f), Line = 1 } });
        }
    }

    private void Proto(string f)
    {
        var text = File.ReadAllText(f);
        foreach (Match s in ProtoService.Matches(text))
            foreach (Match r in ProtoRpc.Matches(s.Groups[2].Value))
                _ctx.Pack.Integrations.ServiceEndpoints.Add($"grpc: {s.Groups[1].Value}.{r.Groups[1].Value}({r.Groups[2].Value}{r.Groups[3].Value}) returns ({r.Groups[4].Value}{r.Groups[5].Value}) [{Rel(f)}]");
    }

    private void EnsureScreen(string id, string name, string kind, string file)
    {
        var s = _ctx.Pack.Journeys.Screens.FirstOrDefault(x => x.Id == id);
        if (s == null) _ctx.Pack.Journeys.Screens.Add(new Screen { Id = id, Name = name, Kind = kind, Location = new Location { File = file, Line = 1 } });
        else s.Location ??= new Location { File = file, Line = 1 };
    }

    private void Nav(string from, string to, string kind, Screen screen, string? action)
    {
        _ctx.AddNavigation(from, to, kind);
        if (action != null && !screen.Actions.Contains(action)) screen.Actions.Add(action);
    }
}
