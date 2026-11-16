using TimeShift.Application.Abstractions;
using TimeShift.Domain.Activities;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Clients;

/// <summary>Занятия пользователя для клиента: список (недавние первыми) и правка названия, категории и типичной длительности.</summary>
public sealed class ActivityService(IActivityRepository activities, IUnitOfWork unitOfWork)
{
    public const int MaxListed = 200;

    public async Task<IReadOnlyList<ActivityModel>> ListAsync(User user, CancellationToken cancellationToken)
    {
        var all = await activities.ListAsync(user.Id, cancellationToken);
        var lastUsed = await activities.LastUsedAsync(user.Id, cancellationToken);
        return all
            .Select(a => (Activity: a, Last: lastUsed.TryGetValue(a.Id, out var at) ? at : (DateTime?)null))
            .OrderByDescending(x => x.Last.HasValue)
            .ThenByDescending(x => x.Last)
            .ThenBy(x => x.Activity.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxListed)
            .Select(x => ToModel(x.Activity, x.Last))
            .ToList();
    }

    public async Task<ActivityModel> UpdateAsync(User user, Guid activityId, ActivityPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var activity = await activities.GetAsync(user.Id, activityId, cancellationToken)
            ?? throw ClientException.NotFound("Такого занятия нет.");

        string? name = null;
        if (patch.Name is { } raw)
        {
            name = raw.Trim();
            if (name.Length is 0 or > Activity.MaxNameLength)
            {
                throw ClientException.Invalid("name", $"Название — от 1 до {Activity.MaxNameLength} символов.");
            }

            if (await activities.FindByNameAsync(user.Id, name, cancellationToken) is { } same && same.Id != activity.Id)
            {
                throw ClientException.Conflict("Занятие с таким названием уже есть.");
            }
        }

        if (patch.Category is { IsSet: true, Value: { } key } && DefaultCategories.Find(key) is null)
        {
            throw ClientException.Invalid("category", "Нет такой категории.");
        }

        if (patch.TypicalMin is { IsSet: true, Value: { } minutes } && minutes is < 1 or > Activity.MaxTypicalMinutes)
        {
            throw ClientException.Invalid("typicalMin", $"Типичная длительность — от 1 до {Activity.MaxTypicalMinutes} минут.");
        }

        if (name is not null)
        {
            activity.Rename(name);
        }

        if (patch.Category.IsSet)
        {
            activity.CategoryKey = patch.Category.Value;
        }

        if (patch.TypicalMin.IsSet)
        {
            activity.TypicalDuration = patch.TypicalMin.Value is { } m ? TimeSpan.FromMinutes(m) : null;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var lastUsed = await activities.LastUsedAsync(user.Id, cancellationToken);
        return ToModel(activity, lastUsed.TryGetValue(activity.Id, out var at) ? at : null);
    }

    private static ActivityModel ToModel(Activity activity, DateTime? lastUsed) => new(
        activity.Id,
        activity.Name,
        activity.CategoryKey,
        activity.TypicalDuration is { } typical ? (int)Math.Round(typical.TotalMinutes) : null,
        lastUsed);
}
