namespace DocControl.Core.Models;

public sealed class LevelCodeRecord
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public int Level { get; set; }
    public required string Code { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
