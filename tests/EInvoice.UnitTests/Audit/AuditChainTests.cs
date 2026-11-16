using EInvoice.Application.Audit;

namespace EInvoice.UnitTests.Audit;

/// <summary>
/// The hash chain without a database: each event's hash covers its own fields and the previous event's hash, so a
/// change to any field of any event, a removed event or a reordering is found, and named by its sequence number.
/// </summary>
public sealed class AuditChainTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A correct chain of <paramref name="count"/> events.</summary>
    private static List<AuditEntry> Chain(int count)
    {
        var entries = new List<AuditEntry>();
        var previous = AuditHash.Genesis;
        for (var sequence = 1L; sequence <= count; sequence++)
        {
            var at = T0.AddMinutes(sequence);
            var actor = $"user{sequence}@test";
            var details = $$"""{"n":{{sequence}}}""";
            var hash = AuditHash.Compute(sequence, at, actor, AuditActions.InvoiceStatusChanged, $"Invoice/{sequence}", details, previous);
            entries.Add(new AuditEntry(sequence, at, actor, AuditActions.InvoiceStatusChanged, $"Invoice/{sequence}", details, previous, hash));
            previous = hash;
        }

        return entries;
    }

    private static async IAsyncEnumerable<AuditEntry> Stream(IEnumerable<AuditEntry> entries)
    {
        foreach (var entry in entries)
        {
            await Task.Yield();
            yield return entry;
        }
    }

    private static Task<AuditVerification> Verify(IEnumerable<AuditEntry> entries) => AuditVerifier.VerifyAsync(Stream(entries), CancellationToken.None);

    // ---- The hash ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_same_event_always_has_the_same_hash()
    {
        var a = AuditHash.Compute(1, T0, "a@test", "X", "S", "{}", AuditHash.Genesis);
        var b = AuditHash.Compute(1, T0, "a@test", "X", "S", "{}", AuditHash.Genesis);

        Assert.Equal(a, b);
        Assert.Equal(32, a.Length); // SHA-256
        Assert.Equal(new byte[32], AuditHash.Genesis);
    }

    [Fact]
    public void Every_field_is_covered_by_the_hash()
    {
        var original = AuditHash.Compute(1, T0, "a@test", "X", "S", "{}", AuditHash.Genesis);
        byte[] otherPrevious = [.. AuditHash.Genesis[..31], 1];

        Assert.All(
            new[]
            {
                AuditHash.Compute(2, T0, "a@test", "X", "S", "{}", AuditHash.Genesis),
                AuditHash.Compute(1, T0.AddTicks(1), "a@test", "X", "S", "{}", AuditHash.Genesis),
                AuditHash.Compute(1, T0, "b@test", "X", "S", "{}", AuditHash.Genesis),
                AuditHash.Compute(1, T0, "a@test", "Y", "S", "{}", AuditHash.Genesis),
                AuditHash.Compute(1, T0, "a@test", "X", "T", "{}", AuditHash.Genesis),
                AuditHash.Compute(1, T0, "a@test", "X", "S", "{ }", AuditHash.Genesis),
                AuditHash.Compute(1, T0, "a@test", "X", "S", "{}", otherPrevious),
            },
            changed => Assert.NotEqual(original, changed));
    }

    [Fact]
    public void Moving_text_from_one_field_to_the_next_changes_the_hash()
    {
        // Without unambiguous field boundaries "ab"+"c" and "a"+"bc" would hash the same.
        Assert.NotEqual(
            AuditHash.Compute(1, T0, "ab", "c", "S", "{}", AuditHash.Genesis),
            AuditHash.Compute(1, T0, "a", "bc", "S", "{}", AuditHash.Genesis));
    }

    [Fact]
    public void The_same_moment_in_another_time_zone_is_the_same_event()
    {
        var berlin = T0.ToOffset(TimeSpan.FromHours(1));

        Assert.Equal(
            AuditHash.Compute(1, T0, "a", "X", "S", "{}", AuditHash.Genesis),
            AuditHash.Compute(1, berlin, "a", "X", "S", "{}", AuditHash.Genesis));
    }

    // ---- Verification -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_intact_chain_is_reported_with_its_length_and_last_hash()
    {
        var chain = Chain(5);

        var result = await Verify(chain);

        Assert.True(result.IsIntact);
        Assert.Equal((5L, 5L), (result.Count, result.LastSequence!.Value));
        Assert.Equal(Convert.ToHexStringLower(chain[^1].Hash), result.LastHash);
        Assert.Null(result.BrokenAt);
    }

    [Fact]
    public async Task An_empty_log_is_intact()
    {
        var result = await Verify([]);

        Assert.True(result.IsIntact);
        Assert.Equal(0, result.Count);
        Assert.Null(result.LastHash);
    }

    [Fact]
    public async Task A_changed_field_is_found_at_its_event()
    {
        var chain = Chain(5);
        chain[2] = chain[2] with { Details = """{"n":999}""" }; // edited by hand, hash left as it was

        var result = await Verify(chain);

        Assert.False(result.IsIntact);
        Assert.Equal((3L, AuditProblem.HashMismatch), (result.BrokenAt!.Value, result.Problem!.Value));
    }

    [Fact]
    public async Task A_recomputed_hash_breaks_the_link_to_the_next_event()
    {
        // A careful forger also recomputes the edited event's hash: then the next event no longer points to it.
        var chain = Chain(5);
        var edited = chain[2] with { Actor = "someone-else@test" };
        chain[2] = edited with { Hash = AuditHash.Compute(edited.Sequence, edited.At, edited.Actor, edited.Action, edited.Subject, edited.Details, edited.PreviousHash) };

        var result = await Verify(chain);

        Assert.Equal((4L, AuditProblem.ChainBroken), (result.BrokenAt!.Value, result.Problem!.Value));
    }

    [Fact]
    public async Task A_removed_event_is_found_as_a_gap()
    {
        var chain = Chain(5);
        chain.RemoveAt(2);

        var result = await Verify(chain);

        Assert.Equal((4L, AuditProblem.SequenceGap), (result.BrokenAt!.Value, result.Problem!.Value));
    }

    [Fact]
    public async Task A_log_that_does_not_start_at_one_is_broken_at_its_first_event()
    {
        var result = await Verify(Chain(3).Skip(1));

        Assert.Equal((2L, AuditProblem.SequenceGap), (result.BrokenAt!.Value, result.Problem!.Value));
    }

    [Fact]
    public async Task The_first_event_must_start_from_the_genesis_hash()
    {
        var chain = Chain(2);
        byte[] notGenesis = [.. new byte[31], 7];
        var first = chain[0] with { PreviousHash = notGenesis };
        chain[0] = first with { Hash = AuditHash.Compute(1, first.At, first.Actor, first.Action, first.Subject, first.Details, notGenesis) };

        var result = await Verify(chain);

        Assert.Equal((1L, AuditProblem.ChainBroken), (result.BrokenAt!.Value, result.Problem!.Value));
    }

    [Fact]
    public async Task The_count_says_how_far_it_was_read_when_broken()
    {
        var chain = Chain(5);
        chain[3] = chain[3] with { Subject = "Invoice/x" };

        var result = await Verify(chain);

        Assert.Equal(3, result.Count); // events 1 to 3 are confirmed
    }

    [Fact]
    public async Task A_broken_chain_still_reports_the_last_good_event_and_its_hash()
    {
        // What was verified up to the break: the event before it, with its hash, to compare with a noted-down copy.
        var chain = Chain(5);
        chain[2] = chain[2] with { Details = """{"n":999}""" };

        var result = await Verify(chain);

        Assert.Equal((2L, 2L), (result.Count, result.LastSequence!.Value));
        Assert.Equal(Convert.ToHexStringLower(chain[1].Hash), result.LastHash);
    }

    [Fact]
    public async Task A_chain_broken_at_its_first_event_has_no_last_good_hash()
    {
        var chain = Chain(3);
        chain[0] = chain[0] with { Details = """{"n":999}""" };

        var result = await Verify(chain);

        Assert.Equal(0, result.Count);
        Assert.Null(result.LastHash);
    }
}
