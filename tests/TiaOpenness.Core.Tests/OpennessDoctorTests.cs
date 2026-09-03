using System;
using System.Linq;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Environment;
using Xunit;

namespace TiaOpenness.Core.Tests;

/// <summary>
/// Doctor is the first thing anyone runs, and what it reports depends on the machine, so these
/// tests deliberately assert nothing about *this* machine's TIA installation. What they hold is
/// the promise the tool makes regardless of environment: every check is identifiable, and a
/// check that fails tells you how to fix it.
/// </summary>
public class OpennessDoctorTests
{
    private static DoctorReport Report() => OpennessDoctor.Run();

    [Fact]
    public void Reports_who_and_where_it_ran()
    {
        var report = Report();

        Assert.False(string.IsNullOrWhiteSpace(report.MachineName));
        Assert.False(string.IsNullOrWhiteSpace(report.UserName));
        Assert.NotEmpty(report.Checks);
    }

    [Fact]
    public void Every_check_has_an_id_and_a_title()
    {
        foreach (var check in Report().Checks)
        {
            Assert.False(string.IsNullOrWhiteSpace(check.Id));
            Assert.False(string.IsNullOrWhiteSpace(check.Title));
        }
    }

    [Fact]
    public void Check_ids_are_unique_so_a_result_can_be_referred_to()
    {
        var ids = Report().Checks.Select(c => c.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The README's claim: "Every failed check prints the exact command that fixes it".</summary>
    [Fact]
    public void A_failing_check_always_carries_a_remedy()
    {
        foreach (var check in Report().Checks.Where(c => c.Status == CheckStatus.Fail))
        {
            Assert.False(string.IsNullOrWhiteSpace(check.Remedy),
                "check '" + check.Id + "' failed without telling the operator what to do about it");
        }
    }

    [Fact]
    public void The_verdict_agrees_with_the_checks_it_is_drawn_from()
    {
        var report = Report();
        var anyFailure = report.Checks.Any(c => c.Status == CheckStatus.Fail);

        Assert.Equal(!anyFailure, report.CanRunOpenness);
    }

    [Fact]
    public void The_formatted_report_mentions_every_check()
    {
        var report = Report();
        var text = OpennessDoctor.Format(report);

        foreach (var check in report.Checks)
        {
            Assert.Contains(check.Title, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The bridge is a 64-bit process because the Openness assemblies are x64-only; the test
    /// host has to match it or the check under test would be measuring the wrong process.
    /// </summary>
    [Fact]
    public void Runs_as_a_64_bit_process()
    {
        Assert.True(System.Environment.Is64BitProcess);
    }
}
