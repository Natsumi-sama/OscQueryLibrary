using System.Text.Json;
using System.Text.Json.Serialization;

namespace OscQueryLibrary.Models;

/// <summary>
/// A node of an OSCQuery address space, see https://github.com/Vidvox/OSCQueryProposal
/// </summary>
public sealed class OscQueryNode
{
    [JsonPropertyName("DESCRIPTION")] public string? Description { get; set; }

    [JsonPropertyName("FULL_PATH")] public string FullPath { get; set; } = "/";

    [JsonPropertyName("ACCESS")] public AccessValues? Access { get; set; }

    [JsonPropertyName("CONTENTS")] public Dictionary<string, OscQueryNode>? Contents { get; set; }

    /// <summary>
    /// OSC type tag string, e.g. <c>f</c>, <c>i</c>, <c>T</c> or <c>ff</c> for multiple arguments.
    /// </summary>
    [JsonPropertyName("TYPE")] public string? Type { get; set; }

    /// <summary>
    /// One entry per OSC argument.
    /// </summary>
    [JsonPropertyName("VALUE")] public JsonElement[]? Value { get; set; }

    [JsonPropertyName("RANGE")] public JsonElement? Range { get; set; }

    [JsonPropertyName("CLIPMODE")] public JsonElement? ClipMode { get; set; }

    [JsonPropertyName("UNIT")] public JsonElement? Unit { get; set; }

    [JsonPropertyName("CRITICAL")] public bool? Critical { get; set; }

    [JsonPropertyName("TAGS")] public string[]? Tags { get; set; }

    [JsonPropertyName("EXTENDED_TYPE")] public JsonElement? ExtendedType { get; set; }

    /// <summary>
    /// Attributes not covered above, e.g. vendor extensions.
    /// </summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>
    /// Finds a node by path relative to this one. Called on the root node, this takes a full OSC path.
    /// </summary>
    public OscQueryNode? Find(string path)
    {
        OscQueryNode? node = this;
        foreach (var segment in path.Split(['/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.Contents == null || !node.Contents.TryGetValue(segment, out node)) return null;
        }

        return node;
    }

    /// <summary>
    /// All nodes below this one, depth first.
    /// </summary>
    public IEnumerable<OscQueryNode> Descendants()
    {
        if (Contents == null) yield break;
        foreach (var child in Contents.Values)
        {
            yield return child;
            foreach (var descendant in child.Descendants())
                yield return descendant;
        }
    }
}
