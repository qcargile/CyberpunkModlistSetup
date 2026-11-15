using ConflictStudio.Core;
using System.Globalization;
using System.Text;

namespace ConflictStudio.App;

public sealed record DiagnosticAction(DateTimeOffset TimestampUtc, string Operation, string Outcome, string Detail);

public static class DiagnosticReportBuilder
{
    public static string Build(string version, string profile, string status, string scanDiagnostics, IReadOnlyList<DiagnosticAction> actions, string recentErrors, string manager = "Unknown")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(scanDiagnostics);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(recentErrors);
        ArgumentNullException.ThrowIfNull(manager);

        StringBuilder report = new();
        report.AppendLine("CONFLICT STUDIO SUPPORT REPORT");
        report.AppendLine(CultureInfo.InvariantCulture, $"Generated UTC: {DateTimeOffset.UtcNow:O}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Version: {version}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Manager: {manager}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Profile: {(string.IsNullOrWhiteSpace(profile) ? "No profile selected" : profile)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Status: {status}");
        report.AppendLine(CultureInfo.InvariantCulture, $"System: {Environment.OSVersion.VersionString} · .NET {Environment.Version}");
        report.AppendLine();
        report.AppendLine("WHAT HAPPENED");
        AppendActions(report, actions);
        report.AppendLine();
        report.AppendLine("CURRENT SCAN");
        report.AppendLine(string.IsNullOrWhiteSpace(scanDiagnostics) ? "No scan has run." : scanDiagnostics);
        report.AppendLine();
        report.AppendLine("APPLICATION ERRORS");
        report.AppendLine(string.IsNullOrWhiteSpace(recentErrors) ? "No application errors have been recorded." : recentErrors);
        report.AppendLine();
        report.AppendLine("No mod source contents are included. Physical paths are removed from this report.");
        return PrivatePathRedactor.Redact(report.ToString());
    }

    public static string BuildSessionView(string status, string scanDiagnostics, IReadOnlyList<DiagnosticAction> actions)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(scanDiagnostics);
        ArgumentNullException.ThrowIfNull(actions);
        StringBuilder view = new();
        view.AppendLine("CURRENT SESSION");
        view.AppendLine(CultureInfo.InvariantCulture, $"Status: {status}");
        view.AppendLine();
        AppendActions(view, actions);
        view.AppendLine();
        view.AppendLine("CURRENT SCAN");
        view.AppendLine(string.IsNullOrWhiteSpace(scanDiagnostics) ? "No scan has run." : scanDiagnostics);
        return PrivatePathRedactor.Redact(view.ToString());
    }

    private static void AppendActions(StringBuilder text, IReadOnlyList<DiagnosticAction> actions)
    {
        if (actions.Count == 0)
        {
            text.AppendLine("No actions have been recorded in this session.");
            return;
        }
        foreach (DiagnosticAction action in actions.TakeLast(100))
        {
            text.Append(action.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
            text.Append(" · ").Append(action.Operation).Append(" · ").Append(action.Outcome);
            if (!string.IsNullOrWhiteSpace(action.Detail)) text.Append(" · ").Append(action.Detail);
            text.AppendLine();
        }
    }
}
