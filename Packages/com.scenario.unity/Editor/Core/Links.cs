using System;

namespace Scenario.Editor.Core
{
    /// <summary>
    /// Scenario web app pages. The team and project ids open the workspace an item
    /// lives in; without them the web app shows the user's last workspace, where an
    /// asset from another team does not load.
    /// </summary>
    public static class Links
    {
        public const string App = "https://app.scenario.com";
        public const string ApiKeys = App + "/settings/organization-api-keys";
        public const string Repository = "https://github.com/scenario-labs/unity-plugin";

        public static string Home(string teamId = "", string projectId = "") => App + "/" + Scope("?", teamId, projectId);

        public static string Asset(string assetId, string teamId = "", string projectId = "") =>
            string.IsNullOrEmpty(assetId) ? "" : App + "/assets?openAssetId=" + Uri.EscapeDataString(assetId) + Scope("&", teamId, projectId);

        public static string Workflow(string workflowId, string teamId = "", string projectId = "") =>
            string.IsNullOrEmpty(workflowId) ? "" : App + "/workflows/" + Uri.EscapeDataString(workflowId) + Scope("?", teamId, projectId);

        static string Scope(string lead, string teamId, string projectId)
        {
            if (string.IsNullOrEmpty(teamId)) return "";
            var query = "teamId=" + Uri.EscapeDataString(teamId);
            if (!string.IsNullOrEmpty(projectId)) query += "&projectId=" + Uri.EscapeDataString(projectId);
            return lead + query;
        }
    }
}
