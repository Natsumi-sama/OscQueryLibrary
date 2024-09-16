using System.Text.Json.Serialization;

namespace OscQueryLibrary.Models;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(RootNode))]
[JsonSerializable(typeof(AvatarContents))]
[JsonSerializable(typeof(OscParameterNode))]
[JsonSerializable(typeof(OscParameterNodeEnd<string>))]
internal partial class JsonSourceGenerationContext : JsonSerializerContext
{
}