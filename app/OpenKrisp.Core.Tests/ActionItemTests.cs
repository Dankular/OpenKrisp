using OpenKrisp.Core.Notes;

namespace OpenKrisp.Core.Tests;

public sealed class TempFile : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("openkrisp-test-").FullName;
    public string Path => System.IO.Path.Combine(Dir, "items.json");
    public void Dispose() => Directory.Delete(Dir, recursive: true);
}

public class ActionItemStoreTests
{
    [Fact]
    public void Items_survive_a_reload()
    {
        using var f = new TempFile();
        var a = new ActionItemStore(f.Path);
        var m = a.AddMeeting("Product Strategy");
        var i = a.AddItem(m.Id, "Review project deliverables", ActionItem.Me, new DateOnly(2026, 1, 24));
        a.SetDone(i.Id, true);

        var b = new ActionItemStore(f.Path);
        var item = Assert.Single(b.Items);
        Assert.Equal("Review project deliverables", item.Text);
        Assert.True(item.Done);
        Assert.Equal(new DateOnly(2026, 1, 24), item.Due);
        Assert.Equal("Product Strategy", Assert.Single(b.Meetings).Title);
    }

    [Fact]
    public void Rejects_blank_text_and_unknown_meetings()
    {
        using var f = new TempFile();
        var s = new ActionItemStore(f.Path);
        var m = s.AddMeeting("x");
        Assert.Throws<ArgumentException>(() => s.AddItem(m.Id, "   "));
        Assert.Throws<ArgumentException>(() => s.AddItem(Guid.NewGuid(), "orphan"));
        Assert.Throws<ArgumentException>(() => s.AddMeeting(""));
        Assert.Empty(s.Items);
    }

    [Fact]
    public void A_corrupt_file_is_moved_aside_not_deleted()
    {
        using var f = new TempFile();
        File.WriteAllText(f.Path, "{ not json");
        var s = new ActionItemStore(f.Path);
        Assert.Empty(s.Items);
        Assert.Contains(Directory.GetFiles(f.Dir), p => p.Contains(".corrupt-"));
        s.AddMeeting("fresh start"); // still usable
    }

    [Fact]
    public void Remove_and_unknown_ids()
    {
        using var f = new TempFile();
        var s = new ActionItemStore(f.Path);
        var m = s.AddMeeting("m");
        var i = s.AddItem(m.Id, "a");
        Assert.False(s.SetDone(Guid.NewGuid(), true));
        Assert.True(s.Remove(i.Id));
        Assert.False(s.Remove(i.Id));
    }
}

public class ActionItemsViewModelTests
{
    private static (ActionItemsViewModel vm, ActionItemStore store, TempFile f) Make(IActionItemExtractor? ex = null)
    {
        var f = new TempFile();
        var store = new ActionItemStore(f.Path);
        return (new ActionItemsViewModel(store, ex, () => new DateOnly(2026, 1, 25)), store, f);
    }

    [Fact]
    public void Assigned_to_me_hides_other_peoples_items_and_empty_meetings_all_shows_everything()
    {
        var (vm, s, f) = Make();
        using var _ = f;
        var ps = s.AddMeeting("Product Strategy", new DateTimeOffset(2026, 1, 20, 10, 0, 0, TimeSpan.Zero));
        var br = s.AddMeeting("Brainstorming", new DateTimeOffset(2026, 1, 21, 10, 0, 0, TimeSpan.Zero));
        s.AddMeeting("Empty meeting", new DateTimeOffset(2026, 1, 22, 10, 0, 0, TimeSpan.Zero));
        s.AddItem(ps.Id, "mine", ActionItem.Me);
        s.AddItem(ps.Id, "Jane's", "Jane");
        s.AddItem(br.Id, "unassigned");

        Assert.Equal(ActionItemFilter.AssignedToMe, vm.Filter);
        var g = Assert.Single(vm.Groups);
        Assert.Equal("Product Strategy", g.Title);
        Assert.Equal("mine", Assert.Single(g.Items).Text);

        vm.Filter = ActionItemFilter.All;
        Assert.Equal(new[] { "Empty meeting", "Brainstorming", "Product Strategy" }, vm.Groups.Select(x => x.Title)); // newest first
        Assert.Equal(2, vm.Groups.Single(x => x.Title == "Product Strategy").Items.Count);
    }

    [Fact]
    public void Checking_an_item_updates_the_store_and_the_group_counts()
    {
        var (vm, s, f) = Make();
        using var _ = f;
        var m = s.AddMeeting("m");
        s.AddItem(m.Id, "a", ActionItem.Me);
        s.AddItem(m.Id, "b", ActionItem.Me);

        vm.Groups[0].Items[0].Done = true;

        Assert.Equal("1 of 2 done", vm.Groups[0].CountText);
        Assert.Single(s.Items, i => i.Done);
    }

    [Fact]
    public void Item_text_for_time_due_date_assignee_and_overdue()
    {
        var (vm, s, f) = Make();
        using var _ = f;
        var m = s.AddMeeting("m");
        var due = s.AddItem(m.Id, "late", ActionItem.Me, new DateOnly(2026, 1, 24));
        s.AddItem(m.Id, "no date", ActionItem.Me);
        var rows = vm.Groups[0].Items;
        var late = rows.Single(r => r.Text == "late");
        var nodate = rows.Single(r => r.Text == "no date");

        Assert.Equal("Due Jan 24", late.DueText);
        Assert.True(late.IsOverdue);
        Assert.Equal("Due date", nodate.DueText);
        Assert.False(nodate.IsOverdue);
        Assert.Equal("Me", late.AssigneeText);
        Assert.Matches(@"^\d\d:\d\d$", late.TimeText);

        late.Done = true;
        Assert.False(vm.Groups[0].Items.Single(r => r.Text == "late").IsOverdue); // done items are not overdue
    }

    [Fact]
    public void New_items_follow_the_current_filter()
    {
        var (vm, s, f) = Make();
        using var _ = f;
        var m = vm.AddMeeting("m");

        vm.AddItem(m.Id, "typed on mine");
        vm.Filter = ActionItemFilter.All;
        vm.AddItem(m.Id, "typed on all");

        Assert.Equal(ActionItem.Me, s.Items.Single(i => i.Text == "typed on mine").Assignee);
        Assert.Equal("", s.Items.Single(i => i.Text == "typed on all").Assignee);
    }

    [Fact]
    public async Task Extraction_is_unavailable_by_default_and_says_why()
    {
        var (vm, s, f) = Make();
        using var _ = f;
        var m = s.AddMeeting("m");
        Assert.False(vm.ExtractionAvailable);
        Assert.Contains("local language model", vm.ExtractionStatus);
        await Assert.ThrowsAsync<NotSupportedException>(() => vm.ExtractAsync(m.Id, "transcript"));
    }

    private sealed class FakeExtractor : IActionItemExtractor
    {
        public bool IsAvailable => true;
        public string UnavailableReason => "";
        public Task<IReadOnlyList<ExtractedActionItem>> ExtractAsync(string t, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ExtractedActionItem>>(new[]
            {
                new ExtractedActionItem("Send the deck", "Jane", new DateOnly(2026, 2, 1)),
                new ExtractedActionItem("Book the room", null, null),
            });
    }

    [Fact]
    public async Task An_available_extractor_adds_items_to_the_meeting()
    {
        var (vm, s, f) = Make(new FakeExtractor());
        using var _ = f;
        var m = s.AddMeeting("m");
        Assert.Equal(2, await vm.ExtractAsync(m.Id, "..."));
        Assert.Equal(new[] { "Send the deck", "Book the room" }, s.Items.Select(i => i.Text));
        Assert.Equal("Jane", s.Items[0].Assignee);
    }
}
