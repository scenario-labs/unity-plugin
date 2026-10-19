using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Scenario.Editor
{
    /// <summary>Per-user, per-project state (UserSettings/ScenarioUserSettings.asset, not committed).</summary>
    [FilePath("UserSettings/ScenarioUserSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class ScenarioUserSettings : ScriptableSingleton<ScenarioUserSettings>
    {
        public string TeamId = "";
        public string TeamName = "";
        public string ProjectId = "";
        public string ProjectName = "";
        public string Plan = "";

        public string OutputType = "image";
        public List<string> TypeModelKeys = new List<string>();
        public List<string> TypeModelValues = new List<string>();
        public List<string> Favorites = new List<string>();
        public List<string> RecentModels = new List<string>();
        public string LastPrompt = "";
        public string View = "create";
        public float TrayHeight = 220;

        /// <summary>Refuses every paid call (same effect as SCENARIO_NO_SPEND=1).</summary>
        public bool SpendingDisabled;

        public bool AgentEnabled;
        public int AgentPort = 8791;
        public int AgentAutoApproveCu = 80;
        public int AgentSessionBudgetCu = 1200;
        public bool AgentCanEditScene = true;
        /// <summary>The claude command for Ask; empty finds it on PATH and in the usual install folders.</summary>
        public string ClaudeCodePath = "";

        public string ModelFor(string outputType)
        {
            var index = TypeModelKeys.IndexOf(outputType);
            return index >= 0 && index < TypeModelValues.Count ? TypeModelValues[index] : "";
        }

        public void SetModelFor(string outputType, string modelId)
        {
            var index = TypeModelKeys.IndexOf(outputType);
            if (index >= 0) TypeModelValues[index] = modelId;
            else
            {
                TypeModelKeys.Add(outputType);
                TypeModelValues.Add(modelId);
            }
            RecentModels.Remove(modelId);
            RecentModels.Insert(0, modelId);
            if (RecentModels.Count > 12) RecentModels.RemoveRange(12, RecentModels.Count - 12);
            Save();
        }

        public bool ToggleFavorite(string modelId)
        {
            var added = !Favorites.Remove(modelId);
            if (added) Favorites.Insert(0, modelId);
            Save();
            return added;
        }

        const string TokenPref = "Scenario.AgentToken";

        /// <summary>The agent bridge token: per user and machine (EditorPrefs), outside the project.</summary>
        public string EnsureAgentToken(bool regenerate = false)
        {
            var token = EditorPrefs.GetString(TokenPref, "");
            if (regenerate || string.IsNullOrEmpty(token))
            {
                token = "sut_" + Core.OAuthClient.RandomToken(24);
                EditorPrefs.SetString(TokenPref, token);
            }
            return token;
        }

        public void Save() => Save(true);
    }
}
