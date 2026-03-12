namespace RemoteX;

public sealed class QuickCommand
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Group { get; set; } = "默认";
    public string Description { get; set; } = "";
    public int SortOrder { get; set; }

    public bool HasVariables => Command.Contains('{');
}
