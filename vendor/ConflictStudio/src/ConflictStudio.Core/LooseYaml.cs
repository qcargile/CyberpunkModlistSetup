using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace ConflictStudio.Core;

internal abstract record LooseYamlNode(string Tag, int Line)
{
    public bool IsAlias { get; init; }
}

internal sealed record LooseYamlScalar(string Value, string ScalarTag, int ScalarLine) : LooseYamlNode(ScalarTag, ScalarLine);

internal sealed record LooseYamlSequence(LooseYamlNode[] Children, string SequenceTag, int SequenceLine) : LooseYamlNode(SequenceTag, SequenceLine);

internal sealed record LooseYamlMapping((LooseYamlNode Key, LooseYamlNode Value)[] Children, string MappingTag, int MappingLine) : LooseYamlNode(MappingTag, MappingLine);

internal static class LooseYaml
{
    public static LooseYamlNode Parse(string text) => ParseAll(text).Single();

    public static LooseYamlNode[] ParseAll(string text)
    {
        Parser parser = new(new StringReader(text));
        List<LooseYamlNode> documents = [];
        while (parser.MoveNext())
        {
            if (parser.Current is StreamEnd) break;
            if (parser.Current is not DocumentStart) continue;
            if (!parser.MoveNext()) throw new YamlException("The YAML document is empty.");
            Dictionary<string, LooseYamlNode> anchors = new(StringComparer.Ordinal);
            documents.Add(Read(parser, anchors));
            if (parser.Current is not DocumentEnd) throw new YamlException("The YAML document did not end cleanly.");
        }
        if (documents.Count == 0) throw new YamlException("The YAML stream contains no documents.");
        return documents.ToArray();
    }

    private static LooseYamlNode Read(Parser parser, Dictionary<string, LooseYamlNode> anchors)
    {
        if (parser.Current is Scalar scalar)
        {
            LooseYamlNode node = new LooseYamlScalar(scalar.Value, Tag(scalar), checked((int)scalar.Start.Line));
            Register(scalar, node, anchors);
            parser.MoveNext();
            return node;
        }
        if (parser.Current is AnchorAlias alias)
        {
            if (!anchors.TryGetValue(alias.Value.Value, out LooseYamlNode? anchored)) throw new YamlException($"Unknown YAML anchor '{alias.Value.Value}'.");
            parser.MoveNext();
            return WithLine(anchored, checked((int)alias.Start.Line));
        }
        if (parser.Current is SequenceStart sequenceStart)
        {
            List<LooseYamlNode> children = [];
            parser.MoveNext();
            while (parser.Current is not SequenceEnd) children.Add(Read(parser, anchors));
            parser.MoveNext();
            LooseYamlNode node = new LooseYamlSequence(children.ToArray(), Tag(sequenceStart), checked((int)sequenceStart.Start.Line));
            Register(sequenceStart, node, anchors);
            return node;
        }
        if (parser.Current is MappingStart mappingStart)
        {
            List<(LooseYamlNode Key, LooseYamlNode Value)> children = [];
            parser.MoveNext();
            while (parser.Current is not MappingEnd)
            {
                LooseYamlNode key = Read(parser, anchors);
                LooseYamlNode value = Read(parser, anchors);
                children.Add((key, value));
            }
            parser.MoveNext();
            LooseYamlNode node = new LooseYamlMapping(children.ToArray(), Tag(mappingStart), checked((int)mappingStart.Start.Line));
            Register(mappingStart, node, anchors);
            return node;
        }
        throw new YamlException($"Unsupported YAML event '{parser.Current?.GetType().Name ?? "end of stream"}'.");
    }

    private static void Register(NodeEvent yaml, LooseYamlNode node, Dictionary<string, LooseYamlNode> anchors)
    {
        if (!yaml.Anchor.IsEmpty) anchors[yaml.Anchor.Value] = node;
    }

    private static string Tag(NodeEvent node) => node.Tag.IsEmpty || node.Tag.IsNonSpecific ? string.Empty : node.Tag.Value;

    private static LooseYamlNode WithLine(LooseYamlNode node, int line)
        => node switch
        {
            LooseYamlScalar scalar => new LooseYamlScalar(scalar.Value, scalar.ScalarTag, line) { IsAlias = true },
            LooseYamlSequence sequence => new LooseYamlSequence(sequence.Children, sequence.SequenceTag, line) { IsAlias = true },
            LooseYamlMapping mapping => new LooseYamlMapping(mapping.Children, mapping.MappingTag, line) { IsAlias = true },
            _ => node
        };
}
