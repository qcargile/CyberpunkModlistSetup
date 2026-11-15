using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ConflictStudio.Core;

public sealed record CodeSourceEvidence(
    ConflictSurface Surface,
    string Target,
    string Provider,
    string FilePath,
    string PhysicalPath,
    string SourceSha256,
    int StartLine,
    int EndLine,
    int FocusStartLine,
    int FocusEndLine,
    bool IsCompleteBlock,
    string Description)
{
    public int Occurrence { get; init; }

    public string OperationId { get; init; } = string.Empty;

    public CodeEvidenceOperationKind OperationKind { get; init; }

    public int OperationOccurrence { get; init; }

    public string? NormalizedValue { get; init; }

    public string? ValueType { get; init; }

    public string? Member { get; init; }

    public bool IsAliasSource { get; init; }

    public bool IsGeneratedSource { get; init; }
}

public sealed record CodeSourceDocument(CodeSourceEvidence Evidence, string[] Lines);

internal sealed record CodeSourceCapture(CodeSourceEvidence[] Evidence, TweakReferenceIndex TweakReferences);

public sealed class CodeSourceReadException : IOException
{
    public CodeSourceReadException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public static class CodeSourceReader
{
    public const int MaximumSourceBytes = 8 * 1024 * 1024;
    public const int MaximumSourceLines = 200_000;

    public static async Task<CodeSourceDocument> ReadAsync(CodeSourceEvidence evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(evidence.PhysicalPath) || evidence.SourceSha256.Length != 64 || evidence.SourceSha256.Any(value => !Uri.IsHexDigit(value)))
            throw new CodeSourceReadException($"{evidence.FilePath} has invalid source evidence. Scan again before comparing its source.");
        byte[] bytes;
        int count = 0;
        try
        {
            await using FileStream stream = new(evidence.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long openedLength = stream.Length;
            if (openedLength > MaximumSourceBytes) throw new CodeSourceReadException($"{evidence.FilePath} is larger than the 8 MB source comparison limit.");
            bytes = new byte[checked((int)openedLength + 1)];
            while (count < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(count, bytes.Length - count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > openedLength) throw new CodeSourceReadException($"{evidence.FilePath} changed after the scan. Scan again before comparing its source.");
        }
        catch (CodeSourceReadException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException exception) { throw new CodeSourceReadException($"{evidence.FilePath} is no longer available. Scan again before comparing its source.", exception); }
        catch (DirectoryNotFoundException exception) { throw new CodeSourceReadException($"{evidence.FilePath} is no longer available. Scan again before comparing its source.", exception); }
        catch (UnauthorizedAccessException exception) { throw new CodeSourceReadException($"{evidence.FilePath} cannot be read. Check its file permissions, then scan again.", exception); }
        catch (IOException exception) { throw new CodeSourceReadException($"{evidence.FilePath} could not be read. Scan again before comparing its source.", exception); }

        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, count)));
        if (!string.Equals(sha256, evidence.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new CodeSourceReadException($"{evidence.FilePath} changed after the scan. Scan again before comparing its source.");

        cancellationToken.ThrowIfCancellationRequested();
        string text;
        using (StreamReader reader = new(new MemoryStream(bytes, 0, count, false), Encoding.UTF8, true)) text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (LineCount(text) > MaximumSourceLines)
            throw new CodeSourceReadException($"{evidence.FilePath} has more than 200,000 lines and cannot be shown in source comparison. Open the file directly instead.");
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (evidence.StartLine < 1 || evidence.EndLine < evidence.StartLine || evidence.EndLine > lines.Length
            || evidence.FocusStartLine < evidence.StartLine || evidence.FocusEndLine < evidence.FocusStartLine || evidence.FocusEndLine > evidence.EndLine)
            throw new CodeSourceReadException($"{evidence.FilePath} has invalid source evidence. Scan again before comparing its source.");
        return new CodeSourceDocument(evidence, lines);
    }

    private static int LineCount(string text)
    {
        int lines = 1;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                lines++;
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
            }
            else if (text[index] == '\n') lines++;
            if (lines > MaximumSourceLines) return lines;
        }
        return lines;
    }
}

internal static class CodeSourceEvidenceBuilder
{
    private static readonly Regex Annotation = new("^[ \\t]*@(?<kind>wrapMethod|replaceMethod|addMethod|addField)\\((?<class>[^)]+)\\)", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex Field = new("\\blet\\s+(?<field>[A-Za-z_][A-Za-z0-9_]*)\\s*:[^;=]+(?:[=][^;]+)?;", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Lifecycle = new("\\bregisterForEvent\\s*\\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static CodeSourceEvidence[] Build(
        DeploymentFileManifest manifest,
        ModSourceInventory inventory,
        IReadOnlyList<InteractionFinding> interactions,
        IReadOnlyList<RedScriptFlowEvidence> flows,
        IReadOnlyList<LuaCallbackEvidence> callbacks,
        IReadOnlyList<SharedStateWrite> runtimeWrites,
        IReadOnlyList<SharedStateWriteFinding> sharedStateFindings,
        IReadOnlyList<TweakOperation> tweakOperations)
        => BuildCapture(manifest, inventory, interactions, flows, callbacks, runtimeWrites, sharedStateFindings, tweakOperations).Evidence;

    internal static CodeSourceCapture BuildCapture(
        DeploymentFileManifest manifest,
        ModSourceInventory inventory,
        IReadOnlyList<InteractionFinding> interactions,
        IReadOnlyList<RedScriptFlowEvidence> flows,
        IReadOnlyList<LuaCallbackEvidence> callbacks,
        IReadOnlyList<SharedStateWrite> runtimeWrites,
        IReadOnlyList<SharedStateWriteFinding> sharedStateFindings,
        IReadOnlyList<TweakOperation> tweakOperations)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(inventory);
        HashSet<string> interactionTargets = interactions.Select(value => value.Target).ToHashSet(StringComparer.Ordinal);
        Dictionary<(string Provider, string FilePath, string Target, int Line), Queue<RedScriptFieldDeclaration>> fieldDeclarations = interactions
            .SelectMany(finding => (finding.DeclarationEvidence ?? []).Select(value => (value.Provider, value.FilePath, finding.Target, value.Line)))
            .GroupBy(value => value)
            .ToDictionary(group => group.Key, group => new Queue<RedScriptFieldDeclaration>(interactions.SelectMany(finding => (finding.DeclarationEvidence ?? []).Where(value => value.Provider == group.Key.Provider && value.FilePath == group.Key.FilePath && finding.Target == group.Key.Target && value.Line == group.Key.Line))));
        HashSet<(SharedStateSurface Surface, string Target)> sharedTargets = sharedStateFindings.Select(value => (value.Surface, value.Target)).ToHashSet();
        Dictionary<(string Provider, string FilePath), SourceIdentity> identities = SourceIdentities(manifest, inventory);
        Dictionary<(string Provider, string FilePath), string> texts = inventory.RedScripts.Select(value => (value.Provider, value.FilePath, value.Text))
            .Concat(inventory.LuaSources.Select(value => (value.Provider, value.FilePath, value.Text)))
            .Concat(inventory.TweakSources.Select(value => (value.Provider, value.FilePath, value.Text)))
            .ToDictionary(value => (value.Provider, value.FilePath), value => value.Text);
        Dictionary<(string Provider, string FilePath), SourceContext> contexts = texts.Where(value => identities.ContainsKey(value.Key)).ToDictionary(
            value => value.Key,
            value => new SourceContext(value.Key.Provider, value.Key.FilePath, identities[value.Key].PhysicalPath, identities[value.Key].Sha256, value.Value, Lines(value.Value)));
        List<CodeSourceEvidence> evidence = [];

        foreach (RedScriptFlowEvidence flow in flows.Where(value => interactionTargets.Contains(value.Target)))
        {
            if (!TryContext(flow.Provider, flow.FilePath, contexts, out SourceContext context)) continue;
            int startLine = LineAt(context.Text, flow.SourceStartIndex >= 0 ? flow.SourceStartIndex : IndexAtLine(context.Text, flow.Line));
            int endLine = LineAt(context.Text, flow.SourceEndIndex >= 0 ? flow.SourceEndIndex : IndexAtLine(context.Text, flow.Line));
            evidence.Add(Bind(Create(ConflictSurface.ScriptAndTweak, flow.Target, context, startLine, endLine, flow.Line, endLine, true, "RedScript method"), flow.OperationId, CodeOperationIdentity.Kind(flow.Kind), flow.OperationOccurrence, flow.BodySha256, "method", flow.Target));
        }

        foreach (RedScriptSource source in RedScriptConditionalSourceFilter.Filter(inventory.RedScripts))
        {
            if (!TryContext(source.Provider, source.FilePath, contexts, out SourceContext context)) continue;
            string syntax = SourceTextMask.RedScript(source.Text, true);
            foreach (Match annotation in Annotation.Matches(syntax).Where(value => value.Groups["kind"].Value == "addField"))
            {
                Match field = Field.Match(syntax, annotation.Index + annotation.Length);
                Match nextAnnotation = annotation.NextMatch();
                if (!field.Success || nextAnnotation.Success && nextAnnotation.Index < field.Index) continue;
                string target = annotation.Groups["class"].Value.Trim() + "." + field.Groups["field"].Value;
                int fieldLine = LineAt(source.Text, field.Index);
                if (!fieldDeclarations.TryGetValue((source.Provider, source.FilePath, target, fieldLine), out Queue<RedScriptFieldDeclaration>? declarations) || declarations.Count == 0) continue;
                RedScriptFieldDeclaration declaration = declarations.Dequeue();
                int startLine = LineAt(source.Text, annotation.Index);
                int endLine = LineAt(source.Text, field.Index + field.Length - 1);
                evidence.Add(Bind(Create(ConflictSurface.ScriptAndTweak, target, context, startLine, endLine, endLine, endLine, true, "RedScript field"), declaration.OperationId, CodeEvidenceOperationKind.RedScriptAddField, declaration.OperationOccurrence, declaration.Type, "field type", target));
            }
        }

        Dictionary<(string Provider, string FilePath, string Target, LuaCallbackEvidenceKind Kind, int Line), Queue<LuaCallbackEvidence>> callbackOperations = callbacks
            .SelectMany(value => value.Copies.Select(copy => (copy.Provider, copy.FilePath, Callback: value)))
            .GroupBy(value => (value.Provider, value.FilePath, value.Callback.Target, value.Callback.Kind, value.Callback.Line))
            .ToDictionary(group => group.Key, group => new Queue<LuaCallbackEvidence>(group.Select(value => value.Callback)));
        foreach (LuaSource source in inventory.LuaSources)
        {
            if (!TryContext(source.Provider, source.FilePath, contexts, out SourceContext context)) continue;
            string syntax = SourceTextMask.Lua(source.Text, true);
            foreach (LuaHookRegistration registration in LuaHookRegistrationAnalyzer.Analyze(source.Text).Where(value => interactionTargets.Contains(value.Target)))
            {
                int start = registration.InvocationIndex;
                int end = registration.EvidenceEndIndex;
                if (end < start) end = LineEnd(source.Text, start);
                bool complete = registration.InvocationIndex == registration.RegistrationIndex && registration.InlineCallback;
                string description = complete ? "Lua hook callback" : registration.ResolvedHelper ? "Resolved Lua hook invocation" : "Lua hook registration excerpt";
                int startLine = LineAt(source.Text, start);
                int endLine = LineAt(source.Text, end);
                LuaCallbackEvidence? callback = callbackOperations.TryGetValue((source.Provider, source.FilePath, registration.Target, registration.Kind, registration.Line), out Queue<LuaCallbackEvidence>? matches) && matches.Count > 0 ? matches.Dequeue() : null;
                CodeSourceEvidence registrationEvidence = Create(ConflictSurface.ScriptAndTweak, registration.Target, context, startLine, endLine, startLine, endLine, complete, description);
                if (callback is not null) registrationEvidence = Bind(registrationEvidence, callback.OperationId, CodeOperationIdentity.Kind(callback.Kind), callback.OperationOccurrence, callback.CallbackSha256, "callback", callback.Target);
                evidence.Add(registrationEvidence);
                if (registration.CallbackStartIndex >= 0 && registration.CallbackEndIndex >= registration.CallbackStartIndex)
                {
                    int callbackStartLine = LineAt(source.Text, registration.CallbackStartIndex);
                    int callbackEndLine = LineAt(source.Text, registration.CallbackEndIndex);
                    evidence.Add(callback is null
                        ? Create(ConflictSurface.ScriptAndTweak, registration.Target, context, callbackStartLine, callbackEndLine, callbackStartLine, callbackEndLine, true, "Named Lua callback body")
                        : Bind(Create(ConflictSurface.ScriptAndTweak, registration.Target, context, callbackStartLine, callbackEndLine, callbackStartLine, callbackEndLine, true, "Named Lua callback body"), callback.OperationId, CodeOperationIdentity.Kind(callback.Kind), callback.OperationOccurrence, callback.CallbackSha256, "callback", callback.Target));
                }
            }
            foreach (Match lifecycle in Lifecycle.Matches(SourceTextMask.Lua(source.Text)))
            {
                int opening = syntax.IndexOf('(', lifecycle.Index);
                int end = CallEnd(syntax, opening);
                if (end < lifecycle.Index) end = LineEnd(source.Text, lifecycle.Index);
                string call = source.Text[lifecycle.Index..Math.Min(source.Text.Length, end + 1)];
                Match targetMatch = Regex.Match(call, "registerForEvent\\s*\\(\\s*[\"'](?<target>[^\"']+)[\"']", RegexOptions.CultureInvariant);
                if (!targetMatch.Success || !interactionTargets.Contains(targetMatch.Groups["target"].Value)) continue;
                int startLine = LineAt(source.Text, lifecycle.Index);
                int endLine = LineAt(source.Text, end);
                string target = targetMatch.Groups["target"].Value;
                CodeSourceEvidence lifecycleEvidence = Create(ConflictSurface.ScriptAndTweak, target, context, startLine, endLine, startLine, endLine, false, "Lua lifecycle callback excerpt");
                if (callbackOperations.TryGetValue((source.Provider, source.FilePath, target, LuaCallbackEvidenceKind.Lifecycle, startLine), out Queue<LuaCallbackEvidence>? lifecycleMatches) && lifecycleMatches.Count > 0)
                {
                    LuaCallbackEvidence callback = lifecycleMatches.Dequeue();
                    lifecycleEvidence = Bind(lifecycleEvidence, callback.OperationId, CodeOperationIdentity.Kind(callback.Kind), callback.OperationOccurrence, callback.CallbackSha256, "callback", callback.Target);
                }
                evidence.Add(lifecycleEvidence);
            }
        }

        foreach (TweakOperation operation in tweakOperations.Where(value => interactionTargets.Contains(value.Target)))
        {
            if (!TryContext(operation.Provider, operation.FilePath, contexts, out SourceContext context)) continue;
            int startLine = operation.SourceStartLine > 0 ? operation.SourceStartLine : operation.LineNumber;
            int endLine = operation.SourceEndLine >= startLine ? operation.SourceEndLine : startLine;
            string description = operation.IsAliasSource
                ? "TweakXL alias source excerpt"
                : operation.IsGeneratedSource
                    ? "Generated TweakXL source excerpt"
                    : operation.IsCompleteSourceBlock ? "TweakXL property" : "TweakXL source excerpt";
            evidence.Add(Bind(Create(ConflictSurface.ScriptAndTweak, operation.Target, context, startLine, endLine, startLine, endLine, operation.IsCompleteSourceBlock, description), operation.OperationId, CodeOperationIdentity.Kind(operation.Kind), operation.OperationOccurrence, operation.Value, TweakValueType(operation.Kind), TweakMember(operation)) with { IsAliasSource = operation.IsAliasSource, IsGeneratedSource = operation.IsGeneratedSource });
        }

        foreach (SharedStateWrite write in runtimeWrites.Where(value => interactionTargets.Contains(value.Target)))
        {
            if (!TryContext(write.Provider, write.FilePath, contexts, out SourceContext context)) continue;
            AddWrite(evidence, ConflictSurface.ScriptAndTweak, write.Target, write, context);
        }

        foreach (SharedStateWriteFinding finding in sharedStateFindings)
        {
            foreach (SharedStateWrite write in finding.Writes.Where(value => sharedTargets.Contains((value.Surface, value.Target))))
            {
                if (!TryContext(write.Provider, write.FilePath, contexts, out SourceContext context)) continue;
                AddWrite(evidence, ConflictSurface.SharedState, finding.Target, write, context);
            }
        }

        CodeSourceEvidence[] numbered = NumberOccurrences(evidence);
        return new CodeSourceCapture(numbered, BuildTweakReferenceIndex(interactionTargets, tweakOperations, contexts));
    }

    private static TweakReferenceIndex BuildTweakReferenceIndex(
        HashSet<string> interactionTargets,
        IReadOnlyList<TweakOperation> tweakOperations,
        IReadOnlyDictionary<(string Provider, string FilePath), SourceContext> contexts)
    {
        TweakOperation[] candidates = tweakOperations.Where(value => interactionTargets.Contains(value.Target) && TweakReferenceIndex.IsReferenceKind(CodeOperationIdentity.Kind(value.Kind))
                && TweakReferenceIndex.IsLiteral(value.Value) && !value.IsAliasSource && !value.IsGeneratedSource)
            .OrderBy(value => value.OperationId, StringComparer.Ordinal)
            .ToArray();
        TweakOperation[] captured = candidates.Take(TweakReferenceIndex.MaximumOperations).ToArray();
        TweakReferenceOperation[] operations = captured.Select(value => new TweakReferenceOperation(value.OperationId, value.Value))
            .ToArray();
        string[] references = operations.Select(value => value.Reference).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        ILookup<string, TweakOperation> constructionsByRecord = tweakOperations.Where(value => value.RecordTarget is not null
                && value.Kind is TweakOperationKind.TypeDeclaration or TweakOperationKind.BaseDeclaration
                && !value.IsAliasSource && !value.IsGeneratedSource)
            .ToLookup(value => value.RecordTarget!, StringComparer.Ordinal);
        List<TweakReferenceDefinitionSet> definitionSets = [];
        int retainedDefinitions = 0;
        foreach (string reference in references)
        {
            TweakOperation[] constructions = constructionsByRecord[reference].ToArray();
            var groups = constructions.GroupBy(value => (value.Provider, value.FilePath, value.RecordStartLine, value.RecordEndLine))
                .OrderBy(value => value.Key.Provider, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.Key.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.Key.RecordStartLine)
                .ToArray();
            int available = Math.Min(TweakReferenceIndex.MaximumDefinitionsPerReference, Math.Max(0, TweakReferenceIndex.MaximumDefinitions - retainedDefinitions));
            CodeSourceEvidence[] definitions = groups.Take(available).Select(group =>
            {
                if (!contexts.TryGetValue((group.Key.Provider, group.Key.FilePath), out SourceContext? context)) return null;
                return DefinitionEvidence(reference, group.ToArray(), context);
            }).Where(value => value is not null).Cast<CodeSourceEvidence>().ToArray();
            retainedDefinitions += definitions.Length;
            bool limited = groups.Length > definitions.Length;
            definitionSets.Add(new TweakReferenceDefinitionSet(reference, definitions, groups.Length, limited));
        }
        return new TweakReferenceIndex(operations, definitionSets.ToArray(), candidates.Length > TweakReferenceIndex.MaximumOperations || definitionSets.Any(value => value.IsLimited), candidates.Length);
    }

    internal static bool MatchesTweakDefinitions(TweakReferenceIndex index, HashSet<string> interactionTargets, DeploymentFileManifest manifest, IReadOnlyDictionary<string, string>? deployedWinners, IReadOnlySet<string>? excludedPhysicalPaths, CancellationToken cancellationToken)
    {
        List<TweakSource> sources = [];
        Dictionary<(string Provider, string FilePath), SourceContext> contexts = [];
        foreach (DeploymentFileEntry file in ModSourceScanner.EffectiveTweakFiles(manifest, deployedWinners, excludedPhysicalPaths, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ProfileFileSnapshot snapshot = manifest.Capture(file, true, cancellationToken);
                if (snapshot.Sha256 is null) return false;
                string text = manifest.ReadText(file, cancellationToken);
                sources.Add(new(file.Provider.Name, file.RelativePath, text));
                contexts.Add((file.Provider.Name, file.RelativePath), new(file.Provider.Name, file.RelativePath, file.PhysicalPath, snapshot.Sha256, text, Lines(text)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        }
        TweakOperation[] operations = TweakInteractionAnalyzer.AnalyzeDetailed(sources).Operations;
        cancellationToken.ThrowIfCancellationRequested();
        TweakReferenceIndex expected = BuildTweakReferenceIndex(interactionTargets, operations, contexts);
        if (index.TotalOperations != expected.TotalOperations || index.IsLimited != expected.IsLimited || !index.Operations.SequenceEqual(expected.Operations) || index.References.Length != expected.References.Length) return false;
        return index.References.Zip(expected.References).All(pair => pair.First.Reference == pair.Second.Reference
            && pair.First.TotalDefinitions == pair.Second.TotalDefinitions
            && pair.First.IsLimited == pair.Second.IsLimited
            && pair.First.Definitions.SequenceEqual(pair.Second.Definitions));
    }

    private static CodeSourceEvidence DefinitionEvidence(string reference, TweakOperation[] declarations, SourceContext context)
    {
        int focusStart = declarations.Min(value => value.SourceStartLine > 0 ? value.SourceStartLine : value.LineNumber);
        int focusEnd = declarations.Max(value => value.SourceEndLine >= focusStart ? value.SourceEndLine : focusStart);
        return Create(ConflictSurface.ScriptAndTweak, reference, context, declarations[0].RecordStartLine, declarations[0].RecordEndLine, focusStart, focusEnd, true, "TweakXL record definition");
    }

    private static CodeSourceEvidence[] NumberOccurrences(IEnumerable<CodeSourceEvidence> evidence)
    {
        return evidence.OrderBy(value => value.Surface)
            .ThenBy(value => value.Target, StringComparer.Ordinal)
            .ThenBy(value => value.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.StartLine)
            .ThenBy(value => value.EndLine)
            .GroupBy(value => (value.Surface, value.Target, value.Provider, value.FilePath))
            .SelectMany(group =>
            {
                ILookup<(int StartLine, int EndLine), CodeSourceEvidence> sharedRanges = group.ToLookup(value => (value.StartLine, value.EndLine));
                return group.Select((value, index) =>
                {
                    bool sharedRange = sharedRanges[(value.StartLine, value.EndLine)].Count() > 1;
                    string description = sharedRange && !value.Description.EndsWith("excerpt", StringComparison.OrdinalIgnoreCase) ? value.Description + " excerpt" : value.Description;
                    return value with { Occurrence = index + 1, IsCompleteBlock = value.IsCompleteBlock && !sharedRange, Description = description };
                });
            })
            .ToArray();
    }

    private static void AddWrite(List<CodeSourceEvidence> evidence, ConflictSurface surface, string target, SharedStateWrite write, SourceContext context)
    {
        int start = write.SourceStartIndex >= 0 ? write.SourceStartIndex : IndexAtLine(context.Text, write.Line);
        int end = write.SourceEndIndex >= start ? write.SourceEndIndex : LineEnd(context.Text, start);
        int startLine = LineAt(context.Text, start);
        int endLine = LineAt(context.Text, end);
        evidence.Add(Bind(Create(surface, target, context, startLine, endLine, write.Line, endLine, write.SourceEndIndex >= start, "Runtime value write"), write.OperationId, CodeOperationIdentity.Kind(write.Surface), write.OperationOccurrence, TweakRuntimeEvidence.ScalarLiteral(write), "scalar", write.Target));
    }

    private static Dictionary<(string Provider, string FilePath), SourceIdentity> SourceIdentities(DeploymentFileManifest manifest, ModSourceInventory inventory)
    {
        HashSet<(string Provider, string FilePath)> active = inventory.RedScripts.Select(value => (value.Provider, value.FilePath))
            .Concat(inventory.LuaSources.Select(value => (value.Provider, value.FilePath)))
            .Concat(inventory.TweakSources.Select(value => (value.Provider, value.FilePath)))
            .ToHashSet();
        Dictionary<(string Provider, string FilePath), SourceIdentity> identities = [];
        foreach (DeploymentFileEntry file in manifest.Files)
        {
            (string, string) key = (file.Provider.Name, file.RelativePath);
            if (!active.Contains(key) || identities.ContainsKey(key)) continue;
            ProfileFileSnapshot snapshot = manifest.Capture(file, true);
            if (snapshot.Sha256 is not null) identities.Add(key, new SourceIdentity(file.PhysicalPath, snapshot.Sha256));
        }
        return identities;
    }

    private static bool TryContext(string provider, string filePath, Dictionary<(string Provider, string FilePath), SourceContext> contexts, out SourceContext context)
    {
        return contexts.TryGetValue((provider, filePath), out context!);
    }

    private static CodeSourceEvidence Create(ConflictSurface surface, string target, SourceContext context, int startLine, int endLine, int focusStartLine, int focusEndLine, bool complete, string description)
    {
        int lineCount = context.Lines.Length;
        startLine = Math.Clamp(startLine, 1, lineCount);
        endLine = Math.Clamp(endLine, startLine, lineCount);
        focusStartLine = Math.Clamp(focusStartLine, startLine, endLine);
        focusEndLine = Math.Clamp(focusEndLine, focusStartLine, endLine);
        return new CodeSourceEvidence(surface, target, context.Provider, context.FilePath, context.PhysicalPath, context.Sha256, startLine, endLine, focusStartLine, focusEndLine, complete, description);
    }

    private static CodeSourceEvidence Bind(CodeSourceEvidence evidence, string operationId, CodeEvidenceOperationKind kind, int occurrence, string? value, string? valueType, string? member)
        => evidence with { OperationId = operationId, OperationKind = kind, OperationOccurrence = occurrence, NormalizedValue = value, ValueType = valueType, Member = member };

    private static string TweakValueType(TweakOperationKind kind) => kind switch
    {
        TweakOperationKind.TypeDeclaration => "record type",
        TweakOperationKind.BaseDeclaration => "base record",
        TweakOperationKind.ScalarAssignment => "scalar",
        TweakOperationKind.ArrayReplacement => "array",
        TweakOperationKind.InlineRecord => "record",
        _ => "array member"
    };

    private static string TweakMember(TweakOperation operation)
        => operation.Kind is TweakOperationKind.ArrayAppend or TweakOperationKind.ArrayAppendOnce or TweakOperationKind.ArrayPrepend or TweakOperationKind.ArrayPrependOnce or TweakOperationKind.ArrayRemove ? operation.Value : operation.Target;

    private static int CallEnd(string syntax, int opening)
    {
        if (opening < 0) return -1;
        int depth = 0;
        for (int index = opening; index < syntax.Length; index++)
        {
            if (syntax[index] == '(') depth++;
            else if (syntax[index] == ')' && --depth == 0) return index;
        }
        return -1;
    }

    private static int LineAt(string text, int index) => RedScriptFlowEvidenceAnalyzer.LineAt(text, Math.Clamp(index, 0, text.Length));

    private static int IndexAtLine(string text, int line)
    {
        if (line <= 1) return 0;
        int current = 1;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (++current == line) return index + (index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1);
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
            }
            else if (text[index] == '\n' && ++current == line) return index + 1;
        }
        return Math.Max(0, text.Length - 1);
    }

    private static int LineEnd(string text, int index)
    {
        int end = text.IndexOfAny(['\r', '\n'], Math.Clamp(index, 0, text.Length));
        return end < 0 ? Math.Max(0, text.Length - 1) : Math.Max(index, end - 1);
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private sealed record SourceIdentity(string PhysicalPath, string Sha256);
    private sealed record SourceContext(string Provider, string FilePath, string PhysicalPath, string Sha256, string Text, string[] Lines);
}
