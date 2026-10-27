using ALMARFELD.SubFlow.App.Services;

namespace ALMARFELD.SubFlow.Tests;

public class SingleInstanceProfileTests
{
    [Fact]
    public void EquivalentProfilePathsShareIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), "subflow-profile");
        var identity = SingleInstanceProfile.MutexName(path);
        Assert.Equal(identity, SingleInstanceProfile.MutexName(path + Path.DirectorySeparatorChar));
        Assert.Equal(identity, SingleInstanceProfile.MutexName(path.ToUpperInvariant()));
        Assert.Equal(identity, SingleInstanceProfile.MutexName(Path.Combine(path, "nested", "..")));
        Assert.DoesNotContain(path, identity, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DifferentProfilesNeverActivateEachOther()
    {
        Assert.NotEqual(SingleInstanceProfile.MutexName("profile-a"), SingleInstanceProfile.MutexName("profile-b"));
        Assert.NotEqual(SingleInstanceProfile.MutexName(null), SingleInstanceProfile.MutexName("profile-a"));
        Assert.Equal(@"Local\ALMARFELD.SubFlow.SingleInstance", SingleInstanceProfile.MutexName(null));
    }

    [Fact]
    public void DataOptionNormalizesItsPathAndLeavesDefaultUntouched()
    {
        Assert.Null(SingleInstanceProfile.DataDirectory([]));
        Assert.Equal(Path.GetFullPath("profile"), SingleInstanceProfile.DataDirectory(["--data", "profile\\"]));
    }

    [Fact]
    public void MissingProfileValueIsRejected()
    {
        foreach (var args in new[] { new[] { "--data" }, new[] { "--data", "" }, new[] { "--data", "--screenshots" } })
            Assert.Throws<ArgumentException>(() => SingleInstanceProfile.DataDirectory(args));
    }
}
