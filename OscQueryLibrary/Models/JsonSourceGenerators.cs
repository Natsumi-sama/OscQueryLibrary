using System.Text.Json.Serialization;

namespace OscQueryLibrary.Models;

[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(HostInfo.ExtensionsNode))]
[JsonSerializable(typeof(HostInfo.OscTransportType))]
[JsonSerializable(typeof(RootNode))]
[JsonSerializable(typeof(AvatarContents))]
[JsonSerializable(typeof(OscParameterNode))]
[JsonSerializable(typeof(OscParameterNodeEnd<string>))] // We need to specify generic type <T> here, otherwise we get a warning about it ignoring this 
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default, WriteIndented = true, AllowTrailingCommas = true)]
internal partial class ModelsSourceGenerationContext : JsonSerializerContext;