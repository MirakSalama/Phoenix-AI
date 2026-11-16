using System.Text.Json;

namespace NpuChat;

public sealed partial class MainWindow
{
    private DecisionRouter? decisionRouter;
    private bool decisionUnavailable;

    private async Task<ToolDecision> ChooseRouteAsync(string text, bool canSearch, Uri? address, string? localReference)
    {
        if (decisionUnavailable || !workspaceUsable || !workspace.UseDecisionRouter || text.Length > 1500 ||
            !DecisionRouter.IsInstalled(project))
            return new();
        bool hasDocuments = TurnWorkspace.Knowledge.Count > 0;
        bool hasMemory = TurnWorkspace.Memory.Count > 0;
        bool canReadPage = address is not null && VisibleTurnServers.Any(IsApprovedChrome);
        var skills = TurnWorkspace.DecisionSkills(text, SharedTurnSkills);
        if (!canSearch && !canReadPage && !hasDocuments && !hasMemory && skills.Length == 0) return new();
        if (hasDocuments && ToolRouting.NamesLocalDocument(text)) return new(DecisionRoute.Documents, FromModel: false);
        SetPhase(Phase.PreparingTools, "Choosing a route with d1-omni-600M…");
        try
        {
            decisionRouter ??= new DecisionRouter(project);
            SetDecisionActivity(true);
            var decision = await decisionRouter.ChooseAsync(text, canSearch, address is not null, canReadPage,
                hasDocuments, hasMemory, skills, closing.Token, localReference);
            return workspace.UseDecisionRouter ? decision : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or
                                      OperationCanceledException or JsonException or InvalidOperationException or
                                      KeyNotFoundException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException)
        {
            decisionUnavailable = workspace.UseDecisionRouter;
            decisionRouter?.Dispose();
            decisionRouter = null;
            DiagnosticLog.Write("d1-router", $"Using standard routing: {error.GetType().Name}");
            return new();
        }
        finally { SetDecisionActivity(false); }
    }

    private void SetDecisionRouterEnabled(bool enabled)
    {
        bool previous = workspace.UseDecisionRouter;
        workspace.UseDecisionRouter = enabled;
        if (previous != enabled && !SaveWorkspace())
        {
            workspace.UseDecisionRouter = previous;
            throw new IOException("Could not save the decision router setting.");
        }
        decisionUnavailable = false;
        if (enabled) return;
        decisionRouter?.Dispose();
        decisionRouter = null;
    }
}
