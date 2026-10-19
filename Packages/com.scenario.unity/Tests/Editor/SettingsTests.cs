using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;

namespace Scenario.Editor.Tests
{
    /// <summary>
    /// The settings files load back on the next start. Unity links a saved settings object to its class
    /// only when the class sits in a file of the same name; otherwise the file is written with no script
    /// reference and every start reads the defaults (project, port, model choices).
    /// </summary>
    public class SettingsTests
    {
        [Test]
        public void SettingsClasses_HaveTheirScript()
        {
            Assert.IsNotNull(MonoScript.FromScriptableObject(ScenarioUserSettings.instance), "ScenarioUserSettings belongs in ScenarioUserSettings.cs");
            Assert.IsNotNull(MonoScript.FromScriptableObject(ScenarioProjectSettings.instance), "ScenarioProjectSettings belongs in ScenarioProjectSettings.cs");
        }

        [Test]
        public void SavedUserSettings_ReferenceTheirScript()
        {
            var path = Path.Combine(TempDir.Make("settings"), "ScenarioUserSettings.asset");
            InternalEditorUtility.SaveToSerializedFileAndForget(new UnityEngine.Object[] { ScenarioUserSettings.instance }, path, true);
            var text = File.ReadAllText(path);
            StringAssert.DoesNotContain("m_Script: {fileID: 0}", text);
            StringAssert.Contains("m_Script: {fileID: 11500000, guid: ", text);
        }
    }
}
