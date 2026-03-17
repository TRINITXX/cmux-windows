using Cmux.Core.Config;
using Cmux.Core.Models;

namespace Cmux.Core.Services;

public class WorkspaceTemplateService
{
    private static readonly List<WorkspaceTemplate> Defaults =
    [
        new() { Name = "VTC-Planner", Directory = "C:/Users/TRINITX/Desktop/VTC-Planner", StartupCommand = "claude --dangerously-skip-permissions --effort max", IconGlyph = "\uE8B7" },
        new() { Name = "FidelyPass", Directory = "C:/Users/TRINITX/Desktop/FidelyPass", StartupCommand = "claude --dangerously-skip-permissions --effort max", IconGlyph = "\uE8D7" },
        new() { Name = "Qwitt", Directory = "C:/Users/TRINITX/Desktop/Qwitt", StartupCommand = "claude --dangerously-skip-permissions --effort max", IconGlyph = "\uE8F1" },
        new() { Name = "dress_up", Directory = "C:/Users/TRINITX/Desktop/dress_up", StartupCommand = "claude --dangerously-skip-permissions --effort max", IconGlyph = "\uE771" },
        new() { Name = "Email verifier", Directory = "C:/Users/TRINITX/Desktop/Email verifier", StartupCommand = "claude --dangerously-skip-permissions --effort max", IconGlyph = "\uE715" },
        new() { Name = "Scraping", Directory = "C:/Users/TRINITX/Desktop/Scraping", StartupCommand = "claude --dangerously-skip-permissions --effort max", IconGlyph = "\uE774" },
    ];

    public List<WorkspaceTemplate> GetTemplates()
    {
        var settings = SettingsService.Current;
        if (settings.WorkspaceTemplates.Count == 0)
        {
            settings.WorkspaceTemplates = new List<WorkspaceTemplate>(Defaults);
            SettingsService.Save();
        }
        return settings.WorkspaceTemplates;
    }
}
