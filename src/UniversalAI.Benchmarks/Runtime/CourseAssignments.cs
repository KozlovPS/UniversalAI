using System.Text.Json;

namespace UniversalAI.Benchmarks.Runtime;

public sealed record CourseAssignment(int Day, string Title, string Text, string Sha256);
public sealed record CourseAssignmentSnapshot(string Source, string ReadOn, string SourceSha256, List<CourseAssignment> Assignments);

/// <summary>Verbatim source snapshot. Plans and implementation notes never replace the assignment.</summary>
public static class CourseAssignments
{
    public static CourseAssignmentSnapshot Snapshot { get; } = Load();
    public static CourseAssignment? Find(string id) => id.StartsWith("day", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(id.AsSpan(3), out int day) ? Snapshot.Assignments.SingleOrDefault(a => a.Day == day) : null;
    private static CourseAssignmentSnapshot Load()
    {
        var assembly = typeof(CourseAssignments).Assembly;
        using var stream = assembly.GetManifestResourceStream("UniversalAI.Benchmarks.Materials.course-assignments.json")
            ?? throw new InvalidOperationException("Не найден полный снимок DAYS.md.");
        return JsonSerializer.Deserialize<CourseAssignmentSnapshot>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
