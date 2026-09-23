using System.Reflection;
using CL.Storage;
using CL.Storage.Queue;
using Xunit;

namespace Storage.Tests;

/// <summary>Stored and serialized enum numbers must not move between releases.</summary>
public sealed class NeedsReviewEnumTests
{
    // needs-review A14, D1: 4.8.93 stored Completed=2, Failed=3, Cancelled=4; new states go at the end.
    [Fact]
    public void Transfer_states_keep_the_numbers_4_8_93_stored()
    {
        Assert.Equal(0, (int)StorageTransferState.Queued);
        Assert.Equal(1, (int)StorageTransferState.Running);
        Assert.Equal(2, (int)StorageTransferState.Completed);
        Assert.Equal(3, (int)StorageTransferState.Failed);
        Assert.Equal(4, (int)StorageTransferState.Cancelled);
        Assert.True((int)StorageTransferState.Paused > 4);
        Assert.True((int)StorageTransferState.Blocked > 4);
        Assert.True((int)StorageTransferState.NeedsReconciliation > 4);
        Assert.True((int)StorageTransferState.Interrupted > 4);
    }

    // needs-review A14: every public enum spells out its numbers, so reordering members cannot renumber them.
    [Fact]
    public void Every_public_enum_has_distinct_values()
    {
        var enums = typeof(StorageLibrary).Assembly.GetExportedTypes().Where(t => t.IsEnum).ToList();
        Assert.NotEmpty(enums);
        foreach (var type in enums.Where(t => t.GetCustomAttribute<FlagsAttribute>() is null))
        {
            var values = Enum.GetValues(type).Cast<object>().Select(Convert.ToInt64).ToList();
            Assert.True(values.Count == values.Distinct().Count(), $"{type.Name} has duplicate values");
        }
    }
}
