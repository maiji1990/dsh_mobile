using System.Text.Json;
using DshMobile.Agent.Protocol;
using DshMobile.Agent.Rendering;

namespace DshMobile.Agent.Tests.Smoke;

public sealed class AgentSkeletonTests
{
    [Fact]
    public void MarkdownParser_ParsesHeadingsFencedCodeTablesAndLists()
    {
        const string sample = """
            # Title

            Intro paragraph with **bold** and `inline`.

            ```csharp
            Console.WriteLine("hello");
            ```

            | ColA | ColB |
            | --- | --- |
            | 1 | 2 |

            - item 1
            - [x] done
            """;

        var summary = MarkdownParser.InspectSummary(sample);
        Assert.Equal(1, summary.Headings);
        Assert.Equal(1, summary.Paragraphs);
        Assert.Equal(1, summary.CodeBlocks);
        Assert.Equal(1, summary.Tables);
        Assert.Equal(1, summary.Lists);
    }

    [Fact]
    public void AgentProtocolJsonContext_RoundtripsSourceGeneratedDto()
    {
        var dto = new ProtocolHandshakeProbe("client-request", "rpc-1", "session/list");
        var json = JsonSerializer.Serialize(dto, AgentProtocolJsonContext.Default.ProtocolHandshakeProbe);
        var roundtrip = JsonSerializer.Deserialize(json, AgentProtocolJsonContext.Default.ProtocolHandshakeProbe);

        Assert.Equal(dto, roundtrip);
        Assert.Contains("\"rpcId\":\"rpc-1\"", json, StringComparison.Ordinal);
    }
}
