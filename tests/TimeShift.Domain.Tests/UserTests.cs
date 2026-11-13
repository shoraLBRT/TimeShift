using TimeShift.Domain.Users;

namespace TimeShift.Domain.Tests;

public class UserTests
{
    [Fact]
    public void New_user_gets_default_settings_from_spec()
    {
        var user = new User(42, "Test", User.DefaultTimeZone, DateTime.UtcNow);

        var s = user.Settings;
        Assert.Equal(30, s.PingIntervalMin);
        Assert.Equal(1.5, s.NormFactor);
        Assert.Equal(2.0, s.MaxFactor);
        Assert.Equal(2, s.MaxUnansweredPings);
        Assert.False(s.PingDuringBlocks);
        Assert.Equal(30, s.EveningReviewOffsetMin);
        Assert.Equal(5, s.RoundingMin);
    }

    [Fact]
    public void Created_at_must_be_utc()
    {
        Assert.Throws<ArgumentException>(() =>
            new User(42, "Test", User.DefaultTimeZone, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));
    }
}
