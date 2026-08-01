using System.Globalization;
using System.Text;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers;

internal static class ProviderPaging
{
    public static Result<StoragePage> Create(IEnumerable<StorageItem> source, StorageListOptions options)
    {
        var items = source.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
        var decoded = Decode(options.ContinuationToken);
        if (decoded.IsFailure)
            return Result<StoragePage>.Failure(decoded.Error!);
        var offset = decoded.Value;
        if (offset > items.Length)
            return Result<StoragePage>.Failure(StorageErrors.InvalidPath("The continuation token is outside the listing."));
        var page = items.Skip(offset).Take(options.PageSize).ToArray();
        var next = offset + page.Length;
        return Result<StoragePage>.Success(new StoragePage(page, next < items.Length ? Encode(next) : null));
    }

    private static string Encode(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private static Result<int> Decode(string? token)
    {
        if (token is null) return Result<int>.Success(0);
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0
                ? Result<int>.Success(offset)
                : Result<int>.Failure(StorageErrors.InvalidPath("The continuation token is invalid."));
        }
        catch (FormatException)
        {
            return Result<int>.Failure(StorageErrors.InvalidPath("The continuation token is invalid."));
        }
    }
}
