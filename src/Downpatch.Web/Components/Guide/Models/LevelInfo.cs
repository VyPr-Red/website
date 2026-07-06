namespace Downpatch.Web.Components.Guide.Models;

public class LevelInfo
{
    public string Title { get; set; } = "";

    public string Game { get; set; } = "";

    public string? MissionNumber { get; set; }

    public string HeroImage { get; set; } = "";

    public string MapName { get; set; } = "";

    public string PreviousName { get; set; } = "";

    public string PreviousUrl { get; set; } = "";

    public string NextName { get; set; } = "";

    public string NextUrl { get; set; } = "";

    public List<GoalInfo> Goals { get; set; } = [];
}
