using TimeShift.Domain.Activities;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Facts;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Users;

namespace TimeShift.Domain.Tests;

/// <summary>Правила классификации по времени суток (spec §4.2, #52): часть суток, выбор категории, хранение как факт.</summary>
public class CategoryRuleTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    /// <summary>Москва (UTC+3), 14 ноября 2026.</summary>
    private static DateTime Msk(int hour, int minute = 0) =>
        new DateTime(2026, 11, 14, hour, minute, 0, DateTimeKind.Utc).AddHours(-3);

    [Theory]
    [InlineData(0, 0, PartOfDay.Night)]
    [InlineData(5, 59, PartOfDay.Night)]
    [InlineData(6, 0, PartOfDay.Morning)]
    [InlineData(11, 59, PartOfDay.Morning)]
    [InlineData(12, 0, PartOfDay.Afternoon)]
    [InlineData(17, 59, PartOfDay.Afternoon)]
    [InlineData(18, 0, PartOfDay.Evening)]
    [InlineData(23, 59, PartOfDay.Evening)]
    public void Part_of_day_is_taken_in_the_users_time_zone_with_fixed_bounds(int hour, int minute, PartOfDay expected)
    {
        Assert.Equal(expected, PartsOfDay.Of(Msk(hour, minute), Moscow));

        // Границы те же, по которым разбирается «вечером» в тексте: момент лежит внутри своей части суток.
        var (start, end) = PartsOfDay.Bounds(expected);
        var time = new TimeOnly(hour, minute);
        Assert.True(start <= time && (end == TimeOnly.MinValue || time < end));
    }

    [Fact]
    public void The_same_instant_is_a_different_part_of_day_in_another_time_zone()
    {
        var instant = Msk(19); // 16:00 UTC
        Assert.Equal(PartOfDay.Evening, PartsOfDay.Of(instant, Moscow));
        Assert.Equal(PartOfDay.Afternoon, PartsOfDay.Of(instant, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Codes_round_trip_and_labels_name_the_hours()
    {
        foreach (var part in PartsOfDay.All)
        {
            Assert.Equal(part, PartsOfDay.Parse(PartsOfDay.Code(part)));
        }

        Assert.Null(PartsOfDay.Parse("noon"));
        Assert.Null(PartsOfDay.Parse(null));
        Assert.Equal("вечером (18:00–24:00)", PartsOfDay.Label(PartOfDay.Evening));
        Assert.Equal("ночью (00:00–06:00)", PartsOfDay.Label(PartOfDay.Night));
    }

    [Fact]
    public void A_rule_beats_the_activity_category_only_in_its_part_of_day()
    {
        var user = new User(1, "Тест", "Europe/Moscow", Msk(9));
        var youtube = new Activity(user.Id, "YouTube", DefaultCategories.WastedYouTube);
        var other = new Activity(user.Id, "Чтение", DefaultCategories.MindfulRest);
        var rules = new CategoryRules([new CategoryRule(youtube.Id, PartOfDay.Evening, DefaultCategories.MindfulRest)]);

        Assert.Equal(DefaultCategories.MindfulRest, rules.Resolve(youtube, Msk(18), Moscow, user.Categories));
        Assert.Equal(DefaultCategories.MindfulRest, rules.Resolve(youtube, Msk(23, 59), Moscow, user.Categories));
        Assert.Equal(DefaultCategories.WastedYouTube, rules.Resolve(youtube, Msk(17, 59), Moscow, user.Categories));
        Assert.Equal(DefaultCategories.WastedYouTube, rules.Resolve(youtube, Msk(0), Moscow, user.Categories));

        // Чужое занятие правило не задевает; без правил — просто категория занятия (в том числе «неизвестна»).
        Assert.Equal(DefaultCategories.MindfulRest, rules.Resolve(other, Msk(19), Moscow, user.Categories));
        Assert.Null(CategoryRules.None.Resolve(new Activity(user.Id, "Гитара"), Msk(19), Moscow, user.Categories));
        Assert.Null(rules.Match(youtube.Id, Msk(12), Moscow, user.Categories));
    }

    [Fact]
    public void A_rule_for_a_category_that_is_no_longer_offered_is_ignored()
    {
        var now = Msk(12);
        var user = new User(1, "Тест", "Europe/Moscow", Msk(9));
        var music = user.AddCategory("Музыка", CategoryGroup.Rest, null, now);
        var guitar = new Activity(user.Id, "Гитара", DefaultCategories.Projects);
        var rules = new CategoryRules([new CategoryRule(guitar.Id, PartOfDay.Evening, music.Key)]);
        Assert.Equal(music.Key, rules.Resolve(guitar, Msk(20), Moscow, user.Categories));

        user.RemoveCategory(music.Key, now);

        Assert.Equal(DefaultCategories.Projects, rules.Resolve(guitar, Msk(20), Moscow, user.Categories));
    }

    [Fact]
    public void One_rule_per_activity_and_part_of_day_the_later_one_wins()
    {
        var id = Guid.NewGuid();
        var rules = new CategoryRules(
        [
            new CategoryRule(id, PartOfDay.Night, DefaultCategories.Wasted),
            new CategoryRule(id, PartOfDay.Evening, DefaultCategories.Study),
            new CategoryRule(id, PartOfDay.Evening, DefaultCategories.MindfulRest),
        ]);

        Assert.Equal(
            [(PartOfDay.Evening, DefaultCategories.MindfulRest), (PartOfDay.Night, DefaultCategories.Wasted)],
            rules.Of(id).Select(r => (r.Part, r.CategoryKey)));
        Assert.Empty(rules.Of(Guid.NewGuid()));
    }

    [Fact]
    public void A_rule_is_stored_as_a_user_fact_without_minutes()
    {
        var userId = Guid.NewGuid();
        var rule = new CategoryRule(Guid.NewGuid(), PartOfDay.Evening, DefaultCategories.MindfulRest);

        var fact = new UserFact(userId, rule);

        Assert.Equal($"rule:{rule.ActivityId:N}:evening", fact.Key);
        Assert.Equal(UserFact.RuleKey(rule.ActivityId, PartOfDay.Evening), fact.Key);
        Assert.Equal(DefaultCategories.MindfulRest, fact.Value);
        Assert.Null(fact.Minutes);
        Assert.Equal(rule, fact.AsRule());
        Assert.Null(UserFact.ParseRouteKey(fact.Key));

        // Факт-длительность правилом не является, и наоборот.
        var route = new UserFact(userId, UserFact.RouteKey("Зал", "Дом"), 25);
        Assert.Null(route.AsRule());
        Assert.Null(route.Value);
        Assert.Equal(25, route.Minutes);
        Assert.Throws<ArgumentOutOfRangeException>(() => route.Minutes = 0);
    }
}
