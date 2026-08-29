using System.Text.Json;
using System.Text.RegularExpressions;
using AutoPartsHub.Api.Admin;
using AutoPartsHub.Api.Support;

namespace AutoPartsHub.Tests;

/// <summary>
/// Tickets.
/// </summary>
/// <remarks>
/// Two things are pinned down here. The status is DERIVED — a field somebody
/// has to remember to change is a field that is wrong by Wednesday — and an
/// internal note is a note the customer must never see, which is a boundary a
/// type cannot hold because the notes and the replies are the same table.
///
/// Both APIs write to the same table, so both have to answer the same way.
/// </remarks>
public class TicketTests
{
    private static Validated<NewTicket> ReadNew(string json) =>
        TicketInput.ReadNewTicket(JsonDocument.Parse(json).RootElement);

    private static NewTicket Ok(Validated<NewTicket> r)
    {
        Assert.True(r.Ok, $"expected success, got: {r.Error}");
        return r.Value!;
    }

    private static string Err(Validated<NewTicket> r)
    {
        Assert.False(r.Ok, "expected a refusal, got success");
        return r.Error!;
    }

    /* ------------------------ the status derives from what happened --- */

    [Fact]
    public void ACustomerWritingPutsItBackOnOurQueue()
    {
        Assert.Equal("open",
            TicketInput.StatusAfterMessage("answered", fromStaff: false, internalNote: false));
    }

    [Fact]
    public void OurReplyHandsItBackToThem()
    {
        Assert.Equal("answered",
            TicketInput.StatusAfterMessage("open", fromStaff: true, internalNote: false));
    }

    [Fact]
    public void ACustomerWritingToAResolvedTicketReopensIt()
    {
        // Closing a conversation is our decision to make and theirs to
        // overturn by continuing it.
        Assert.Equal("open",
            TicketInput.StatusAfterMessage("resolved", fromStaff: false, internalNote: false));
    }

    [Fact]
    public void AnInternalNoteChangesNothing()
    {
        // The case this function exists for. Staff talking to each other have
        // not answered anybody, and moving the ticket to `answered` would take
        // it off the queue of things waiting on us on the strength of a
        // conversation the customer cannot see and did not receive — which is
        // how somebody ends up waiting a week.
        foreach (var status in TicketInput.Statuses)
        {
            Assert.Equal(status,
                TicketInput.StatusAfterMessage(status, fromStaff: true, internalNote: true));
        }
    }

    [Fact]
    public void KnowsItsOwnStatusesAndNothingElse()
    {
        Assert.True(TicketInput.IsKnown("open"));
        Assert.False(TicketInput.IsKnown("closed"));
        Assert.False(TicketInput.IsKnown("OPEN"));
    }

    /* --------------------------------------- what a ticket may say --- */

    [Fact]
    public void NeedsASubjectAndSomethingToSay()
    {
        Assert.Equal("Give it a subject.", Err(ReadNew("""{"body":"the box arrived empty"}""")));
        Assert.Equal("Say what the problem is.", Err(ReadNew("""{"subject":"Wrong part"}""")));
    }

    [Fact]
    public void TrimsBoth()
    {
        var t = Ok(ReadNew("""{"subject":"  Wrong part  ","body":"  it is the wrong one \n"}"""));

        Assert.Equal("Wrong part", t.Subject);
        Assert.Equal("it is the wrong one", t.Body);
    }

    [Fact]
    public void TreatsWhitespaceAsNothingSaid()
    {
        Assert.Contains("subject", Err(ReadNew("""{"subject":"   ","body":"x"}""")));
        Assert.Contains("what the problem is", Err(ReadNew("""{"subject":"x","body":"  \n "}""")));
    }

    [Fact]
    public void RefusesAnOverLongSubjectAndAnOverLongMessage()
    {
        var longSubject = JsonSerializer.Serialize(new
        {
            subject = new string('x', TicketInput.MaxSubject + 1), body = "y",
        });
        var longBody = JsonSerializer.Serialize(new
        {
            subject = "y", body = new string('x', TicketInput.MaxBody + 1),
        });

        Assert.Contains("under 150", Err(ReadNew(longSubject)));
        // Refused rather than truncated: a message cut off halfway says
        // something its author did not.
        Assert.Contains("under 5000", Err(ReadNew(longBody)));
    }

    [Fact]
    public void TakesAnOrderIdOrNone()
    {
        Assert.Equal("ord-1", Ok(ReadNew("""{"subject":"a","body":"b","orderId":"ord-1"}""")).OrderId);
        Assert.Null(Ok(ReadNew("""{"subject":"a","body":"b"}""")).OrderId);
        Assert.Null(Ok(ReadNew("""{"subject":"a","body":"b","orderId":"  "}""")).OrderId);
    }

    [Fact]
    public void TheReferenceReadsDownATelephoneAndDatesItself()
    {
        var reference = TicketInput.MakeReference(new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc));

        Assert.Matches(new Regex("^APH-T-260827-[A-Z0-9]{4}$"), reference);
    }

    /* ------------------- the boundary a type cannot hold --- */

    // A note and a reply are rows on one table, in one thread, in one order —
    // which is what makes a note useful in the moment and dangerous forever
    // after. What follows asserts that the two readers stayed two.

    private static string EndpointCode()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AutoPartsHub.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        // Line endings normalised before anything below looks for a newline.
        // This repository is committed with LF and checked out with CRLF on
        // Windows, so a scan matching "…(\n" passes on the file as written and
        // fails on the same file after a checkout — which is what happened, and
        // which says nothing whatever about internal notes.
        var source = File.ReadAllText(Path.Combine(
                dir!.FullName, "AutoPartsHub.Api", "Endpoints", "TicketEndpoints.cs"))
            .Replace("\r\n", "\n");

        // Comments stripped: the file explains at length what an internal note
        // is and why there are two readers, naming both freely. A scan that
        // could not tell the explanation from the leak would make the
        // explanation impossible to write.
        source = Regex.Replace(source, @"^\s*///.*$", "", RegexOptions.Multiline);
        source = Regex.Replace(source, @"/\*[\s\S]*?\*/", "");
        return Regex.Replace(source, @"(^|[^:])//.*$", "$1", RegexOptions.Multiline);
    }

    [Fact]
    public void KeepsTheTwoReadersTwoRatherThanOneWithAFlag()
    {
        // A single method taking `includeInternal` would be one wrong argument
        // away from the leak, and the argument would be supplied by whichever
        // endpoint was written last.
        var code = EndpointCode();

        Assert.Contains("CustomerThreadAsync", code);
        Assert.Contains("StaffThreadAsync", code);
        Assert.DoesNotContain("includeInternal", code);
    }

    [Fact]
    public void ExcludesNotesInTheQueryNotAfterIt()
    {
        var code = EndpointCode();
        var customer = code[code.IndexOf("CustomerThreadAsync(\n", StringComparison.Ordinal)..];

        Assert.Contains("NOT \"internal\"", code);
        // And the customer reader is the one carrying it.
        var reader = code[code.LastIndexOf("private static Task<List<TicketMessageRow>> CustomerThreadAsync",
            StringComparison.Ordinal)..];
        Assert.Contains("NOT \"internal\"", reader[..reader.IndexOf("StaffThreadAsync", StringComparison.Ordinal)]);
        Assert.NotEmpty(customer);
    }

    [Fact]
    public void TheCustomerSideNeverWritesAnInternalNote()
    {
        // The flag is not offered on that side at all rather than offered and
        // refused: a parameter that is always rejected is one somebody
        // eventually makes work.
        var code = EndpointCode();
        var customerPost = code[code.IndexOf("\"/api/tickets/{id}/messages\"", StringComparison.Ordinal)..];
        var body = customerPost[..customerPost.IndexOf("/api/admin/tickets", StringComparison.Ordinal)];

        Assert.Contains("internalNote: false", body);
    }
}
