namespace Talewright.Player.E2E.Tests;

public sealed class AccessibleNodeTests
{
    [Fact]
    public void Narrative_text_uses_parent_text_without_duplicating_descendant_names_or_dropping_prose()
    {
        var code = Node("", "code", "code");
        var paragraph = Node("", "paragraph", "Mara's bold gentle code and combined words.", code);
        Assert.Equal("Mara's bold gentle code and combined words.", paragraph.NarrativeContent);
        var expanded = Node("Current passage", "landmark", "Base Camp \uFFFC Prose.",
            Node("Base Camp", "heading", "Base Camp"), Node("Camp alt", "image", ""));
        var compact = Node("Current passage", "landmark", "Base Camp \uFFFC Prose.");
        Assert.Equal("Base Camp Prose.", expanded.NarrativeContent);
        Assert.Equal(expanded.NarrativeContent, compact.NarrativeContent);
    }

    [Fact]
    public void Empty_parent_text_uses_children_in_order_and_preserves_leaf_alternatives()
    {
        var node = Node("Current passage", "landmark", "",
            Node("", "paragraph", "Before"), Node("Useful alt", "image", ""), Node("", "paragraph", "After"));
        Assert.Equal("Before Useful alt After", node.NarrativeContent);
    }

    private static AccessibleNode Node(string name, string role, string text, params AccessibleNode[] children) =>
        new(name, role, text, false, new(0, 0, 0, 0), children);
}
