namespace Forge.Core.Setup;

/// <summary><c>%LOCALAPPDATA%\ForgeForWindows\state.json</c>, written atomically.</summary>
public sealed class StateStore
{
    public string Path { get; }

    public StateStore(string? path = null)
    {
        // The profile picks the folder (ForgeForWindows, or ForgeForWindows-dev for a development build);
        // FORGE_STATE_DIR still points a run at a throwaway state file.
        Path = path ?? System.IO.Path.Combine(AppProfile.DataDirectory, "state.json");
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
