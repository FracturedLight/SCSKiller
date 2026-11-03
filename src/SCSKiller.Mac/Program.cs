using System.Diagnostics;
using System.Text.RegularExpressions;

const string Usage = """
    usage: scskiller-mac <command>
      setup <release folder> [--bottle B]   a bottle of its own, with the portable release and this fork's native files
      games                                 the Steam games of every CrossOver bottle
      add <game> [--bottle B]               add a game of `games` by a part of its name
      cache list                            D3DMetal's and DXMT's caches, one folder per exe name, shared by all bottles
      cache backup <exe> <folder>           copy D3DMetal's cache of an exe to the folder
      cache set-aside <exe> <folder>        move it there: the game's next start is cold
      cache restore <exe> <folder>          put the folder's copy back, the current one set aside beside it
      <anything else> [--bottle B]          the Windows CLI's command, run in the bottle (compile, status, scan, ...)
    The bottle defaults to SCSKILLER_BOTTLE, else scskiller.
    """;

var argv = args.ToList();
var bottle = Take(argv, "--bottle") ?? Environment.GetEnvironmentVariable("SCSKILLER_BOTTLE") ?? "scskiller";
var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var bottles = Path.Combine(home, "Library", "Application Support", "CrossOver", "Bottles");

if (argv.Count == 0 || argv[0] is "-h" or "--help") { Console.WriteLine(Usage); return 0; }
try
{
    return argv[0] switch
    {
        "setup" when argv.Count == 2 => Setup(argv[1]),
        "games" => Games(),
        "add" when argv.Count == 2 => Add(argv[1]),
        "cache" when argv.Count == 2 && argv[1] == "list" => CacheList(),
        "cache" when argv.Count == 4 && argv[1] == "backup" => CacheCopy(argv[2], argv[3]),
        "cache" when argv.Count == 4 && argv[1] == "set-aside" => CacheAside(argv[2], argv[3]),
        "cache" when argv.Count == 4 && argv[1] == "restore" => CacheRestore(argv[2], argv[3]),
        "setup" or "add" or "cache" => Fail(Usage),
        _ => Cli(argv),
    };
}
catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
{
    return Fail(e.Message);
}

int Setup(string release)
{
    if (!File.Exists(Path.Combine(release, "current", "cli", "scskiller.exe"))) return Fail($"no portable release in {release}");
    var root = Path.Combine(bottles, bottle);
    if (!Directory.Exists(root) && Run(CrossOverTool("cxbottle"), ["--bottle", bottle, "--create", "--template", "win10_64"]) != 0) return Fail("the bottle wasn't created");
    var to = Path.Combine(root, "drive_c", "SCSKiller");
    if (Directory.Exists(to)) return Fail($"{to} is there already");
    CopyTree(release, to);
    Console.WriteLine($"{bottle}: ready");
    return 0;
}

int Games()
{
    foreach (var g in Installed()) Console.WriteLine($"{g.Bottle,-16} {g.AppId,-10} {g.Name}");
    return 0;
}

int Add(string part)
{
    var found = Installed().Where(g => g.Name.Contains(part, StringComparison.OrdinalIgnoreCase)).ToList();
    if (found.Count != 1) return Fail(found.Count == 0 ? $"no installed game matches '{part}'" : $"'{part}' matches {string.Join(", ", found.Select(g => g.Name))}");
    var exe = MainExe(found[0].Folder);
    if (exe == null) return Fail($"{found[0].Name}: no exe found in {found[0].Folder}");
    return Cli(["add", WindowsPath(exe), WindowsPath(found[0].Folder)]);
}

int CacheList()
{
    foreach (var kind in new[] { "d3dm", "dxmt" })
    {
        var root = Path.Combine(UserCache(), kind);
        if (!Directory.Exists(root)) continue;
        foreach (var d in Directory.GetDirectories(root).OrderBy(d => d))
            Console.WriteLine($"{kind} {Size(d) / 1048576.0,9:F1} MB  {Path.GetFileName(d)}");
    }
    return 0;
}

int CacheCopy(string exe, string folder)
{
    CopyTree(D3DMetalCache(exe), Path.Combine(folder, exe));
    return 0;
}

int CacheAside(string exe, string folder)
{
    Directory.CreateDirectory(folder);
    MoveTree(D3DMetalCache(exe), Path.Combine(folder, exe));
    return 0;
}

int CacheRestore(string exe, string folder)
{
    var kept = Path.Combine(folder, exe);
    if (!Directory.Exists(kept)) return Fail($"no copy of {exe} in {folder}");
    var live = Path.Combine(UserCache(), "d3dm", exe);
    if (Directory.Exists(live)) MoveTree(live, $"{kept}.replaced-{DateTime.Now:yyyyMMdd-HHmmss}");
    MoveTree(kept, live);
    return 0;
}

// never replaces: a folder already there is an earlier backup
void MoveTree(string from, string to)
{
    if (Directory.Exists(to)) throw new IOException($"{to} is there already");
    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
    CopyTree(from, to);
    if (Size(from) != Size(to)) throw new IOException($"the copy of {from} differs: it stays");
    Directory.Delete(from, true);
}

string D3DMetalCache(string exe)
{
    if (Path.GetFileName(exe) != exe) throw new InvalidOperationException($"{exe} is not an exe name");
    var d = Path.Combine(UserCache(), "d3dm", exe);
    return Directory.Exists(d) ? d : throw new InvalidOperationException($"D3DMetal has no cache of {exe}");
}

int Cli(IReadOnlyList<string> cli) => Run(CrossOverTool("wine"), ["--bottle", bottle, "--no-gui", @"C:\SCSKiller\current\cli\scskiller.exe", .. cli]);

IEnumerable<SteamGame> Installed()
{
    if (!Directory.Exists(bottles)) yield break;
    foreach (var root in Directory.GetDirectories(bottles).OrderBy(d => d))
    {
        var apps = Path.Combine(root, "drive_c", "Program Files (x86)", "Steam", "steamapps");
        if (!Directory.Exists(apps)) continue;
        foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf").OrderBy(f => f))
        {
            var text = File.ReadAllText(acf);
            string? Key(string name) => Regex.Match(text, $"\"{name}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
            if (Key("appid") is { } id && Key("name") is { } name && Key("installdir") is { } dir && Directory.Exists(Path.Combine(apps, "common", dir)))
                yield return new SteamGame(Path.GetFileName(root), id, name, Path.Combine(apps, "common", dir));
        }
    }
}

// the largest exe, a shipping binary before its launcher stub; the Windows CLI's `exe <game> <exe>` corrects a wrong pick
static string? MainExe(string folder) => Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories)
    .Where(f => !Regex.IsMatch(Path.GetFileName(f), "crash|redist|setup|unins|vc_|dxsetup|crs-|helper", RegexOptions.IgnoreCase))
    .OrderByDescending(f => f.Contains("Shipping", StringComparison.OrdinalIgnoreCase))
    .ThenByDescending(f => new FileInfo(f).Length)
    .FirstOrDefault();

// a bottle maps Y: to the home folder and Z: to /
string WindowsPath(string path) => path.StartsWith(home + "/")
    ? @"Y:\" + path[(home.Length + 1)..].Replace('/', '\\')
    : "Z:" + path.Replace('/', '\\');

string CrossOverTool(string name)
{
    foreach (var app in new[] { Path.Combine(home, "Applications", "CrossOver.app"), "/Applications/CrossOver.app" })
    {
        var tool = Path.Combine(app, "Contents", "SharedSupport", "CrossOver", "bin", name);
        if (File.Exists(tool)) return tool;
    }
    throw new InvalidOperationException("CrossOver.app isn't in ~/Applications or /Applications");
}

static string UserCache()
{
    var p = new ProcessStartInfo("/usr/bin/getconf", "DARWIN_USER_CACHE_DIR") { RedirectStandardOutput = true };
    using var getconf = Process.Start(p)!;
    var dir = getconf.StandardOutput.ReadToEnd().Trim();
    getconf.WaitForExit();
    return dir.Length > 0 ? dir : throw new InvalidOperationException("no user cache folder");
}

static long Size(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

static void CopyTree(string from, string to)
{
    if (Directory.Exists(to)) throw new IOException($"{to} is there already");
    Directory.CreateDirectory(to);
    foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
    foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
}

static int Run(string tool, IEnumerable<string> arguments)
{
    var p = new ProcessStartInfo(tool);
    foreach (var a in arguments) p.ArgumentList.Add(a);
    using var process = Process.Start(p)!;
    process.WaitForExit();
    return process.ExitCode;
}

static int Fail(string why)
{
    Console.Error.WriteLine(why);
    return 1;
}

static string? Take(List<string> list, string option)
{
    var i = list.IndexOf(option);
    if (i < 0 || i + 1 >= list.Count) return null;
    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

record SteamGame(string Bottle, string AppId, string Name, string Folder);
