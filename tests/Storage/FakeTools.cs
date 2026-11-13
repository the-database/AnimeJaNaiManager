using System.Text.Json;
using System.Text.Json.Nodes;

// This test's own exe stands in for AnimeJaNaiUpdater (`--storage ...`) and aji-runtime
// (`location --json`) when started with their arguments and AJM_FAKE_DIR set. In that folder:
// storage.json is what `--storage --json` prints; mode.txt makes the updater "old" (exits 2, as an
// updater without --storage does), "garbage" (prints something else) or "fail" (a switch fails);
// location.json is what `location --json` prints; each call's working directory and arguments are
// appended to calls.txt ("<cwd>\x1e<arg>\x1f<arg>...").
static class FakeTools
{
    public static int? Run(string[] args)
    {
        if (Environment.GetEnvironmentVariable("AJM_FAKE_DIR") is not { Length: > 0 } dir ||
            args.Length == 0 || args[0] is not ("--storage" or "location"))
        {
            return null;
        }
        lock (typeof(FakeTools))
        {
            File.AppendAllText(Path.Combine(dir, "calls.txt"), Environment.CurrentDirectory + "\u001e" + string.Join("\u001f", args) + "\n");
        }
        string Read(string name) => File.Exists(Path.Combine(dir, name)) ? File.ReadAllText(Path.Combine(dir, name)).Trim() : "";
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };

        if (args[0] == "location")
        {
            stdout.WriteLine(Read("location.json"));
            return 0;
        }

        string mode = Read("mode.txt");
        if (mode == "old")
        {
            stdout.WriteLine("Usage: AnimeJaNaiUpdater.exe [--check|--apply|--components|--install <pack>|--remove <pack>|--auto|--recommend]");
            return 2;
        }
        if (args is [_, "--json"])
        {
            stdout.WriteLine(mode == "garbage" ? "Checking for updates..." : Read("storage.json"));
            return 0;
        }

        // --storage inside | shared | <folder>
        stdout.WriteLine("Moving the TensorRT runtime...");
        if (mode == "fail")
        {
            stdout.WriteLine("AJI_MOVE 0 2000000");
            Console.Error.WriteLine("warning: something earlier");
            Thread.Sleep(200);
            stdout.WriteLine("The player is still running. Close it and try again.");
            stdout.WriteLine("");
            return 1;
        }
        var state = JsonNode.Parse(Read("storage.json"))!.AsObject();
        string target = args[1];
        (string choice, string root) = target switch
        {
            "inside" => ("inside", (string)state["inside"]!),
            "shared" => ("shared", (string)state["shared"]!),
            _ => ("custom", target),
        };
        state["choice"] = choice;
        state["root"] = root;
        state["available"] = true;
        stdout.WriteLine("AJI_MOVE 1000000 2000000");
        stdout.WriteLine("AJI_MOVE 2000000 2000000");
        File.WriteAllText(Path.Combine(dir, "storage.json"), state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        stdout.WriteLine("Done.");
        return 0;
    }
}
