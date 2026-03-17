namespace Cmux.Core.Models;

public class WorkspaceTemplate
{
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public string? StartupCommand { get; set; }
    public string? IconGlyph { get; set; }
    public string? AccentColor { get; set; }
}
