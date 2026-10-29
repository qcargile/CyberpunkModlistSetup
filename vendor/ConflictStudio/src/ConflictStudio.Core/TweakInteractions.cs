using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ConflictStudio.Core;

public sealed record TweakSource(string Provider, string FilePath, string Text);

public enum TweakOverlapKind { Redundant, ScalarOverwrite, ComposableMutation, AssignmentThenMutation, MixedArrayOperations, DuplicateMutation, RecordDefinitionCollision, SourceArrayDependency, BaseRecordDependency, InternalContext, OpposingMutation }

public enum TweakOperationKind
{
    TypeDeclaration,
    BaseDeclaration,
    ScalarAssignment,
    ArrayReplacement,
    ArrayAppend,
    ArrayAppendOnce,
    ArrayPrepend,
    ArrayPrependOnce,
    ArrayRemove,
    InlineRecord,
    ArrayAppendFrom,
    ArrayPrependFrom
}

public sealed record TweakOperation(
    string Provider,
    string FilePath,
    string Target,
    string Value,
    bool IsMutation,
    TweakOperationKind Kind = TweakOperationKind.ScalarAssignment,
    int LineNumber = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string OperationId { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public int OperationOccurrence { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal int SourceStartLine { get; init; } = LineNumber;

    [System.Text.Json.Serialization.JsonIgnore]
    internal int SourceEndLine { get; init; } = LineNumber;

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool IsCompleteSourceBlock { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool IsGeneratedSource { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool IsAliasSource { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal string? RecordTarget { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal int RecordStartLine { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal int RecordEndLine { get; init; }
}

public sealed record TweakOverlap(string Target, TweakOverlapKind Kind, TweakOperation[] Operations);

public sealed record TweakAnalysisResult(TweakOverlap[] Overlaps, SourceAnalysisFailure[] Failures)
{
    public TweakOperation[] Operations { get; init; } = [];
}

public static class TweakInteractionAnalyzer
{
    public static TweakOverlap[] Analyze(IReadOnlyList<TweakSource> sources)
        => AnalyzeDetailed(sources).Overlaps;

    public static TweakAnalysisResult AnalyzeDetailed(IReadOnlyList<TweakSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        List<TweakOperation> operations = [];
        List<SourceAnalysisFailure> failures = [];
        foreach (TweakSource source in sources)
        {
            TryRead(source, operations, failures);
        }

        operations = CodeOperationIdentity.NumberTweaks(operations);

        List<TweakOverlap> overlaps = operations.GroupBy(value => value.Target, StringComparer.Ordinal)
            .Select(group =>
            {
                if (group.Select(value => value.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                    && !group.GroupBy(value => value.Value, StringComparer.Ordinal).Any(ValueHasOpposingMutations)) return InternalOverlap(group);
                TweakOverlapKind kind = Classify(group);
                return new TweakOverlap(group.Key, kind, RelevantOperations(group, kind));
            })
            .Where(ShouldReport)
            .ToList();
        overlaps.AddRange(SourceDependencies(operations));
        overlaps.AddRange(BaseDependencies(operations));
        return new TweakAnalysisResult(overlaps.OrderBy(value => value.Target, StringComparer.Ordinal).ToArray(), failures.ToArray()) { Operations = operations.ToArray() };
    }

    private static bool ShouldReport(TweakOverlap overlap)
        => overlap.Operations.Select(value => value.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            || overlap.Kind is TweakOverlapKind.ScalarOverwrite or TweakOverlapKind.MixedArrayOperations or TweakOverlapKind.DuplicateMutation or TweakOverlapKind.RecordDefinitionCollision or TweakOverlapKind.InternalContext or TweakOverlapKind.OpposingMutation;

    private static TweakOverlap InternalOverlap(IGrouping<string, TweakOperation> group)
    {
        TweakOperation[] assignments = group.Where(value => !value.IsMutation).ToArray();
        if (assignments.Select(OperationIdentity).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            if (assignments.Select(value => value.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                || assignments.All(value => value.Kind == TweakOperationKind.ScalarAssignment)
                && assignments.Select(value => value.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                return new TweakOverlap(group.Key, TweakOverlapKind.InternalContext, assignments);
            TweakOverlapKind kind = assignments.Any(value => value.Kind is TweakOperationKind.TypeDeclaration or TweakOperationKind.BaseDeclaration)
                ? TweakOverlapKind.RecordDefinitionCollision
                : assignments.Any(value => value.Kind == TweakOperationKind.ArrayReplacement)
                    ? TweakOverlapKind.MixedArrayOperations
                    : TweakOverlapKind.ScalarOverwrite;
            return new TweakOverlap(group.Key, kind, assignments);
        }
        return new TweakOverlap(group.Key, TweakOverlapKind.ComposableMutation, group.ToArray());
    }

    private static void TryRead(TweakSource source, List<TweakOperation> operations, List<SourceAnalysisFailure> failures)
    {
        List<TweakOperation> parsed = [];
        List<SourceAnalysisFailure> parsedFailures = [];
        try
        {
            ReadDocuments(source, source.Text, parsed, parsedFailures);
            AttachLooseLocations(source, parsed);
            operations.AddRange(parsed);
            failures.AddRange(parsedFailures);
        }
        catch (Exception exception) when (exception is YamlException or InvalidOperationException)
        {
            parsed.Clear();
            parsedFailures.Clear();
            try
            {
                ReadRepeatedDeclarations(source, parsed, parsedFailures);
                operations.AddRange(parsed);
                failures.AddRange(parsedFailures);
            }
            catch (Exception fallbackException) when (fallbackException is YamlException or InvalidOperationException)
            {
                parsed.Clear();
                parsedFailures.Clear();
                failures.Add(new SourceAnalysisFailure(source.Provider, source.FilePath, "TweakXL", $"TweakXL source could not be represented completely: {exception.Message}"));
            }
        }
    }

    private static void AttachLooseLocations(TweakSource source, List<TweakOperation> operations)
    {
        if (!source.Text.Contains('*', StringComparison.Ordinal)) return;
        List<TweakOperation> locations = [];
        try { ReadRepeatedDeclarations(source, locations, []); }
        catch (Exception exception) when (exception is YamlException or InvalidOperationException) { return; }
        Dictionary<(string Target, TweakOperationKind Kind, bool IsMutation), Queue<TweakOperation>> available = locations
            .GroupBy(value => (value.Target, value.Kind, value.IsMutation))
            .ToDictionary(group => group.Key, group => new Queue<TweakOperation>(group));
        for (int index = 0; index < operations.Count; index++)
        {
            TweakOperation operation = operations[index];
            if (!available.TryGetValue((operation.Target, operation.Kind, operation.IsMutation), out Queue<TweakOperation>? matches) || matches.Count == 0) continue;
            TweakOperation location = matches.Dequeue();
            if (!location.IsAliasSource) continue;
            operations[index] = operation with
            {
                SourceStartLine = location.SourceStartLine,
                SourceEndLine = location.SourceEndLine,
                IsCompleteSourceBlock = false,
                IsAliasSource = true
            };
        }
    }

    private static void ReadDocuments(TweakSource source, string text, List<TweakOperation> operations, List<SourceAnalysisFailure> failures)
    {
        YamlStream stream = [];
        stream.Load(new StringReader(text));
        foreach (YamlDocument document in stream.Documents)
        {
            if (document.RootNode is not YamlMappingNode root) throw new YamlException("TweakXL document root must be a mapping.");
            ReadRoot(source, root, operations, failures);
        }
    }

    private static void ReadRepeatedDeclarations(TweakSource source, List<TweakOperation> operations, List<SourceAnalysisFailure> failures)
    {
        foreach (LooseYamlNode document in LooseYaml.ParseAll(source.Text))
        {
            if (document is not LooseYamlMapping root) throw new YamlException("TweakXL document root must be a mapping.");
            foreach ((LooseYamlNode keyNode, LooseYamlNode valueNode) in root.Children)
            {
                if (keyNode is not LooseYamlScalar key || string.IsNullOrWhiteSpace(key.Value)) continue;
                string target = key.Value;
                if (valueNode is LooseYamlMapping record)
                {
                    int recordEndLine = LooseEndLine(record);
                    if (TryGetLoose(record, "$instances", out LooseYamlNode? instancesNode) && instancesNode is LooseYamlSequence instances)
                    {
                        foreach (LooseYamlNode instanceNode in instances.Children)
                        {
                            if (instanceNode is not LooseYamlMapping instance) continue;
                            Dictionary<string, string> variables = Variables(instance);
                            string generatedRecord = Substitute(target, variables);
                            foreach ((LooseYamlNode propertyKeyNode, LooseYamlNode propertyValue) in RecordProperties(record))
                            {
                                if (propertyKeyNode is not LooseYamlScalar propertyKey || string.IsNullOrWhiteSpace(propertyKey.Value) || propertyKey.Value == "$instances") continue;
                                List<TweakOperation> expanded = [];
                                AddLooseOperations(source, Substitute(target + "." + propertyKey.Value, variables), propertyKey.Value, propertyValue, expanded, failures, propertyKey.Line, record.IsAlias ? record.Line : null, generatedRecord, key.Line, recordEndLine);
                                operations.AddRange(expanded.Select(value => value with { Target = Substitute(value.Target, variables), Value = Substitute(value.Value, variables), IsCompleteSourceBlock = false, IsGeneratedSource = true }));
                            }
                        }
                        continue;
                    }
                    foreach ((LooseYamlNode propertyKeyNode, LooseYamlNode propertyValue) in RecordProperties(record))
                    {
                        if (propertyKeyNode is LooseYamlScalar propertyKey && !string.IsNullOrWhiteSpace(propertyKey.Value)) AddLooseOperations(source, target + "." + propertyKey.Value, propertyKey.Value, propertyValue, operations, failures, propertyKey.Line, record.IsAlias ? record.Line : null, target, key.Line, record.IsAlias ? key.Line : recordEndLine);
                    }
                }
                else AddLooseOperations(source, target, target.Split('.').Last(), valueNode, operations, failures, key.Line);
            }
        }
    }

    private static void AddLooseOperations(TweakSource source, string target, string property, LooseYamlNode value, List<TweakOperation> operations, List<SourceAnalysisFailure> failures, int propertyLine, int? parentAliasLine = null, string? recordTarget = null, int recordStartLine = 0, int recordEndLine = 0)
    {
        if (property.StartsWith('$') && property is not ("$type" or "$base")) return;
        int? aliasLine = parentAliasLine ?? (value.IsAlias ? value.Line : null);
        if (value is LooseYamlSequence sequence)
        {
            LooseYamlNode[] mutations = sequence.Children.Where(item => LooseMutationKind(item) is not null).ToArray();
            if (mutations.Length > 0)
            {
                foreach (LooseYamlNode item in mutations) operations.Add(WithRecord(CreateLoose(source, target, item, true, LooseMutationKind(item)!.Value, aliasLine, aliasLine is not null), recordTarget, recordStartLine, recordEndLine));
                if (sequence.Children.Any(item => string.IsNullOrEmpty(item.Tag))) AddMixedDefinitionFailure(source, target, failures);
            }
            else
            {
                operations.Add(WithRecord(CreateLoose(source, target, sequence, false, TweakOperationKind.ArrayReplacement, aliasLine ?? propertyLine, aliasLine is not null), recordTarget, recordStartLine, recordEndLine));
            }
            return;
        }
        if (value is LooseYamlMapping nested && (TryGetLoose(nested, "$type", out _) || TryGetLoose(nested, "$base", out _)))
        {
            int nestedEndLine = aliasLine ?? LooseEndLine(nested);
            foreach ((LooseYamlNode childKeyNode, LooseYamlNode childValue) in RecordProperties(nested))
            {
                if (childKeyNode is LooseYamlScalar childKey && !string.IsNullOrWhiteSpace(childKey.Value)) AddLooseOperations(source, target + "." + childKey.Value, childKey.Value, childValue, operations, failures, childKey.Line, aliasLine, target, propertyLine, nestedEndLine);
            }
            return;
        }
        TweakOperationKind kind = property switch { "$type" => TweakOperationKind.TypeDeclaration, "$base" => TweakOperationKind.BaseDeclaration, _ when value is LooseYamlMapping => TweakOperationKind.InlineRecord, _ => TweakOperationKind.ScalarAssignment };
        operations.Add(WithRecord(CreateLoose(source, DefinitionTarget(target, kind), value, false, kind, aliasLine ?? propertyLine, aliasLine is not null), recordTarget, recordStartLine, recordEndLine));
    }

    private static int LooseEndLine(LooseYamlNode node)
        => node switch
        {
            LooseYamlSequence sequence => sequence.Children.Select(LooseEndLine).Append(sequence.Line).Max(),
            LooseYamlMapping mapping => mapping.Children.SelectMany(value => new[] { LooseEndLine(value.Key), LooseEndLine(value.Value) }).Append(mapping.Line).Max(),
            _ => node.Line
        };

    private static TweakOperation CreateLoose(TweakSource source, string target, LooseYamlNode value, bool mutation, TweakOperationKind kind, int? sourceStartLine = null, bool parentAlias = false)
        => new(source.Provider, source.FilePath, target, NormalizeLoose(value), mutation, kind, value.Line)
        {
            SourceStartLine = sourceStartLine ?? value.Line,
            SourceEndLine = parentAlias ? sourceStartLine ?? value.Line : value.Line,
            IsCompleteSourceBlock = !parentAlias && !value.IsAlias && value is LooseYamlScalar,
            IsAliasSource = parentAlias || value.IsAlias
        };

    private static TweakOperationKind? LooseMutationKind(LooseYamlNode node) => node.Tag.ToLowerInvariant() switch { "!append" => TweakOperationKind.ArrayAppend, "!append-once" => TweakOperationKind.ArrayAppendOnce, "!append-from" => TweakOperationKind.ArrayAppendFrom, "!prepend" => TweakOperationKind.ArrayPrepend, "!prepend-once" => TweakOperationKind.ArrayPrependOnce, "!prepend-from" => TweakOperationKind.ArrayPrependFrom, "!remove" => TweakOperationKind.ArrayRemove, _ => null };

    private static bool TryGetLoose(LooseYamlMapping mapping, string name, out LooseYamlNode? value)
    {
        foreach ((LooseYamlNode key, LooseYamlNode child) in mapping.Children)
        {
            if (key is LooseYamlScalar scalar && scalar.Value == name)
            {
                value = child;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static IEnumerable<(LooseYamlNode Key, LooseYamlNode Value)> RecordProperties(LooseYamlMapping mapping)
    {
        LooseYamlNode? construction = mapping.Children.FirstOrDefault(value => value.Key is LooseYamlScalar { Value: "$base" }).Key
            ?? mapping.Children.FirstOrDefault(value => value.Key is LooseYamlScalar { Value: "$type" }).Key;
        Dictionary<string, LooseYamlNode> assignments = new(StringComparer.Ordinal);
        foreach ((LooseYamlNode key, LooseYamlNode value) in mapping.Children)
        {
            if (key is LooseYamlScalar scalar && IsPropertyAssignment(value)) assignments[scalar.Value] = key;
        }
        return mapping.Children.Where(value => value.Key is LooseYamlScalar { Value: "$type" or "$base" }
            ? ReferenceEquals(value.Key, construction)
            : value.Key is not LooseYamlScalar scalar || !IsPropertyAssignment(value.Value) || ReferenceEquals(assignments[scalar.Value], value.Key));
    }

    private static bool IsPropertyAssignment(LooseYamlNode value)
        => value switch
        {
            LooseYamlSequence sequence => !sequence.Children.Any(item => LooseMutationKind(item) is not null),
            LooseYamlMapping mapping => !TryGetLoose(mapping, "$type", out _) && !TryGetLoose(mapping, "$base", out _),
            _ => true
        };

    private static Dictionary<string, string> Variables(LooseYamlMapping mapping)
    {
        Dictionary<string, string> variables = new(StringComparer.Ordinal);
        foreach ((LooseYamlNode key, LooseYamlNode value) in mapping.Children)
        {
            if (key is not LooseYamlScalar scalar) continue;
            if (!variables.TryAdd(scalar.Value, NormalizeLoose(value))) throw new YamlException($"Duplicate TweakXL instance variable '{scalar.Value}'.");
        }
        return variables;
    }

    private static string NormalizeLoose(LooseYamlNode node)
        => node switch
        {
            LooseYamlScalar scalar => scalar.Value,
            LooseYamlSequence sequence => "[" + string.Join(",", sequence.Children.Select(NormalizeLoose)) + "]",
            LooseYamlMapping mapping => "{" + string.Join(",", mapping.Children.Select(value => NormalizeLoose(value.Key) + ":" + NormalizeLoose(value.Value))) + "}",
            _ => string.Empty
        };

    private static void ReadRoot(TweakSource source, YamlMappingNode root, List<TweakOperation> operations, List<SourceAnalysisFailure> failures)
    {
        foreach ((YamlNode keyNode, YamlNode valueNode) in root.Children)
        {
            if (keyNode is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value))
            {
                continue;
            }

            string target = key.Value;
            if (valueNode is YamlMappingNode record)
            {
                int recordEndLine = NodeEndLine(record);
                if (TryGet(record, "$instances", out YamlNode? instancesNode) && instancesNode is YamlSequenceNode instances)
                {
                    foreach (YamlNode instanceNode in instances.Children)
                    {
                        if (instanceNode is not YamlMappingNode instance) continue;
                        Dictionary<string, string> variables = Variables(instance);
                        string generatedRecord = Substitute(target, variables);
                        foreach ((YamlNode propertyKeyNode, YamlNode propertyValue) in RecordProperties(record))
                        {
                            if (propertyKeyNode is not YamlScalarNode propertyKey || string.IsNullOrWhiteSpace(propertyKey.Value) || propertyKey.Value == "$instances") continue;
                            List<TweakOperation> expanded = [];
                            AddOperations(source, Substitute(target + "." + propertyKey.Value, variables), propertyKey.Value, propertyValue, expanded, failures, checked((int)propertyKey.Start.Line), generatedRecord, checked((int)key.Start.Line), recordEndLine);
                            operations.AddRange(expanded.Select(value => value with { Target = Substitute(value.Target, variables), Value = Substitute(value.Value, variables), IsCompleteSourceBlock = false, IsGeneratedSource = true }));
                        }
                    }
                    continue;
                }
                foreach ((YamlNode propertyKeyNode, YamlNode propertyValue) in RecordProperties(record))
                {
                    if (propertyKeyNode is YamlScalarNode propertyKey && !string.IsNullOrWhiteSpace(propertyKey.Value))
                    {
                        AddOperations(source, target + "." + propertyKey.Value, propertyKey.Value, propertyValue, operations, failures, checked((int)propertyKey.Start.Line), target, checked((int)key.Start.Line), recordEndLine);
                    }
                }
            }
            else
            {
                AddOperations(source, target, target.Split('.').Last(), valueNode, operations, failures, checked((int)key.Start.Line));
            }
        }
    }

    private static bool TryGet(YamlMappingNode mapping, string name, out YamlNode? value)
    {
        foreach ((YamlNode key, YamlNode child) in mapping.Children)
        {
            if (key is YamlScalarNode scalar && scalar.Value == name)
            {
                value = child;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static IEnumerable<KeyValuePair<YamlNode, YamlNode>> RecordProperties(YamlMappingNode mapping)
    {
        YamlNode? construction = mapping.Children.FirstOrDefault(value => value.Key is YamlScalarNode { Value: "$base" }).Key
            ?? mapping.Children.FirstOrDefault(value => value.Key is YamlScalarNode { Value: "$type" }).Key;
        return mapping.Children.Where(value => value.Key is not YamlScalarNode { Value: "$type" or "$base" } || ReferenceEquals(value.Key, construction));
    }

    private static Dictionary<string, string> Variables(YamlMappingNode mapping)
    {
        Dictionary<string, string> variables = new(StringComparer.Ordinal);
        foreach ((YamlNode key, YamlNode value) in mapping.Children)
        {
            if (key is not YamlScalarNode scalar) continue;
            string name = scalar.Value ?? string.Empty;
            if (!variables.TryAdd(name, Normalize(value))) throw new YamlException($"Duplicate TweakXL instance variable '{name}'.");
        }
        return variables;
    }

    private static string Substitute(string value, IReadOnlyDictionary<string, string> variables)
    {
        string result = value;
        foreach ((string name, string replacement) in variables)
            result = result.Replace("$(" + name + ")", replacement, StringComparison.Ordinal).Replace("${" + name + "}", replacement, StringComparison.Ordinal);
        return result;
    }

    private static void AddOperations(TweakSource source, string target, string property, YamlNode value, List<TweakOperation> operations, List<SourceAnalysisFailure> failures, int propertyLine, string? recordTarget = null, int recordStartLine = 0, int recordEndLine = 0)
    {
        if (property.StartsWith('$') && property is not ("$type" or "$base")) return;
        if (value is YamlSequenceNode sequence)
        {
            YamlNode[] mutations = sequence.Children.Where(item => MutationKind(item) is not null).ToArray();
            if (mutations.Length > 0)
            {
                foreach (YamlNode item in mutations)
                {
                    operations.Add(WithRecord(Create(source, target, item, MutationKind(item)!.Value, true), recordTarget, recordStartLine, recordEndLine));
                }
                if (sequence.Children.Any(item => item.Tag.ToString() is "" or "?")) AddMixedDefinitionFailure(source, target, failures);
            }
            else
            {
                operations.Add(WithRecord(Create(source, target, value, TweakOperationKind.ArrayReplacement, false, propertyLine), recordTarget, recordStartLine, recordEndLine));
            }

            return;
        }

        if (value is YamlMappingNode nested && (TryGet(nested, "$type", out _) || TryGet(nested, "$base", out _)))
        {
            int nestedEndLine = NodeEndLine(nested);
            foreach ((YamlNode childKeyNode, YamlNode childValue) in RecordProperties(nested))
            {
                if (childKeyNode is YamlScalarNode childKey && !string.IsNullOrWhiteSpace(childKey.Value)) AddOperations(source, target + "." + childKey.Value, childKey.Value, childValue, operations, failures, checked((int)childKey.Start.Line), target, propertyLine, nestedEndLine);
            }
            return;
        }

        TweakOperationKind kind = property switch
        {
            "$type" => TweakOperationKind.TypeDeclaration,
            "$base" => TweakOperationKind.BaseDeclaration,
            _ when value is YamlMappingNode => TweakOperationKind.InlineRecord,
            _ => TweakOperationKind.ScalarAssignment
        };
        operations.Add(WithRecord(Create(source, DefinitionTarget(target, kind), value, kind, false, propertyLine), recordTarget, recordStartLine, recordEndLine));
    }

    private static TweakOperation WithRecord(TweakOperation operation, string? recordTarget, int startLine, int endLine)
        => operation with { RecordTarget = recordTarget, RecordStartLine = startLine, RecordEndLine = endLine };

    private static void AddMixedDefinitionFailure(TweakSource source, string target, List<SourceAnalysisFailure> failures)
        => failures.Add(new SourceAnalysisFailure(source.Provider, source.FilePath, "TweakXL interpretation", $"{target}: Mixed definition of array replacement and mutations. Only mutations will take effect."));

    private static TweakOperation Create(TweakSource source, string target, YamlNode value, TweakOperationKind kind, bool mutation, int? sourceStartLine = null)
    {
        int valueLine = checked((int)value.Start.Line);
        int startLine = sourceStartLine ?? valueLine;
        return new TweakOperation(source.Provider, source.FilePath, target, mutation ? NormalizeWithoutMutationTag(value) : Normalize(value), mutation, kind, valueLine)
        {
            SourceStartLine = startLine,
            SourceEndLine = Math.Max(startLine, NodeEndLine(value)),
            IsCompleteSourceBlock = true
        };
    }

    private static int NodeEndLine(YamlNode node)
    {
        int endLine = Math.Max(checked((int)node.Start.Line), checked((int)node.End.Line));
        if (node is YamlSequenceNode sequence)
            foreach (YamlNode child in sequence.Children) endLine = Math.Max(endLine, NodeEndLine(child));
        else if (node is YamlMappingNode mapping)
            foreach ((YamlNode key, YamlNode value) in mapping.Children)
            {
                endLine = Math.Max(endLine, NodeEndLine(key));
                endLine = Math.Max(endLine, NodeEndLine(value));
            }
        return endLine;
    }

    private static string DefinitionTarget(string target, TweakOperationKind kind)
    {
        if (kind is not (TweakOperationKind.TypeDeclaration or TweakOperationKind.BaseDeclaration)) return target;
        int separator = target.LastIndexOf('.');
        return separator < 0 ? target + ".$definition" : target[..separator] + ".$definition";
    }

    private static TweakOperationKind? MutationKind(YamlNode node)
    {
        string tag = node.Tag.ToString();
        if (tag.Equals("!append", StringComparison.OrdinalIgnoreCase))
        {
            return TweakOperationKind.ArrayAppend;
        }
        if (tag.Equals("!append-once", StringComparison.OrdinalIgnoreCase)) return TweakOperationKind.ArrayAppendOnce;
        if (tag.Equals("!append-from", StringComparison.OrdinalIgnoreCase)) return TweakOperationKind.ArrayAppendFrom;

        if (tag.Equals("!prepend", StringComparison.OrdinalIgnoreCase))
        {
            return TweakOperationKind.ArrayPrepend;
        }
        if (tag.Equals("!prepend-once", StringComparison.OrdinalIgnoreCase)) return TweakOperationKind.ArrayPrependOnce;
        if (tag.Equals("!prepend-from", StringComparison.OrdinalIgnoreCase)) return TweakOperationKind.ArrayPrependFrom;

        return tag.Equals("!remove", StringComparison.OrdinalIgnoreCase) ? TweakOperationKind.ArrayRemove : null;
    }

    private static string Normalize(YamlNode node)
    {
        string rawTag = node.Tag.ToString();
        string tag = rawTag is "" or "?" ? string.Empty : rawTag + " ";
        return node switch
        {
            YamlScalarNode scalar => tag + (scalar.Value ?? "null"),
            YamlSequenceNode sequence => tag + "[" + string.Join(",", sequence.Children.Select(NormalizeWithoutMutationTag)) + "]",
            YamlMappingNode mapping => tag + "{" + string.Join(",", mapping.Children
                .Select(pair => (Key: NormalizeWithoutMutationTag(pair.Key), Value: NormalizeWithoutMutationTag(pair.Value)))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + ":" + pair.Value)) + "}",
            _ => string.Empty
        };
    }

    private static string NormalizeWithoutMutationTag(YamlNode node)
    {
        string normalized = Normalize(node);
        int separator = normalized.IndexOf(' ');
        return MutationKind(node) is null || separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private static TweakOverlapKind Classify(IGrouping<string, TweakOperation> group)
    {
        TweakOperation[] operations = group.ToArray();
        if (operations.GroupBy(value => value.Value, StringComparer.Ordinal).Any(ValueHasOpposingMutations))
        {
            TweakOperation[] assignments = operations.Where(value => !value.IsMutation).ToArray();
            if (assignments.Any(value => value.Kind is TweakOperationKind.TypeDeclaration or TweakOperationKind.BaseDeclaration)) return TweakOverlapKind.RecordDefinitionCollision;
            if (assignments.Select(OperationIdentity).Distinct(StringComparer.Ordinal).Count() > 1)
                return assignments.Any(value => value.Kind == TweakOperationKind.ArrayReplacement) ? TweakOverlapKind.MixedArrayOperations : TweakOverlapKind.ScalarOverwrite;
            return TweakOverlapKind.OpposingMutation;
        }
        if (operations.All(value => !value.IsMutation) && operations.Select(OperationIdentity).Distinct(StringComparer.Ordinal).Count() == 1) return TweakOverlapKind.Redundant;
        if (operations.Select(OperationIdentity).Distinct(StringComparer.Ordinal).Count() == 1)
        {
            return operations[0].Kind is TweakOperationKind.ArrayAppend or TweakOperationKind.ArrayPrepend ? TweakOverlapKind.DuplicateMutation : TweakOverlapKind.Redundant;
        }
        bool equalByProvider = operations
            .GroupBy(value => value.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(provider => string.Join("\n", provider.Select(OperationIdentity).OrderBy(value => value, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Count() == 1;
        if (equalByProvider)
        {
            if (operations.Any(value => value.Kind is TweakOperationKind.ArrayAppend or TweakOperationKind.ArrayPrepend)) return TweakOverlapKind.DuplicateMutation;
            return TweakOverlapKind.Redundant;
        }
        if (operations.Any(value => value.Kind is TweakOperationKind.TypeDeclaration or TweakOperationKind.BaseDeclaration)) return TweakOverlapKind.RecordDefinitionCollision;

        if (operations.All(value => value.Kind == TweakOperationKind.ArrayReplacement)) return TweakOverlapKind.MixedArrayOperations;

        bool replacementAndMutation = operations.Any(value => !value.IsMutation && operations.Any(other => other.IsMutation && !string.Equals(value.Provider, other.Provider, StringComparison.OrdinalIgnoreCase)));
        if (replacementAndMutation)
        {
            TweakOperation[] assignments = operations.Where(value => !value.IsMutation).ToArray();
            return assignments.Select(OperationIdentity).Distinct(StringComparer.Ordinal).Count() > 1
                ? TweakOverlapKind.MixedArrayOperations
                : TweakOverlapKind.AssignmentThenMutation;
        }
        bool opposing = operations.GroupBy(value => value.Value, StringComparer.Ordinal).Any(ValueIsOrderSensitive);
        if (opposing) return TweakOverlapKind.MixedArrayOperations;
        if (operations.GroupBy(value => value.Value, StringComparer.Ordinal).Any(ValueHasDuplicatePlainAdds)) return TweakOverlapKind.DuplicateMutation;

        return operations.All(value => value.IsMutation)
            ? TweakOverlapKind.ComposableMutation
            : operations.Any(value => value.IsMutation)
                ? TweakOverlapKind.MixedArrayOperations
                : TweakOverlapKind.ScalarOverwrite;
    }

    private static TweakOperation[] RelevantOperations(IGrouping<string, TweakOperation> group, TweakOverlapKind kind)
    {
        TweakOperation[] operations = group.ToArray();
        if (operations.GroupBy(value => value.Value, StringComparer.Ordinal).Any(ValueHasOpposingMutations))
        {
            if (operations.Any(value => !value.IsMutation)) return operations;
            HashSet<string> competingValues = operations.GroupBy(value => value.Value, StringComparer.Ordinal)
                .Where(values => ValueHasOpposingMutations(values) || ValueIsOrderSensitive(values) || ValueHasDuplicatePlainAdds(values))
                .Select(values => values.Key)
                .ToHashSet(StringComparer.Ordinal);
            return operations.Where(value => competingValues.Contains(value.Value)).ToArray();
        }
        if (kind is not (TweakOverlapKind.MixedArrayOperations or TweakOverlapKind.DuplicateMutation)) return operations;
        if (operations.All(value => value.Kind == TweakOperationKind.ArrayReplacement)) return operations;
        bool replacementAndMutation = operations.Any(value => !value.IsMutation && operations.Any(other => other.IsMutation && !string.Equals(value.Provider, other.Provider, StringComparison.OrdinalIgnoreCase)));
        if (replacementAndMutation) return operations;
        HashSet<string> contestedValues = operations.GroupBy(value => value.Value, StringComparer.Ordinal)
            .Where(kind == TweakOverlapKind.MixedArrayOperations ? ValueIsOrderSensitive : ValueHasDuplicatePlainAdds)
            .Select(values => values.Key)
            .ToHashSet(StringComparer.Ordinal);
        return operations.Where(value => contestedValues.Contains(value.Value)).ToArray();
    }

    internal static bool ValueHasOpposingMutations(IEnumerable<TweakOperation> values)
        => values.Any(removal => removal.Kind == TweakOperationKind.ArrayRemove
            && !values.Any(addition => IsAddition(addition.Kind) && !IsArrayCopy(addition.Kind) && SameComponent(removal, addition))
            && values.Any(addition => IsAddition(addition.Kind) && !IsArrayCopy(addition.Kind)
                && !SameComponent(removal, addition)));

    internal static bool SameComponent(TweakOperation first, TweakOperation second)
        => string.Equals(first.Provider, second.Provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.FilePath, second.FilePath, StringComparison.OrdinalIgnoreCase);

    internal static bool ValueIsOrderSensitive(IEnumerable<TweakOperation> values)
    {
        if (values.Select(value => value.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) return false;
        bool hasPlainAdd = values.Any(value => value.Kind is TweakOperationKind.ArrayAppend or TweakOperationKind.ArrayPrepend);
        bool hasUniqueAdd = values.Any(value => value.Kind is TweakOperationKind.ArrayAppendOnce or TweakOperationKind.ArrayPrependOnce);
        return hasPlainAdd && hasUniqueAdd;
    }

    internal static bool ValueHasDuplicatePlainAdds(IEnumerable<TweakOperation> values)
        => values.Select(value => value.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            && values.Count(value => value.Kind is TweakOperationKind.ArrayAppend or TweakOperationKind.ArrayPrepend) > 1
            && values.All(value => IsAddition(value.Kind));

    internal static bool IsAddition(TweakOperationKind kind)
        => kind is TweakOperationKind.ArrayAppend or TweakOperationKind.ArrayAppendOnce or TweakOperationKind.ArrayAppendFrom or TweakOperationKind.ArrayPrepend or TweakOperationKind.ArrayPrependOnce or TweakOperationKind.ArrayPrependFrom;

    internal static bool IsArrayCopy(TweakOperationKind kind)
        => kind is TweakOperationKind.ArrayAppendFrom or TweakOperationKind.ArrayPrependFrom;

    private static IEnumerable<TweakOverlap> SourceDependencies(IReadOnlyList<TweakOperation> operations)
    {
        foreach (IGrouping<(string Target, string Source), TweakOperation> copies in operations.Where(value => IsArrayCopy(value.Kind)).GroupBy(value => (value.Target, value.Value)))
        {
            TweakOperation[] sourceWrites = operations.Where(value => value.Target == copies.Key.Source && copies.Any(copy => !string.Equals(value.Provider, copy.Provider, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (sourceWrites.Length == 0) continue;
            yield return new TweakOverlap(copies.Key.Target + " <- " + copies.Key.Source, TweakOverlapKind.SourceArrayDependency, [.. copies, .. sourceWrites]);
        }
    }

    private static string OperationIdentity(TweakOperation operation)
        => operation.Kind + ":" + operation.Value;

    private static IEnumerable<TweakOverlap> BaseDependencies(IReadOnlyList<TweakOperation> operations)
    {
        ILookup<string, TweakOperation> properties = operations.Where(value => value.Kind is not (TweakOperationKind.BaseDeclaration or TweakOperationKind.TypeDeclaration))
            .ToLookup(value => RecordTarget(value.Target), StringComparer.Ordinal);
        foreach (IGrouping<(string Record, string Base), TweakOperation> clones in operations.Where(value => value.Kind == TweakOperationKind.BaseDeclaration)
            .GroupBy(value => (RecordTarget(value.Target), value.Value)))
        {
            if (clones.Key.Record == clones.Key.Base) continue;
            TweakOperation[] baseWrites = properties[clones.Key.Base].Where(value => clones.Any(clone => !string.Equals(value.Provider, clone.Provider, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (baseWrites.Length == 0) continue;
            HashSet<string> changedProperties = baseWrites.Select(value => value.Target[(clones.Key.Base.Length + 1)..]).ToHashSet(StringComparer.Ordinal);
            TweakOperation[] derivedWrites = properties[clones.Key.Record].Where(value => changedProperties.Contains(value.Target[(clones.Key.Record.Length + 1)..])).ToArray();
            yield return new TweakOverlap(clones.Key.Record + " <- " + clones.Key.Base, TweakOverlapKind.BaseRecordDependency, [.. clones, .. baseWrites, .. derivedWrites]);
        }
    }

    private static string RecordTarget(string target)
    {
        int separator = target.LastIndexOf('.');
        return separator < 0 ? string.Empty : target[..separator];
    }
}
