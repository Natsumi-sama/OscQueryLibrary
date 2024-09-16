using System.Text.Json.Serialization;

namespace OscQueryLibrary.Models;

[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(HostInfo.ExtensionsNode))]
[JsonSerializable(typeof(HostInfo.OscTransportType))]
[JsonSerializable(typeof(RootNode))]
[JsonSerializable(typeof(AvatarContents))]
[JsonSerializable(typeof(OscParameterNode))]
[JsonSerializable(typeof(OscParameterNodeEnd<>))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default, WriteIndented = true, AllowTrailingCommas = true)]
internal partial class ModelsSourceGenerationContext : JsonSerializerContext;