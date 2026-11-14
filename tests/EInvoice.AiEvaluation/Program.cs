using System.Globalization;
using System.Text.Json;
using EInvoice.AiEvaluation;
using Microsoft.Extensions.AI;
using OllamaSharp;

// dotnet run --project tests/EInvoice.AiEvaluation -- <command>
//   generate                     write testdata/ai-eval/E01.pdf ... from cases.json
//   verify                       fail if a committed PDF differs from cases.json
//   run --provider ollama --model qwen2.5:7b [--url http://127.0.0.1:11434/] [--cases E01,E07]
//   run --provider oracle        harness check: perfect answers must reach every expected result (needs Docker)
var root = EvalCaseFile.FindRepositoryRoot(AppContext.BaseDirectory);
var cases = EvalCaseFile.Load(EvalCaseFile.DefaultPath(root)).Cases;
var command = args.FirstOrDefault() ?? "help";
string? Option(string name) => args.SkipWhile(a => a != $"--{name}").Skip(1).FirstOrDefault();

switch (command)
{
    case "generate":
        foreach (var c in cases)
        {
            await File.WriteAllBytesAsync(EvalCaseFile.PdfPath(root, c.Id), EvalPdfRenderer.Render(c));
        }

        Console.WriteLine($"Wrote {cases.Count} PDFs to {EvalCaseFile.Folder(root)}.");
        return 0;

    case "verify":
        var drift = cases.Where(c => !File.Exists(EvalCaseFile.PdfPath(root, c.Id))
            || !File.ReadAllBytes(EvalCaseFile.PdfPath(root, c.Id)).AsSpan().SequenceEqual(EvalPdfRenderer.Render(c))).Select(c => c.Id).ToList();
        Console.WriteLine(drift.Count == 0 ? "All PDFs match cases.json." : $"Differ from cases.json: {string.Join(", ", drift)}. Run generate.");
        return drift.Count == 0 ? 0 : 1;

    case "run":
        return await RunAsync();

    default:
        Console.WriteLine("Commands: generate | verify | run --provider ollama|oracle [--model qwen2.5:7b] [--url http://127.0.0.1:11434/] [--cases E01,E02]");
        return command == "help" ? 0 : 2;
}

async Task<int> RunAsync()
{
    var provider = Option("provider") ?? "ollama";
    var only = Option("cases")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var selected = only is null ? cases : cases.Where(c => only.Contains(c.Id, StringComparer.Ordinal)).ToList();

    string model;
    string? digest = null;
    string? version = null;
    Func<EvalCase, IChatClient> modelFor;
    if (provider == "oracle")
    {
        model = "oracle (perfect answers)";
        modelFor = c => new OracleModel(c);
    }
    else if (provider == "ollama")
    {
        model = Option("model") ?? "qwen2.5:7b";
        var url = new Uri(Option("url") ?? "http://127.0.0.1:11434/");
        using var info = new HttpClient { BaseAddress = url, Timeout = TimeSpan.FromSeconds(30) };
        (version, digest) = await OllamaInfoAsync(info, model);
        modelFor = _ => new OllamaApiClient(new HttpClient { BaseAddress = url, Timeout = TimeSpan.FromMinutes(5) }, model);
    }
    else
    {
        Console.Error.WriteLine("--provider must be ollama or oracle.");
        return 2;
    }

    Console.WriteLine($"Starting a fresh SQL Server and running {selected.Count} cases with {model} ...");
    await using var database = await EvalDatabase.StartAsync(root, CancellationToken.None);
    var results = await new EvaluationRunner(database.AppConnectionString).RunAsync(selected, EvalPdfRenderer.Render, modelFor, CancellationToken.None);
    var scores = results.Select(Scorer.Score).ToList();

    var isMeasurement = provider == "ollama" && only is null;
    var report = EvaluationReport.ToMarkdown(new RunInfo(provider, model, digest, version, DateTimeOffset.UtcNow, isMeasurement), scores);
    Console.WriteLine();
    Console.WriteLine(report);

    if (isMeasurement)
    {
        // Only full runs with a real model are kept: they are what the README quotes.
        var folder = Path.Combine(EvalCaseFile.Folder(root), "results");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:yyyy-MM-dd-HHmm}-{model.Replace(':', '-')}.md"));
        await File.WriteAllTextAsync(file, report);
        Console.WriteLine($"Saved {file}");
    }

    // A harness run must be perfect; a model run reports, it does not fail.
    return provider == "oracle" && scores.Any(s => !s.OutcomeCorrect || s.WrongValueReachedMatched) ? 1 : 0;
}

static async Task<(string? Version, string? Digest)> OllamaInfoAsync(HttpClient http, string model)
{
    using var versionJson = JsonDocument.Parse(await http.GetStringAsync(new Uri("api/version", UriKind.Relative)));
    using var tags = JsonDocument.Parse(await http.GetStringAsync(new Uri("api/tags", UriKind.Relative)));
    var digest = tags.RootElement.GetProperty("models").EnumerateArray()
        .Where(m => m.GetProperty("name").GetString() == model)
        .Select(m => m.GetProperty("digest").GetString())
        .FirstOrDefault();
    return (versionJson.RootElement.GetProperty("version").GetString(), digest?[..12]);
}
