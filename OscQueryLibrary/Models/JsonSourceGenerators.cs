using System.Text.Json.Serialization;

namespace OscQueryLibrary.Models;

[JsonSerializable(typeof(OscQueryNode))]
[JsonSerializable(typeof(OscQueryHostInfo))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default, WriteIndented = true, AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class ModelsSourceGenerationContext : JsonSerializerContext;
