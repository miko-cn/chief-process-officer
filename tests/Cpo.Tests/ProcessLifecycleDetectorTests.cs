using Cpo.Core.Sampling;
using Xunit;

namespace Cpo.Tests;

public class ProcessLifecycleDetectorTests
{
    private static Dictionary<int, ProcessIdentity> Dict(params (int Pid, int Ppid, string Name)[] items) =>
        items.ToDictionary(
            i => i.Pid,
            i => new ProcessIdentity(i.Pid, i.Ppid, i.Name));

    [Fact]
    public void Diff_SameSnapshot_NoChanges()
    {
        var a = Dict((1, 0, "a.exe"), (2, 1, "b.exe"));
        var diff = ProcessLifecycleDetector.Diff(a, a);
        Assert.Empty(diff.Started);
        Assert.Empty(diff.Exited);
    }

    [Fact]
    public void Diff_NewProcess_ReportedStarted()
    {
        var prev = Dict((1, 0, "a.exe"));
        var curr = Dict((1, 0, "a.exe"), (2, 1, "b.exe"));

        var diff = ProcessLifecycleDetector.Diff(prev, curr);

        var started = Assert.Single(diff.Started);
        Assert.Equal(2, started.Pid);
        Assert.Equal(1, started.ParentPid);
        Assert.Equal("b.exe", started.Name);
        Assert.Empty(diff.Exited);
    }

    [Fact]
    public void Diff_ExitedProcess_ReportedExited()
    {
        var prev = Dict((1, 0, "a.exe"), (2, 1, "b.exe"));
        var curr = Dict((1, 0, "a.exe"));

        var diff = ProcessLifecycleDetector.Diff(prev, curr);

        var exited = Assert.Single(diff.Exited);
        Assert.Equal(2, exited.Pid);
        Assert.Empty(diff.Started);
    }

    [Fact]
    public void Diff_Mixed_StartedAndExited_SortedByPid()
    {
        var prev = Dict((1, 0, "a.exe"), (2, 1, "b.exe"), (5, 0, "e.exe"));
        var curr = Dict((1, 0, "a.exe"), (3, 1, "c.exe"), (4, 3, "d.exe"));

        var diff = ProcessLifecycleDetector.Diff(prev, curr);

        Assert.Equal(new[] { 3, 4 }, diff.Started.Select(s => s.Pid));
        Assert.Equal(new[] { 2, 5 }, diff.Exited.Select(e => e.Pid));
    }

    [Fact]
    public void Diff_FirstBaseline_AllStarted()
    {
        var curr = Dict((1, 0, "a.exe"), (2, 1, "b.exe"));
        var diff = ProcessLifecycleDetector.Diff(new Dictionary<int, ProcessIdentity>(), curr);

        Assert.Equal(2, diff.Started.Count);
        Assert.Empty(diff.Exited);
    }
}
