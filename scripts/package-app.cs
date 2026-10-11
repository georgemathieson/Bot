// Builds appPackage/build/appPackage.zip (manifest + icons) for uploading to Teams.
//
//   dotnet run scripts/package-app.cs -- <bot-client-id> [bot-app-id-uri] [teams-app-id]
//
// bot-client-id   -> bots[0].botId and webApplicationInfo.id (the bot app registration's client ID)
// bot-app-id-uri  -> webApplicationInfo.resource (default api://botid-<bot-client-id>)
// teams-app-id    -> manifest id (default: the bot client ID)
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var root = FindRepoRoot();
var appDir = Path.Combine(root, "appPackage");
var outDir = Path.Combine(appDir, "build");

var botId = args.ElementAtOrDefault(0) ?? Environment.GetEnvironmentVariable("BOT_ID");
if (string.IsNullOrWhiteSpace(botId) || !Guid.TryParse(botId, out _))
{
    Console.Error.WriteLine("Usage: dotnet run scripts/package-app.cs -- <bot-client-id> [bot-app-id-uri] [teams-app-id]");
    return 1;
}

var values = new Dictionary<string, string>
{
    ["BOT_ID"] = botId,
    ["BOT_APP_ID_URI"] = args.ElementAtOrDefault(1) is { Length: > 0 } uri ? uri : $"api://botid-{botId}",
    ["TEAMS_APP_ID"] = args.ElementAtOrDefault(2) is { Length: > 0 } appId ? appId : botId,
};

var manifest = Regex.Replace(
    File.ReadAllText(Path.Combine(appDir, "manifest.json")),
    @"\$\{\{(\w+)\}\}",
    m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : throw new InvalidOperationException($"No value for {m.Value}"));
JsonNode.Parse(manifest); // fail fast on broken JSON

Directory.CreateDirectory(outDir);
File.WriteAllText(Path.Combine(outDir, "manifest.json"), manifest);

var zipPath = Path.Combine(outDir, "appPackage.zip");
File.Delete(zipPath);
using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
{
    zip.CreateEntryFromFile(Path.Combine(outDir, "manifest.json"), "manifest.json");
    zip.CreateEntryFromFile(Path.Combine(appDir, "color.png"), "color.png");
    zip.CreateEntryFromFile(Path.Combine(appDir, "outline.png"), "outline.png");
}

Console.WriteLine($"Wrote {zipPath}");
Console.WriteLine($"  botId / webApplicationInfo.id : {values["BOT_ID"]}");
Console.WriteLine($"  webApplicationInfo.resource   : {values["BOT_APP_ID_URI"]}");
return 0;

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "appPackage", "manifest.json"))) return dir.FullName;
    }
    throw new InvalidOperationException("Run this from inside the repository (appPackage/manifest.json not found).");
}
