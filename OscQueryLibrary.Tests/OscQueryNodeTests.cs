using OscQueryLibrary.Models;

namespace OscQueryLibrary.Tests;

public class OscQueryNodeTests
{
    private static OscQueryNode CreateTree() => new()
    {
        FullPath = "/",
        Contents = new Dictionary<string, OscQueryNode>
        {
            ["avatar"] = new()
            {
                FullPath = "/avatar",
                Contents = new Dictionary<string, OscQueryNode>
                {
                    ["change"] = new() { FullPath = "/avatar/change" },
                    ["parameters"] = new()
                    {
                        FullPath = "/avatar/parameters",
                        Contents = new Dictionary<string, OscQueryNode> { ["A"] = new() { FullPath = "/avatar/parameters/A" } }
                    }
                }
            },
            ["input"] = new() { FullPath = "/input" }
        }
    };

    [Test]
    [Arguments("/")]
    [Arguments("")]
    public async Task Find_RootPath_ReturnsSelf(string path)
    {
        var root = CreateTree();

        await Assert.That(root.Find(path)).IsSameReferenceAs(root);
    }

    [Test]
    [Arguments("/avatar/change")]
    [Arguments("avatar/change")]
    [Arguments("/avatar//change/")]
    public async Task Find_NestedPath_ReturnsNode(string path)
    {
        await Assert.That(CreateTree().Find(path)?.FullPath).IsEqualTo("/avatar/change");
    }

    [Test]
    [Arguments("/missing")]
    [Arguments("/avatar/missing")]
    [Arguments("/avatar/change/below-leaf")]
    [Arguments("/Avatar/change")]
    public async Task Find_UnknownPath_ReturnsNull(string path)
    {
        await Assert.That(CreateTree().Find(path)).IsNull();
    }

    [Test]
    public async Task Find_IsRelativeToNode()
    {
        var avatar = CreateTree().Find("/avatar")!;

        await Assert.That(avatar.Find("parameters/A")?.FullPath).IsEqualTo("/avatar/parameters/A");
    }

    [Test]
    public async Task Descendants_AreDepthFirst()
    {
        var paths = CreateTree().Descendants().Select(n => n.FullPath).ToArray();

        await Assert.That(paths).IsEquivalentTo(
            ["/avatar", "/avatar/change", "/avatar/parameters", "/avatar/parameters/A", "/input"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Descendants_OfLeaf_IsEmpty()
    {
        await Assert.That(new OscQueryNode().Descendants()).IsEmpty();
    }
}
