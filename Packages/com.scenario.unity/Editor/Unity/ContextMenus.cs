using System.IO;
using System.Linq;
using Scenario.Editor.Core;
using Scenario.Editor.UI;
using UnityEditor;
using UnityEngine;

namespace Scenario.Editor
{
    /// <summary>
    /// Entry points outside the window: Project window and Hierarchy context menus,
    /// and Assets > Create > Scenario. Steps open Create ready to go (nothing is sent);
    /// Make Playable changes the scene at once, in one Undo step.
    /// </summary>
    static class ContextMenus
    {
        const int Priority = 1600;

        // --- Project window --------------------------------------------------------------

        static string SelectedFolder()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(path)) return "";
            return AssetDatabase.IsValidFolder(path) ? path : Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
        }

        [MenuItem("Assets/Scenario/Generate Here…", false, Priority)]
        static void GenerateHere()
        {
            var window = ScenarioWindow.OpenView("create");
            window.Create.SetFolder(SelectedFolder());
            window.Create.FocusPrompt();
        }

        [MenuItem("Assets/Scenario/Generate Here…", true)]
        static bool GenerateHereValid() => AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(Selection.activeObject));

        [MenuItem("Assets/Scenario/Next Step…", false, Priority + 1)]
        static void NextStep()
        {
            var asset = Selection.activeObject;
            var kind = InputSlot.KindOf(asset);
            var menu = new GenericMenu();
            CreateView.StepsMenu(menu, kind, step => ScenarioWindow.OpenView("create").Create.StartStep(step, Item(asset, kind)));
            menu.ShowAsContext();
        }

        [MenuItem("Assets/Scenario/Next Step…", true)]
        static bool NextStepValid() => OutputTypes.StepsFor(InputSlot.KindOf(Selection.activeObject)).Any() && !AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(Selection.activeObject));

        [MenuItem("Assets/Scenario/Use as Input", false, Priority + 2)]
        static void UseAsInput()
        {
            var window = ScenarioWindow.OpenView("create");
            window.Create.AttachObjects(Selection.objects.Where(o => InputSlot.KindOf(o).Length > 0));
        }

        [MenuItem("Assets/Scenario/Use as Input", true)]
        static bool UseAsInputValid() => Selection.objects.Any(o => InputSlot.KindOf(o).Length > 0 && !AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(o)));

        [MenuItem("Assets/Scenario/Copy Scenario Asset Id", false, Priority + 3)]
        static async void UploadAndCopy()
        {
            var asset = Selection.activeObject;
            var info = Provenance.Read(asset);
            if (info != null && info.AssetId.Length > 0)
            {
                EditorGUIUtility.systemCopyBuffer = info.AssetId;
                Debug.Log($"Scenario: copied {info.AssetId}.");
                return;
            }
            var result = await ScenarioService.Instance.Upload(asset);
            if (!result.Ok)
            {
                Debug.LogWarning("Scenario: " + result.Error.Message);
                return;
            }
            EditorGUIUtility.systemCopyBuffer = result.Value;
            Debug.Log($"Scenario: uploaded {asset.name} as {result.Value} (copied).");
        }

        [MenuItem("Assets/Scenario/Copy Scenario Asset Id", true)]
        static bool UploadAndCopyValid() => ScenarioService.Instance.Ready && InputSlot.KindOf(Selection.activeObject).Length > 0;

        static SlotItem Item(Object asset, string kind)
        {
            var info = Provenance.Read(asset);
            return new SlotItem { AssetId = info?.AssetId ?? "", Source = asset, Kind = kind, Label = asset.name };
        }

        // --- Assets > Create > Scenario ---------------------------------------------------

        static void CreateType(string type)
        {
            var window = ScenarioWindow.OpenView("create");
            window.Create.SetFolder(SelectedFolder());
            window.Create.SelectType(type);
            window.Create.FocusPrompt();
        }

        [MenuItem("Assets/Create/Scenario/Image", false, 0)] static void CreateImage() => CreateType(OutputTypes.Image);
        [MenuItem("Assets/Create/Scenario/3D Model", false, 1)] static void CreateModel() => CreateType(OutputTypes.Model3D);
        [MenuItem("Assets/Create/Scenario/Material", false, 2)] static void CreateMaterial() => CreateType(OutputTypes.Material);
        [MenuItem("Assets/Create/Scenario/Skybox", false, 3)] static void CreateSkybox() => CreateType(OutputTypes.Skybox);
        [MenuItem("Assets/Create/Scenario/Sound", false, 4)] static void CreateSound() => CreateType(OutputTypes.Audio);
        [MenuItem("Assets/Create/Scenario/Video", false, 5)] static void CreateVideo() => CreateType(OutputTypes.Video);

        // --- Hierarchy ---------------------------------------------------------------------

        static bool SceneObjectSelected() => Selection.activeGameObject != null && !EditorUtility.IsPersistent(Selection.activeGameObject);

        [MenuItem("GameObject/Scenario/Generate Material for This", false, 30)]
        static void MaterialForThis() => OpenFor(OutputTypes.Material);

        [MenuItem("GameObject/Scenario/Generate Sprite for This", false, 31)]
        static void SpriteForThis() => OpenFor(OutputTypes.Image);

        [MenuItem("GameObject/Scenario/Generate Sound for This", false, 32)]
        static void SoundForThis() => OpenFor(OutputTypes.Audio);

        static void OpenFor(string type)
        {
            var window = ScenarioWindow.OpenView("create");
            window.Create.SelectType(type);
            window.Create.FocusPrompt();
        }

        [MenuItem("GameObject/Scenario/Next Step…", false, 29)]
        static void SceneNextStep()
        {
            var context = SceneActions.Current();
            var menu = new GenericMenu();
            CreateView.StepsMenu(menu, context.Kind, step => SceneActions.OpenStep(step, context));
            menu.ShowAsContext();
        }

        [MenuItem("GameObject/Scenario/Next Step…", true)]
        static bool SceneNextStepValid() => SceneObjectSelected() && SceneActions.Current().Steps.Any();

        [MenuItem("GameObject/Scenario/Retexture", false, 40)]
        static void Retexture()
        {
            // A placed Scenario model is retextured from its own asset; anything else is exported as GLB.
            var context = SceneActions.Current();
            if (context.Mode == SelectionContext.Object3D) SceneActions.OpenStep(OutputTypes.Step("retexture"), context);
            else
            {
                var go = Selection.activeGameObject;
                ScenarioWindow.OpenView("create").Create.StartStep(OutputTypes.Step("retexture"), new SlotItem { Source = go, Kind = "3d", Label = go.name });
            }
        }

        [MenuItem("GameObject/Scenario/Use as Reference", false, 41)]
        static void UseAsReference()
        {
            ScenarioWindow.OpenView("create").Create.AttachObjects(new Object[] { Selection.activeGameObject });
        }

        [MenuItem("GameObject/Scenario/Make Playable", false, 42)]
        static void MakePlayable()
        {
            var outcome = SceneActions.MakePlayable(SceneActions.Current().Target);
            SceneActions.Report(outcome);
            if (outcome.Ok) Selection.activeGameObject = outcome.Object;
        }

        [MenuItem("GameObject/Scenario/Make Playable", true)]
        static bool MakePlayableValid() => SceneObjectSelected() && SceneActions.Current().CanMakePlayable;

        [MenuItem("GameObject/Scenario/Make Walkable", false, 43)]
        static void MakeWalkable() => SceneActions.Report(SceneActions.MakeWalkable(Selection.activeGameObject));

        [MenuItem("GameObject/Scenario/Make Walkable", true)]
        static bool MakeWalkableValid() => SceneObjectSelected() && Selection.activeGameObject.GetComponentInChildren<MeshFilter>() != null;

        [MenuItem("GameObject/Scenario/Generate Material for This", true)]
        [MenuItem("GameObject/Scenario/Generate Sprite for This", true)]
        [MenuItem("GameObject/Scenario/Generate Sound for This", true)]
        [MenuItem("GameObject/Scenario/Retexture", true)]
        [MenuItem("GameObject/Scenario/Use as Reference", true)]
        static bool SceneObjectValid() => SceneObjectSelected();
    }
}
