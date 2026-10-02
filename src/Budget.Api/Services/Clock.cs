namespace Budget.Api.Services;

public class Clock
{
    private readonly TimeZoneInfo _tz;

    public Clock(IConfiguration config)
    {
        var id = config["Budget:TimeZone"] ?? "Asia/Singapore";
        _tz = TimeZoneInfo.FindSystemTimeZoneById(id);
    }

    public DateTime UtcNow => DateTime.UtcNow;
    public DateOnly Today => ToLocalDate(DateTime.UtcNow);

    public DateOnly ToLocalDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _tz));

    public DateTime LocalDateStartToUtc(DateOnly date) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), _tz);

    /// <summary>
    /// Accepts ISO-8601 with or without offset. No offset = local (Singapore) time.
    /// Null/blank/unparseable = now.
    /// </summary>
    public DateTime ParseToUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return UtcNow;

        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dto))
        {
            var hasOffset = value.Contains('Z') || value.Contains('+') ||
                            System.Text.RegularExpressions.Regex.IsMatch(value, @"T.*-\d{2}:?\d{2}$");
            if (hasOffset) return dto.UtcDateTime;

            var local = DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(local, _tz);
        }

        return UtcNow;
    }
}
