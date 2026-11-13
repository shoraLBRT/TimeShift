namespace TimeShift.Domain;

internal static class Utc
{
    public static void Ensure(DateTime value, string paramName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Время хранится только в UTC.", paramName);
        }
    }
}
