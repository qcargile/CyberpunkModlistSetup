using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ConflictStudio.Core;

public sealed record ArchiveXlSource(string Provider, string FilePath, string Text);

public enum ArchiveXlOperationKind { MergeRegistration, FactoryRegistration, LocalizationRegistration, JournalRegistration, ResourcePatch, ResourceLink, ResourceCopy, ResourceScope, ResourceFix, StreamingMutation, QuestPhaseRegistration, CustomizationRegistration }

public sealed record ArchiveXlOperation(string Provider, string FilePath, ArchiveXlOperationKind Kind, string Target, string Payload);

public sealed record ArchiveXlAnalysisResult(ArchiveXlOperation[] Operations, ArchiveXlSourceFailure[] Failures);

public static class ArchiveXlManifestAnalyzer
{
    public static ArchiveXlOperation[] Analyze(IReadOnlyList<ArchiveXlSource> sources) => AnalyzeDetailed(sources).Operations;

    public static ArchiveXlAnalysisResult AnalyzeDetailed(IReadOnlyList<ArchiveXlSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        List<ArchiveXlOperation> operations = [];
        List<ArchiveXlSourceFailure> failures = [];
        foreach (ArchiveXlSource source in sources) AnalyzeSource(source, operations, failures);
        return new ArchiveXlAnalysisResult(operations.ToArray(), failures.ToArray());
    }

    private static void AnalyzeSource(ArchiveXlSource source, List<ArchiveXlOperation> operations, List<ArchiveXlSourceFailure> failures)
    {
        YamlStream yaml = [];
        try
        {
            yaml.Load(new StringReader(source.Text));
        }
        catch (YamlException exception)
        {
            if (AnalyzeRepeatedResources(source, operations, failures)) return;
            Fail(source, failures, $"ArchiveXL source could not be represented completely: {exception.Message}", ArchiveXlFailureKind.Malformed);
            return;
        }
        if (yaml.Documents.Count == 0) return;
        if (yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            Fail(source, failures, "ArchiveXL manifest root must be a mapping.", ArchiveXlFailureKind.Malformed);
            return;
        }

        AnalyzeRoot(source, root, operations, failures, true);
    }

    private static void AnalyzeRoot(ArchiveXlSource source, YamlMappingNode root, List<ArchiveXlOperation> operations, List<ArchiveXlSourceFailure> failures, bool includeResource)
    {
        foreach ((YamlNode keyNode, YamlNode value) in root.Children)
        {
            string key = Scalar(keyNode);
            if (key == "factories") AddSequence(source, value, ArchiveXlOperationKind.FactoryRegistration, operations, failures, key);
            else if (key == "journal") AddSequence(source, value, ArchiveXlOperationKind.JournalRegistration, operations, failures, key);
            else if (key == "merge") Fail(source, failures, "Unsupported ArchiveXL root operation 'merge'.", ArchiveXlFailureKind.Coverage);
            else if (key == "localization") AddLeafScalars(source, value, ArchiveXlOperationKind.LocalizationRegistration, operations, failures, key);
            else if (key == "resource" && includeResource) AddResourceOperations(source, value, operations, failures);
            else if (key == "streaming")
            {
                AddPathOperations(source, value, "blocks", ArchiveXlOperationKind.StreamingMutation, operations, failures);
                AddPathOperations(source, value, "sectors", ArchiveXlOperationKind.StreamingMutation, operations, failures);
            }
            else if (key == "quest") AddPathOperations(source, value, "phases", ArchiveXlOperationKind.QuestPhaseRegistration, operations, failures);
            else if (key == "customizations") AddLeafScalars(source, value, ArchiveXlOperationKind.CustomizationRegistration, operations, failures, key);
        }
    }

    private static void AddResourceOperations(ArchiveXlSource source, YamlNode node, List<ArchiveXlOperation> operations, List<ArchiveXlSourceFailure> failures)
    {
        if (node is not YamlMappingNode resource)
        {
            Fail(source, failures, "ArchiveXL resource section must be a mapping.", ArchiveXlFailureKind.Malformed);
            return;
        }
        foreach ((YamlNode keyNode, YamlNode value) in resource.Children)
        {
            string name = Scalar(keyNode);
            ArchiveXlOperationKind? kind = name switch
            {
                "patch" => ArchiveXlOperationKind.ResourcePatch,
                "link" => ArchiveXlOperationKind.ResourceLink,
                "copy" => ArchiveXlOperationKind.ResourceCopy,
                "scope" => ArchiveXlOperationKind.ResourceScope,
                "fix" => ArchiveXlOperationKind.ResourceFix,
                _ => null
            };
            if (kind is null)
            {
                Fail(source, failures, $"Unsupported ArchiveXL resource operation '{name}'.", ArchiveXlFailureKind.Coverage);
                continue;
            }
            if (kind == ArchiveXlOperationKind.ResourcePatch && value is YamlSequenceNode patchSequence)
            {
                AddPatchSequence(source, patchSequence, operations, failures);
                continue;
            }
            if (value is not YamlMappingNode targets)
            {
                Fail(source, failures, $"ArchiveXL resource operation '{name}' must be a mapping.", ArchiveXlFailureKind.Malformed);
                continue;
            }
            foreach ((YamlNode target, YamlNode payload) in targets.Children)
            {
                string sourcePath = Scalar(target);
                if (kind is ArchiveXlOperationKind.ResourceLink or ArchiveXlOperationKind.ResourceCopy)
                {
                    foreach (string endpoint in LeafScalars(payload)) Add(source, operations, kind.Value, endpoint, sourcePath);
                }
                else if (kind == ArchiveXlOperationKind.ResourcePatch)
                {
                    YamlNode endpoints = payload is YamlMappingNode patch && TryGet(patch, "targets", out YamlNode? patchTargets) ? patchTargets! : payload;
                    foreach (string endpoint in LeafScalars(endpoints)) Add(source, operations, kind.Value, endpoint, sourcePath + "|" + Normalize(payload));
                }
                else Add(source, operations, kind.Value, sourcePath, Normalize(payload));
            }
        }
    }

    private static void AddPatchSequence(ArchiveXlSource source, YamlSequenceNode sequence, List<ArchiveXlOperation> operations, List<ArchiveXlSourceFailure> failures)
    {
        foreach (YamlNode item in sequence.Children)
        {
            if (item is not YamlMappingNode entry || !TryGet(entry, "source", out YamlNode? sourceNode) || string.IsNullOrWhiteSpace(Scalar(sourceNode!)))
            {
                Fail(source, failures, "ArchiveXL patch list entry must contain a source path.", ArchiveXlFailureKind.Malformed);
                continue;
            }
            if (!TryGet(entry, "targets", out YamlNode? targets))
            {
                Fail(source, failures, "ArchiveXL patch list entry must contain at least one target path.", ArchiveXlFailureKind.Malformed);
                continue;
            }
            string sourcePath = Scalar(sourceNode!);
            foreach (string endpoint in LeafScalars(targets!)) Add(source, operations, ArchiveXlOperationKind.ResourcePatch, endpoint, sourcePath + "|" + Normalize(entry));
        }
    }

    private static IEnumerable<string> LeafScalars(YamlNode node)
    {
        if (node is YamlScalarNode scalar)
        {
            if (!string.IsNullOrWhiteSpace(scalar.Value)) yield return scalar.Value.Trim();
            yield break;
        }
        if (node is YamlSequenceNode sequence)
        {
            foreach (YamlNode child in sequence.Children)
            {
                foreach (string value in LeafScalars(child)) yield return value;
            }
        }
    }

    private static void AddPathOperations(ArchiveXlSource source, YamlNode node, string childName, ArchiveXlOperationKind kind, List<ArchiveXlOperation> operations, List<ArchiveXlSourceFailure> failures)
    {
        if (node is not YamlMappingNode mapping || !TryGet(mapping, childName, out YamlNode? children)) return;
        if (children is not YamlSequenceNode sequence)
        {
            Fail(source, failures, $"ArchiveXL {childName} section must be a sequence.", ArchiveXlFailureKind.Malformed);
            return;
        }
        foreach (YamlNode item in sequence.Children)
        {
            if (item is YamlScalarNode scalar) Add(source, operations, kind, scalar.Value ?? string.Empty, Normalize(item));
            else if (item is YamlMappingNode entry && TryGet(entry, "path", out YamlNode? path)) Add(source, operations, kind, Scalar(path!), Normalize(item));
            else Fail(source, failures, $"ArchiveXL {childName} entry does not contain a path.", ArchiveXlFailureKind.Malformed);
        }
    }

    private static void AddSequence(ArchiveXlSource source, YamlNode node, ArchiveXlOperationKind kind, List<ArchiveXlOperation> operations, List<ArchiveXlSourceFailure> failures, string section)
    {
        if (node is not YamlSequenceNode sequence)
        {
            Fail(source, failures, $"ArchiveXL {section} section must be a sequence.", ArchiveXlFailureKind.Malformed);
            return;
        }
        foreach (YamlNode item in sequence.Children)
        {
            if (item is YamlScalarNode) Add(source, operations, kind, Scalar(item));
            else Fail(source, failures, $"ArchiveXL {section} entry must be a path.", ArchiveXlFailureKind.Malformed);
        }
    }

    private static void AddLeafScalars(ArchiveXlSource source, YamlNode node, ArchiveXlOperationKind kind, List<ArchiveXlOperation> operations, List<ArchiveXlSourceFailure> failures, string section)
    {
        int before = operations.Count;
        Visit(node);
        if (operations.Count == before) Fail(source, failures, $"ArchiveXL {section} section contains no readable paths.", ArchiveXlFailureKind.Malformed);

        void Visit(YamlNode current)
        {
            if (current is YamlScalarNode scalar)
            {
                Add(source, operations, kind, scalar.Value ?? string.Empty);
                return;
            }
            if (current is YamlSequenceNode sequence)
            {
                foreach (YamlNode child in sequence.Children) Visit(child);
                return;
            }
            if (current is YamlMappingNode mapping)
            {
                foreach (YamlNode child in mapping.Children.Values) Visit(child);
            }
        }
    }

    private static bool TryGet(YamlMappingNode mapping, string key, out YamlNode? value)
    {
        foreach ((YamlNode candidate, YamlNode child) in mapping.Children)
        {
            if (StringComparer.Ordinal.Equals(Scalar(candidate), key))
            {
                value = child;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static string Scalar(YamlNode node) => (node as YamlScalarNode)?.Value?.Trim() ?? string.Empty;

    private static string Normalize(YamlNode node)
        => node switch
        {
            YamlScalarNode scalar => scalar.Value?.Trim() ?? string.Empty,
            YamlSequenceNode sequence => "[" + string.Join(",", sequence.Children.Select(Normalize)) + "]",
            YamlMappingNode mapping => "{" + string.Join(",", mapping.Children.Select(value => Normalize(value.Key) + ":" + Normalize(value.Value)).OrderBy(value => value, StringComparer.Ordinal)) + "}",
            _ => string.Empty
        };

    private static bool AnalyzeRepeatedResources(ArchiveXlSource source, List<ArchiveXlOperation> operations, List<ArchiveXlSourceFailure> failures)
    {
        string[] lines = source.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (!TrySanitizedRoot(lines, out YamlMappingNode? root)) return false;
        List<ArchiveXlOperation> parsed = [];
        AnalyzeRoot(source, root!, parsed, failures, false);
        ValidateResourceChildren(source, root!, failures);
        ArchiveXlOperationKind? kind = null;
        int resourceCount = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string content = line.Trim();
            if (content.Length == 0 || content.StartsWith('#')) continue;
            int indent = line.Length - line.TrimStart().Length;
            if (indent == 2 && content.EndsWith(':'))
            {
                kind = content[..^1] switch
                {
                    "patch" => ArchiveXlOperationKind.ResourcePatch,
                    "link" => ArchiveXlOperationKind.ResourceLink,
                    "copy" => ArchiveXlOperationKind.ResourceCopy,
                    "scope" => ArchiveXlOperationKind.ResourceScope,
                    "fix" => ArchiveXlOperationKind.ResourceFix,
                    _ => null
                };
                continue;
            }
            if (indent != 4 || kind is null || !content.EndsWith(':')) continue;
            int end = index + 1;
            while (end < lines.Length)
            {
                string next = lines[end];
                if (!string.IsNullOrWhiteSpace(next) && !next.TrimStart().StartsWith('#') && next.Length - next.TrimStart().Length <= 4) break;
                end++;
            }
            string sourcePath = content[..^1];
            string[] payloadLines = lines[(index + 1)..end].Select(value => value.Trim()).Where(value => value.Length > 0 && !value.StartsWith('#')).ToArray();
            string payload = string.Join("|", payloadLines);
            if (kind is ArchiveXlOperationKind.ResourceLink or ArchiveXlOperationKind.ResourceCopy)
            {
                foreach (string endpoint in payloadLines.Where(value => value.StartsWith("- ", StringComparison.Ordinal)).Select(value => value[2..].Trim()))
                {
                    Add(source, parsed, kind.Value, endpoint, sourcePath);
                    resourceCount++;
                }
            }
            else if (kind == ArchiveXlOperationKind.ResourcePatch)
            {
                foreach (string endpoint in RepeatedPatchTargets(payloadLines))
                {
                    Add(source, parsed, kind.Value, endpoint, sourcePath + "|" + payload);
                    resourceCount++;
                }
            }
            else
            {
                Add(source, parsed, kind.Value, sourcePath, payload);
                resourceCount++;
            }
            index = end - 1;
        }
        if (resourceCount == 0) return false;
        operations.AddRange(parsed);
        return true;
    }

    private static IEnumerable<string> RepeatedPatchTargets(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            if (line.StartsWith("- ", StringComparison.Ordinal)) yield return line[2..].Trim();
            if (!line.StartsWith("targets:", StringComparison.Ordinal)) continue;
            string value = line["targets:".Length..].Trim();
            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                foreach (string target in value[1..^1].Split(',').Select(item => item.Trim()).Where(item => item.Length > 0)) yield return target;
            }
        }
    }

    private static void ValidateResourceChildren(ArchiveXlSource source, YamlMappingNode root, List<ArchiveXlSourceFailure> failures)
    {
        if (!TryGet(root, "resource", out YamlNode? resourceNode) || resourceNode is not YamlMappingNode resource)
        {
            Fail(source, failures, "ArchiveXL resource section must be a mapping.", ArchiveXlFailureKind.Malformed);
            return;
        }
        foreach ((YamlNode operationNode, YamlNode targets) in resource.Children)
        {
            string operation = Scalar(operationNode);
            bool known = operation is "patch" or "link" or "copy" or "scope" or "fix";
            if (!known) Fail(source, failures, $"Unsupported ArchiveXL resource operation '{operation}'.", ArchiveXlFailureKind.Coverage);
            else if (targets is not YamlMappingNode) Fail(source, failures, $"ArchiveXL resource operation '{operation}' must be a mapping.", ArchiveXlFailureKind.Malformed);
        }
    }

    private static bool TrySanitizedRoot(string[] lines, out YamlMappingNode? root)
    {
        string[] sanitized = lines.ToArray();
        bool resource = false;
        bool resourceOperation = false;
        int suffix = 0;
        for (int index = 0; index < sanitized.Length; index++)
        {
            string line = sanitized[index];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            int indent = line.Length - line.TrimStart().Length;
            string content = line.Trim();
            if (indent == 0)
            {
                resource = content == "resource:";
                resourceOperation = false;
            }
            else if (resource && indent == 2) resourceOperation = content is "patch:" or "link:" or "copy:" or "scope:" or "fix:";
            else if (resource && resourceOperation && indent == 4 && content.EndsWith(':')) sanitized[index] = line[..^1] + "__conflictstudio_" + suffix++.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
        }
        try
        {
            YamlStream validation = [];
            validation.Load(new StringReader(string.Join('\n', sanitized)));
            root = validation.Documents.Single().RootNode as YamlMappingNode;
            return root is not null;
        }
        catch (YamlException)
        {
            root = null;
            return false;
        }
    }

    private static void Add(ArchiveXlSource source, List<ArchiveXlOperation> operations, ArchiveXlOperationKind kind, string target, string? payload = null)
    {
        string normalized = target.Trim();
        if (normalized.Length > 0) operations.Add(new ArchiveXlOperation(source.Provider, source.FilePath, kind, normalized, payload?.Trim() ?? normalized));
    }

    private static void Fail(ArchiveXlSource source, List<ArchiveXlSourceFailure> failures, string message, ArchiveXlFailureKind kind)
    {
        if (!failures.Any(value => value.Provider == source.Provider && value.FilePath == source.FilePath && value.Message == message && value.Kind == kind)) failures.Add(new ArchiveXlSourceFailure(source.Provider, source.FilePath, message, kind));
    }
}
