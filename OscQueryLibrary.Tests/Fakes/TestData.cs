using System.Net;

namespace OscQueryLibrary.Tests.Fakes;

internal static class TestData
{
    /// <summary>
    /// Shaped like the address space VRChat serves.
    /// </summary>
    public const string VrcRootJson = """
        {
          "DESCRIPTION": "root node",
          "FULL_PATH": "/",
          "ACCESS": 0,
          "CONTENTS": {
            "avatar": {
              "FULL_PATH": "/avatar",
              "ACCESS": 0,
              "CONTENTS": {
                "change": { "FULL_PATH": "/avatar/change", "ACCESS": 3, "TYPE": "s", "VALUE": ["avtr_123"] },
                "parameters": {
                  "FULL_PATH": "/avatar/parameters",
                  "ACCESS": 0,
                  "CONTENTS": {
                    "VRCEmote": { "FULL_PATH": "/avatar/parameters/VRCEmote", "ACCESS": 3, "TYPE": "i", "VALUE": [4] },
                    "Nested": {
                      "FULL_PATH": "/avatar/parameters/Nested",
                      "ACCESS": 0,
                      "CONTENTS": {
                        "Float": { "FULL_PATH": "/avatar/parameters/Nested/Float", "ACCESS": 3, "TYPE": "f", "VALUE": [0.5] }
                      }
                    },
                    "NoValue": { "FULL_PATH": "/avatar/parameters/NoValue", "ACCESS": 1, "TYPE": "T" }
                  }
                }
              }
            }
          }
        }
        """;

    public static string HostInfoJson(string name, IPAddress oscIp, ushort oscPort) =>
        $$"""{"NAME":"{{name}}","OSC_IP":"{{oscIp}}","OSC_PORT":{{oscPort}},"OSC_TRANSPORT":"UDP"}""";
}
