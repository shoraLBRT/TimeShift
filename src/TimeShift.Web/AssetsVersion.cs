using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace TimeShift.Web;

/// <summary>
/// Короткий хеш (8 hex) по файлам <c>wwwroot</c>: путь, размер, время записи. Клиент по нему узнаёт, что пора обновить кеш.
/// Папки может не быть — тогда хеш пустого набора. В разработке пересчитывается не чаще раза в пару секунд, в продакшене — один раз.
/// </summary>
public sealed class AssetsVersion(IWebHostEnvironment environment)
{
    private static readonly TimeSpan DevelopmentTtl = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();

    private string? _value;

    private long _computedAt;

    public string Current
    {
        get
        {
            lock (_gate)
            {
                var now = Environment.TickCount64;
                if (_value is null || (environment.IsDevelopment() && now - _computedAt >= DevelopmentTtl.TotalMilliseconds))
                {
                    _value = Compute(environment.WebRootFileProvider);
                    _computedAt = now;
                }

                return _value;
            }
        }
    }

    private static string Compute(IFileProvider files)
    {
        var entries = new List<string>();
        Collect(files, string.Empty, entries);
        entries.Sort(StringComparer.Ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries)));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    private static void Collect(IFileProvider files, string path, List<string> entries)
    {
        var directory = files.GetDirectoryContents(path);
        if (!directory.Exists)
        {
            return;
        }

        foreach (var item in directory)
        {
            var relative = path.Length == 0 ? item.Name : $"{path}/{item.Name}";
            if (item.IsDirectory)
            {
                Collect(files, relative, entries);
            }
            else
            {
                entries.Add(string.Create(CultureInfo.InvariantCulture, $"{relative}|{item.Length}|{item.LastModified.UtcTicks}"));
            }
        }
    }
}
