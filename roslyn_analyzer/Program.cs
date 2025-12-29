using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;

using System.CommandLine;

using Microsoft.Build.Locator;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

public sealed record ClassMetrics(
    string Id,
    string Name,
    string Namespace,
    string Project,
    string Service,
    string FilePath,
    Dictionary<string, double> Metrics
);

public sealed record DependencyEdge(string SourceClassId, string TargetClassId);

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var solutionOpt = new Option<string>("--solution") { IsRequired = true };
        var repoRootOpt = new Option<string>("--repoRoot") { IsRequired = true };
        var outJsonOpt = new Option<string>("--outJson") { IsRequired = true };
        var outDepsOpt = new Option<string>("--outDeps") { IsRequired = true };
		
        var msbuildPathOpt = new Option<string?>(
            "--msbuildPath",
            description: "Explicit MSBuild path to register (e.g., C:\\Program Files\\dotnet\\sdk\\10.0.101). Optional."
        );

        var root = new RootCommand("Roslyn analyzer for MS-QDR");
        root.AddOption(solutionOpt);
        root.AddOption(repoRootOpt);
        root.AddOption(outJsonOpt);
        root.AddOption(outDepsOpt);
        root.AddOption(msbuildPathOpt);

        root.SetHandler(async (solutionPath, repoRoot, outJson, outDeps, msbuildPath) =>
        {

            EnsureMSBuildRegistered(msbuildPath);

            using var workspace = MSBuildWorkspace.Create();
            workspace.WorkspaceFailed += (_, e) => Console.Error.WriteLine($"[MSBuildWorkspace] {e.Diagnostic}");

            var sln = await workspace.OpenSolutionAsync(solutionPath);
            var repoRootFull = Path.GetFullPath(repoRoot);


            var classes = new List<ClassMetrics>();
            var edges = new List<DependencyEdge>();

            var typeToId = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var proj in sln.Projects)
            {
                foreach (var doc in proj.Documents)
                {
                    if (doc.SourceCodeKind != SourceCodeKind.Regular) continue;
                    if (doc.FilePath is null) continue;
                    if (!doc.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;

                    var tree = await doc.GetSyntaxTreeAsync();
                    if (tree is null) continue;
                    var rootNode = await tree.GetRootAsync();

                    foreach (var cls in rootNode.DescendantNodes().OfType<ClassDeclarationSyntax>())
                    {
                        var ns = GetNamespace(cls);
                        var name = cls.Identifier.Text;
                        var fq = string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
                        var id = $"{proj.Name}:{fq}";
                        if (!typeToId.ContainsKey(fq))
                            typeToId[fq] = id;
                    }
                }
            }

            var projectRefs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var proj in sln.Projects)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pr in proj.ProjectReferences)
                {
                    var target = sln.GetProject(pr.ProjectId);
                    if (target != null) set.Add(target.Name);
                }
                projectRefs[proj.Name] = set;
            }
            var projectsInCycles = ComputeProjectsInCycles(projectRefs);

            foreach (var proj in sln.Projects)
            {
                var compilation = await proj.GetCompilationAsync();
                if (compilation is null) continue;

                bool projectInCycle = projectsInCycles.Contains(proj.Name);

                foreach (var doc in proj.Documents)
                {
                    if (doc.SourceCodeKind != SourceCodeKind.Regular) continue;
                    if (doc.FilePath is null) continue;
                    if (!doc.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;

                    var tree = await doc.GetSyntaxTreeAsync();
                    if (tree is null) continue;

                    var semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                    var rootNode = await tree.GetRootAsync();

                    foreach (var cls in rootNode.DescendantNodes().OfType<ClassDeclarationSyntax>())
                    {
                        var ns = GetNamespace(cls);
                        var name = cls.Identifier.Text;
                        var fq = string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
                        var id = $"{proj.Name}:{fq}";

                        var filePath = doc.FilePath ?? "";
                        var service = InferService(repoRootFull, filePath);

                        var loc = CountLines(tree, cls.Span);


                        int nom = cls.Members.OfType<MethodDeclarationSyntax>().Count();
                        int nof = cls.Members.OfType<FieldDeclarationSyntax>().Sum(fd => fd.Declaration.Variables.Count)
                                  + cls.Members.OfType<PropertyDeclarationSyntax>().Count();


                        double wmc = nom;
                        var invoked = new HashSet<string>(StringComparer.Ordinal);
                        var depTypes = new HashSet<string>(StringComparer.Ordinal);
                        int cyclomatic = 0;


                        int layerViolations = 0;

                        foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>())
                        {
                            cyclomatic += CyclomaticOfMethod(method);

                            foreach (var inv in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                            {
                                var sym = semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                                if (sym is null) continue;

                                invoked.Add($"{sym.ContainingType.ToDisplayString()}.{sym.Name}");

                                var t = sym.ContainingType.ToDisplayString();
                                depTypes.Add(t);


                                if (typeToId.TryGetValue(t, out var targetId))
                                {
                                    edges.Add(new DependencyEdge(id, targetId));
                                }


                                layerViolations += LayerViolationDelta(filePath, sym);
                            }
                        }


                        foreach (var field in cls.Members.OfType<FieldDeclarationSyntax>())
                        {
                            var typeInfo = semanticModel.GetTypeInfo(field.Declaration.Type).Type;
                            if (typeInfo != null)
                            {
                                var t = typeInfo.ToDisplayString();
                                depTypes.Add(t);
                                if (typeToId.TryGetValue(t, out var targetId))
                                    edges.Add(new DependencyEdge(id, targetId));
                                layerViolations += LayerViolationDelta(filePath, typeInfo);
                            }
                        }
                        foreach (var prop in cls.Members.OfType<PropertyDeclarationSyntax>())
                        {
                            var typeInfo = semanticModel.GetTypeInfo(prop.Type).Type;
                            if (typeInfo != null)
                            {
                                var t = typeInfo.ToDisplayString();
                                depTypes.Add(t);
                                if (typeToId.TryGetValue(t, out var targetId))
                                    edges.Add(new DependencyEdge(id, targetId));
                                layerViolations += LayerViolationDelta(filePath, typeInfo);
                            }
                        }


                        depTypes.RemoveWhere(IsIgnorableDependency);


                        double cbo = depTypes.Count;
                        double rfc = invoked.Count;

                        var metrics = new Dictionary<string, double>()
                        {
                            ["LOC"] = loc,
                            ["NOM"] = nom,
                            ["NOF"] = nof,
                            ["WMC"] = wmc,
                            ["RFC"] = rfc,
                            ["CBO"] = cbo,
                            ["Cyclomatic"] = cyclomatic,
                            ["LayerViolations"] = layerViolations,
                            ["CycleInvolvement"] = projectInCycle ? 1 : 0
                        };

                        classes.Add(new ClassMetrics(
                            id,
                            name,
                            ns,
                            proj.Name,
                            service,
                            NormalizePath(repoRootFull, filePath),
                            metrics
                        ));
                    }
                }
            }


            var edgeSet = new HashSet<(string, string)>();
            var uniqEdges = new List<DependencyEdge>();
            foreach (var e in edges)
            {
                var key = (e.SourceClassId, e.TargetClassId);
                if (edgeSet.Add(key)) uniqEdges.Add(e);
            }


            var payload = new
            {
                generatedAtUtc = DateTime.UtcNow.ToString("O"),
                solution = solutionPath,
                classes = classes
            };
            var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
            Directory.CreateDirectory(Path.GetDirectoryName(outJson)!);
            await File.WriteAllTextAsync(outJson, JsonSerializer.Serialize(payload, jsonOpts));


            Directory.CreateDirectory(Path.GetDirectoryName(outDeps)!);
            await using (var sw = new StreamWriter(outDeps))
            {
                await sw.WriteLineAsync("sourceClassId,targetClassId");
                foreach (var e in uniqEdges)
                    await sw.WriteLineAsync($"{e.SourceClassId},{e.TargetClassId}");
            }

            Console.WriteLine($"Wrote: {outJson}");
            Console.WriteLine($"Wrote: {outDeps}");
        }, solutionOpt, repoRootOpt, outJsonOpt, outDepsOpt, msbuildPathOpt);

        return await root.InvokeAsync(args);
    }


    static void EnsureMSBuildRegistered(string? msbuildPath)
    {
        if (MSBuildLocator.IsRegistered) return;

        var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
        if (instances.Length > 0)
        {
            var best = instances.OrderByDescending(i => i.Version).First();
            Console.WriteLine($"[MSBuildLocator] Using VS instance: {best.Name} {best.Version} ({best.MSBuildPath})");
            MSBuildLocator.RegisterInstance(best);
            return;
        }

        if (!string.IsNullOrWhiteSpace(msbuildPath))
        {
            var full = Path.GetFullPath(msbuildPath);
            if (!Directory.Exists(full))
                throw new InvalidOperationException($"--msbuildPath does not exist: {full}");

            Console.WriteLine($"[MSBuildLocator] Using provided --msbuildPath: {full}");
            MSBuildLocator.RegisterMSBuildPath(full);
            return;
        }

        throw new InvalidOperationException(
            "No instances of MSBuild could be detected.\n" +
            "Provide --msbuildPath (example: C:\\Program Files\\dotnet\\sdk\\10.0.101) or install Visual Studio Build Tools."
        );
    }

    static string GetNamespace(SyntaxNode cls)
    {

        var names = new Stack<string>();
        SyntaxNode? n = cls.Parent;
        while (n != null)
        {
            if (n is NamespaceDeclarationSyntax nds)
                names.Push(nds.Name.ToString());
            else if (n is FileScopedNamespaceDeclarationSyntax fns)
                names.Push(fns.Name.ToString());
            n = n.Parent;
        }
        return string.Join(".", names);
    }

    static int CountLines(SyntaxTree tree, TextSpan span)
    {
        var text = tree.GetText();
        var startLine = text.Lines.GetLineFromPosition(span.Start).LineNumber;
        var endLine = text.Lines.GetLineFromPosition(span.End).LineNumber;
        return Math.Max(1, endLine - startLine + 1);
    }

    static int CyclomaticOfMethod(MethodDeclarationSyntax method)
    {

        int count = 1;
        foreach (var node in method.DescendantNodes())
        {
            switch (node.Kind())
            {
                case SyntaxKind.IfStatement:
                case SyntaxKind.ForStatement:
                case SyntaxKind.ForEachStatement:
                case SyntaxKind.ForEachVariableStatement:
                case SyntaxKind.WhileStatement:
                case SyntaxKind.DoStatement:
                case SyntaxKind.CaseSwitchLabel:
                case SyntaxKind.CatchClause:
                case SyntaxKind.ConditionalExpression:
                    count++;
                    break;
                case SyntaxKind.LogicalAndExpression:
                case SyntaxKind.LogicalOrExpression:
                    count++;
                    break;
            }
        }
        return count;
    }

    static bool IsIgnorableDependency(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return true;

        if (typeName.StartsWith("System.", StringComparison.Ordinal)) return true;
        if (typeName is "string" or "int" or "long" or "double" or "float" or "decimal" or "bool" or "byte" or "char") return true;

        if (typeName.StartsWith("System.Collections", StringComparison.Ordinal)) return true;
        return false;
    }

    static string InferService(string repoRoot, string filePath)
    {
        var full = Path.GetFullPath(filePath);
        if (!full.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
            return "Unknown";

        var rel = NormalizePath(repoRoot, full);
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "Unknown";


        var top = parts[0];

        if (top is "Application" or "Domain" or "Infrastructure" or "Infrastructure.Persistence" or "Common")
            return "Shared";
        if (top.Equals("WebApi", StringComparison.OrdinalIgnoreCase))
            return "WebApi";
        if (top.Equals("AuthServer", StringComparison.OrdinalIgnoreCase))
            return "AuthServer";
        if (top.EndsWith("Service", StringComparison.OrdinalIgnoreCase))
            return top;

        return top;
    }

    static string NormalizePath(string repoRoot, string fullPath)
    {
        var rel = Path.GetRelativePath(repoRoot, fullPath);
        return rel.Replace('\\', '/');
    }

    static int LayerViolationDelta(string filePath, ISymbol sym)
    {
        var target = sym.ContainingAssembly?.Name ?? "";
        return LayerViolationDelta(filePath, target);
    }

    static int LayerViolationDelta(string filePath, ITypeSymbol typeSym)
    {
        var target = typeSym.ContainingAssembly?.Name ?? "";
        return LayerViolationDelta(filePath, target);
    }

    static int LayerViolationDelta(string filePath, string targetAssemblyName)
    {

        var fp = filePath.Replace('\\','/');

        bool sourceIsDomain = fp.Contains("/Domain/", StringComparison.OrdinalIgnoreCase);
        bool sourceIsApplication = fp.Contains("/Application/", StringComparison.OrdinalIgnoreCase);

        bool targetIsInfrastructure = targetAssemblyName.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase)
                                    || targetAssemblyName.Contains("Persistence", StringComparison.OrdinalIgnoreCase)
                                    || targetAssemblyName.Contains("WebApi", StringComparison.OrdinalIgnoreCase);


        if ((sourceIsDomain || sourceIsApplication) && targetIsInfrastructure)
            return 1;

        return 0;
    }

    static HashSet<string> ComputeProjectsInCycles(Dictionary<string, HashSet<string>> graph)
    {

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        void Dfs1(string v)
        {
            visited.Add(v);
            if (graph.TryGetValue(v, out var nbrs))
            {
                foreach (var n in nbrs)
                    if (!visited.Contains(n)) Dfs1(n);
            }
            order.Add(v);
        }
        foreach (var v in graph.Keys)
            if (!visited.Contains(v)) Dfs1(v);

        var rev = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var v in graph.Keys) rev[v] = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in graph)
        {
            foreach (var n in kv.Value)
            {
                if (!rev.ContainsKey(n)) rev[n] = new HashSet<string>(StringComparer.Ordinal);
                rev[n].Add(kv.Key);
            }
        }

        var inCycle = new HashSet<string>(StringComparer.Ordinal);
        visited.Clear();

        void Dfs2(string v, List<string> comp)
        {
            visited.Add(v);
            comp.Add(v);
            if (rev.TryGetValue(v, out var nbrs))
            {
                foreach (var n in nbrs)
                    if (!visited.Contains(n)) Dfs2(n, comp);
            }
        }

        for (int i = order.Count - 1; i >= 0; i--)
        {
            var v = order[i];
            if (visited.Contains(v)) continue;
            var comp = new List<string>();
            Dfs2(v, comp);
            if (comp.Count > 1)
            {
                foreach (var x in comp) inCycle.Add(x);
            }
        }

        return inCycle;
    }
}
