using Scenario.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Scenario.Editor.UI
{
    /// <summary>Signed out: one button for the browser sign-in, an API key as the alternative (one primary action at a time).</summary>
    public sealed class SignInView : View
    {
        public override string Id => "signin";

        readonly VisualElement _box;
        readonly Button _primary;
        readonly VisualElement _waiting;
        readonly VisualElement _error;
        readonly VisualElement _keyForm;
        readonly TextField _key;
        readonly TextField _secret;
        readonly Button _keyButton;
        readonly Label _keyToggle;
        bool _showKey;

        public SignInView(ScenarioWindow window) : base(window)
        {
            var center = new VisualElement();
            center.AddToClassList("sc-signin");
            Add(center);
            _box = new VisualElement();
            _box.AddToClassList("sc-signin-box");
            center.Add(_box);

            var mark = new VisualElement();
            mark.AddToClassList("sc-signin-mark");
            mark.style.backgroundImage = new StyleBackground(Icons.Get("scenario-mark"));
            _box.Add(mark);
            _box.Add(Ui.Text("Scenario for Unity", "sc-signin-title"));
            _box.Add(Ui.Text("Every Scenario model, inside Unity.", "sc-signin-line"));

            _primary = Ui.Button("Sign in with Scenario", () => _ = S.SignInWithBrowser(), null, "sc-btn-primary");
            _primary.name = "signin-browser";
            _box.Add(_primary);

            _waiting = new VisualElement();
            _waiting.style.alignItems = Align.Center;
            _waiting.style.marginTop = 4;
            var waitingRow = Ui.Row(Ui.Spinner(true), Ui.Text("  Finish signing in in your browser…", "sc-dim"));
            _waiting.Add(waitingRow);
            var cancel = Ui.Button("Cancel", S.CancelSignIn, null, "sc-btn-ghost", "sc-btn-sm");
            cancel.style.marginTop = 6;
            _waiting.Add(cancel);
            _box.Add(_waiting);

            _error = new VisualElement();
            _error.style.marginTop = 12;
            _box.Add(_error);

            _keyForm = new VisualElement();
            _keyForm.style.marginTop = 6;
            _key = new TextField("API key") { name = "api-key" };
            _key.textEdition.placeholder = "Key, or paste key:secret";
            _key.textEdition.hidePlaceholderOnFocus = false;
            _key.RegisterValueChangedCallback(e =>
            {
                if (ApiKeyAuth.TrySplitPasted(e.newValue, out var key, out var secret))
                {
                    _key.SetValueWithoutNotify(key);
                    _secret.SetValueWithoutNotify(secret);
                }
            });
            StackLabel(_key);
            _keyForm.Add(_key);
            _secret = new TextField("Secret") { isPasswordField = true, name = "api-secret" };
            StackLabel(_secret);
            _secret.style.marginTop = 8;
            _secret.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) SignInWithKey();
            });
            _keyForm.Add(_secret);
            _keyButton = Ui.Button("Sign in with the key", SignInWithKey, null, "sc-btn-primary");
            _keyButton.style.marginTop = 10;
            _keyForm.Add(_keyButton);
            var links = Ui.Row(
                Link("Get an API key", Links.ApiKeys),
                Ui.Spacer(),
                Link("Create an account", Links.App));
            links.style.marginTop = 8;
            _keyForm.Add(links);
            _box.Add(_keyForm);

            _keyToggle = Ui.Text("Use an API key instead", "sc-link", "sc-small");
            _keyToggle.style.alignSelf = Align.Center;
            _keyToggle.style.marginTop = 14;
            _keyToggle.name = "use-api-key";
            _keyToggle.RegisterCallback<ClickEvent>(_ =>
            {
                _showKey = !_showKey;
                Refresh();
                if (_showKey) _key.Focus();
            });
            _box.Add(_keyToggle);

            _box.Add(Ui.Text("Prompts and files you use go to Scenario. Nothing from the plugin ships in your game build.", "sc-signin-foot"));
        }

        static void StackLabel(TextField field)
        {
            field.style.flexDirection = FlexDirection.Column;
            field.labelElement.style.minWidth = 0;
            field.labelElement.style.marginBottom = 4;
            field.labelElement.AddToClassList("sc-dim");
            field.labelElement.AddToClassList("sc-small");
        }

        static Label Link(string text, string url)
        {
            var link = Ui.Text(text, "sc-link", "sc-small");
            link.RegisterCallback<ClickEvent>(_ => Application.OpenURL(url));
            return link;
        }

        async void SignInWithKey()
        {
            _keyButton.SetEnabled(false);
            await S.SignInWithApiKey(_key.value, _secret.value);
            _keyButton.SetEnabled(true);
            if (S.IsSignedIn) _secret.SetValueWithoutNotify("");
        }

        public override void OnShow() => Refresh();

        public override void Refresh()
        {
            var signingIn = S.State == SignInState.SigningIn;
            _primary.style.display = signingIn || _showKey ? DisplayStyle.None : DisplayStyle.Flex;
            _waiting.style.display = signingIn && S.Method != AuthMethod.ApiKey && !_showKey ? DisplayStyle.Flex : DisplayStyle.None;
            _keyForm.style.display = _showKey ? DisplayStyle.Flex : DisplayStyle.None;
            _keyToggle.text = _showKey ? "Sign in with the browser instead" : "Use an API key instead";
            _error.Clear();
            if (S.SignInError.Length > 0) _error.Add(Ui.Banner(S.SignInError, "error"));
        }
    }

    /// <summary>After an OAuth sign-in with several projects: pick where generations go.</summary>
    public sealed class WorkspaceView : View
    {
        public override string Id => "workspace";

        readonly VisualElement _list;

        public WorkspaceView(ScenarioWindow window) : base(window)
        {
            var scroll = Ui.Scroll();
            Add(scroll);
            scroll.Add(Ui.Text("Pick a project", "sc-title"));
            scroll.Add(Ui.Text("Scenario keeps your generations, models and workflows in a project. Switch any time from the header.", "sc-subtitle"));
            _list = new VisualElement();
            scroll.Add(_list);
        }

        public override void OnShow()
        {
            if (S.Teams.Count == 0 && !S.TeamsLoading) _ = S.LoadTeams();
            Refresh();
        }

        public override void Refresh()
        {
            _list.Clear();
            if (S.TeamsLoading && S.Teams.Count == 0)
            {
                _list.Add(Ui.Row(Ui.Spinner(true), Ui.Text("  Loading your teams…", "sc-dim")));
                return;
            }
            if (S.Teams.Count == 0)
            {
                _list.Add(Ui.Banner("No team found for this account.", "warning", "Reload", () => _ = S.LoadTeams()));
                return;
            }
            foreach (var team in S.Teams)
            {
                var header = Ui.Row(Ui.Text(team.Name.ToUpperInvariant(), "sc-group-header"));
                if (team.Plan.Length > 0) header.Add(Ui.Pill(char.ToUpperInvariant(team.Plan[0]) + team.Plan.Substring(1)));
                _list.Add(header);
                if (team.Projects.Count == 0) _list.Add(Ui.Text("No project in this team yet. Create one on Scenario.", "sc-faint", "sc-small"));
                foreach (var project in team.Projects)
                {
                    var t = team;
                    var p = project;
                    var row = new VisualElement();
                    row.AddToClassList("sc-list-row");
                    row.name = "project-" + p.Id;
                    row.Add(Ui.Icon("folder"));
                    var label = Ui.Text(p.Name, "sc-bold");
                    label.style.marginLeft = 10;
                    row.Add(label);
                    row.Add(Ui.Spacer());
                    row.Add(Ui.Icon("chevron-right", "sc-icon-sm"));
                    row.RegisterCallback<ClickEvent>(_ => S.SelectWorkspace(t.Id, p.Id));
                    _list.Add(row);
                }
            }
            var foot = Ui.Row(Ui.Button("Reload", () => _ = S.LoadTeams(), "refresh", "sc-btn-ghost", "sc-btn-sm"), Ui.Spacer(),
                Ui.Button("Sign out", S.SignOut, "logout", "sc-btn-ghost", "sc-btn-sm"));
            foot.style.marginTop = 16;
            _list.Add(foot);
        }
    }
}
