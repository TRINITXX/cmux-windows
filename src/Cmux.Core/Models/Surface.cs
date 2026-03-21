namespace Cmux.Core.Models;

public class Surface
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Terminal";
    public SplitNode RootSplitNode { get; set; } = SplitNode.CreateLeaf();
    public string? FocusedPaneId { get; set; }
    public Dictionary<string, string> PaneCustomNames { get; set; } = [];
    public Dictionary<string, PaneStateSnapshot> PaneSnapshots { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public bool BrowserVisible { get; set; }
    public string? BrowserUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
