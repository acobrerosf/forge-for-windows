namespace Forge.Core.Setup;

/// <summary><c>%LOCALAPPDATA%\ForgeForWindows\state.json</c>, written atomically.</summary>
public sealed class StateStore
{
    public string Path { get; }

    public StateStore(string? path = null)
    {
        // FORGE_STATE_DIR points a development build at a throwaway state file.
        var dir = Environment.GetEnvironmentVariable("FORGE_STATE_DIR") is { Length: > 0 } custom
            ? custom
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForgeForWindows");
        Path = path ?? System.IO.Path.Combine(dir, "state.json");
    }

    public SetupState Load()
    {
        try
        {
            return File.Exists(Path) ? SetupState.FromJson(File.ReadAllText(Path)) : SetupState.Empty;
        }
        catch (IOException)
        {
            return SetupState.Empty;
        }
    }

    public void Save(SetupState state)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, state.ToJson());
        File.Move(temp, Path, overwrite: true);
    }
}
