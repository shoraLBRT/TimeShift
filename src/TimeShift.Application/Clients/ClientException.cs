namespace TimeShift.Application.Clients;

/// <summary>Вид ошибки запроса клиента — адаптер переводит его в свой код ответа.</summary>
public enum ClientErrorKind
{
    /// <summary>Значение вне допустимого диапазона или неверного вида; <see cref="ClientException.Field"/> называет поле.</summary>
    Validation,

    /// <summary>Нет такой записи, блока или занятия.</summary>
    NotFound,

    /// <summary>Конфликт с текущим состоянием: пересечение блоков, нечего останавливать, онбординг не закончен.</summary>
    Conflict,
}

/// <summary>Ожидаемая ошибка запроса клиента. Текст — по-русски, для показа пользователю.</summary>
public sealed class ClientException : Exception
{
    public ClientException(ClientErrorKind kind, string message, string? field = null)
        : base(message)
    {
        Kind = kind;
        Field = field;
    }

    public ClientErrorKind Kind { get; }

    /// <summary>Имя поля запроса (camelCase) для <see cref="ClientErrorKind.Validation"/>.</summary>
    public string? Field { get; }

    public static ClientException Invalid(string field, string message) => new(ClientErrorKind.Validation, message, field);

    public static ClientException NotFound(string message) => new(ClientErrorKind.NotFound, message);

    public static ClientException Conflict(string message) => new(ClientErrorKind.Conflict, message);
}

/// <summary>Необязательное поле частичного обновления: «не передано» отличается от явного <c>null</c>.</summary>
public readonly record struct Optional<T>(bool IsSet, T? Value)
{
    public static Optional<T> Unset => default;

    public static Optional<T> Of(T? value) => new(true, value);
}
