using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Scenario.Editor.UI
{
    /// <summary>Icons drawn for the plugin (white, tinted by USS) and the Scenario logo.</summary>
    public static class Icons
    {
        public const string Folder = "Packages/com.scenario.unity/Editor/UI/Icons/";
        static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();

        public static Texture2D Get(string name)
        {
            if (Cache.TryGetValue(name, out var texture) && texture != null) return texture;
            texture = AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + name + ".png");
            Cache[name] = texture;
            return texture;
        }
    }

    /// <summary>Small factories so every view builds the same controls.</summary>
    public static class Ui
    {
        public static VisualElement Icon(string name, string extraClass = null)
        {
            var icon = new VisualElement();
            icon.AddToClassList("sc-icon");
            if (extraClass != null) icon.AddToClassList(extraClass);
            icon.style.backgroundImage = new StyleBackground(Icons.Get(name));
            icon.pickingMode = PickingMode.Ignore;
            return icon;
        }

        public static void SetIcon(VisualElement icon, string name) => icon.style.backgroundImage = new StyleBackground(Icons.Get(name));

        public static Label Text(string text, params string[] classes)
        {
            var label = new Label(text);
            foreach (var c in classes) label.AddToClassList(c);
            return label;
        }

        public static Button Button(string text, Action click, string icon = null, params string[] classes)
        {
            var button = new Button(click) { text = "" };
            button.AddToClassList("sc-btn");
            foreach (var c in classes) button.AddToClassList(c);
            if (icon != null) button.Add(Icon(icon));
            if (!string.IsNullOrEmpty(text))
            {
                var label = new Label(text);
                label.pickingMode = PickingMode.Ignore;
                button.Add(label);
            }
            else if (icon != null)
            {
                button.AddToClassList("sc-icon-only");
            }
            return button;
        }

        public static Button IconButton(string icon, string tooltip, Action click, params string[] classes)
        {
            var button = Button(null, click, icon, classes);
            button.AddToClassList("sc-btn-ghost");
            button.tooltip = tooltip;
            return button;
        }

        public static void SetButtonText(Button button, string text)
        {
            var label = button.Q<Label>();
            if (label != null) label.text = text;
        }

        public static VisualElement Chip(string text, string icon, bool selected, Action click, params string[] classes)
        {
            var chip = new VisualElement();
            chip.AddToClassList("sc-chip");
            foreach (var c in classes) chip.AddToClassList(c);
            if (icon != null) chip.Add(Icon(icon));
            chip.Add(new Label(text) { pickingMode = PickingMode.Ignore });
            chip.EnableInClassList("sc-selected", selected);
            chip.RegisterCallback<ClickEvent>(_ => click());
            chip.focusable = true;
            chip.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.Space) click();
            });
            return chip;
        }

        public static Label Pill(string text, string kind = null)
        {
            var pill = new Label(text);
            pill.AddToClassList("sc-pill");
            if (kind != null) pill.AddToClassList("sc-pill-" + kind);
            return pill;
        }

        public static Label Section(string text)
        {
            var label = new Label(text.ToUpperInvariant());
            label.AddToClassList("sc-section");
            return label;
        }

        public static VisualElement Row(params VisualElement[] children)
        {
            var row = new VisualElement();
            row.AddToClassList("sc-row");
            foreach (var child in children) row.Add(child);
            return row;
        }

        public static VisualElement Spacer()
        {
            var spacer = new VisualElement();
            spacer.AddToClassList("sc-spacer");
            return spacer;
        }

        /// <summary>A vertical scroll area that takes the space left in its column, never scrolling sideways.</summary>
        public static ScrollView Scroll(bool padded = true)
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            scroll.AddToClassList("sc-scroll");
            scroll.AddToClassList("sc-fill");
            if (padded)
            {
                var content = scroll.contentContainer.style;
                content.paddingLeft = content.paddingRight = 14;
                content.paddingTop = content.paddingBottom = 12;
            }
            return scroll;
        }

        /// <summary>An on/off switch (a Toggle styled as a pill).</summary>
        public static Toggle Switch(bool value, Action<bool> changed, string label = null)
        {
            var toggle = new Toggle(label) { value = value };
            toggle.AddToClassList("sc-switch");
            toggle.RegisterValueChangedCallback(e => changed?.Invoke(e.newValue));
            return toggle;
        }

        public static VisualElement Card(params string[] classes)
        {
            var card = new VisualElement();
            card.AddToClassList("sc-card");
            foreach (var c in classes) card.AddToClassList(c);
            return card;
        }

        /// <summary>A rotating ring, for work in progress.</summary>
        public static VisualElement Spinner(bool small = false)
        {
            var spinner = new VisualElement();
            spinner.AddToClassList("sc-spinner");
            if (small) spinner.AddToClassList("sc-spinner-sm");
            spinner.pickingMode = PickingMode.Ignore;
            var angle = 0f;
            spinner.schedule.Execute(() =>
            {
                angle = (angle + 24f) % 360f;
                spinner.style.rotate = new Rotate(angle);
            }).Every(33);
            return spinner;
        }

        public static VisualElement Empty(string icon, string title, string body = null)
        {
            var box = new VisualElement();
            box.AddToClassList("sc-empty-state");
            box.Add(Icon(icon));
            box.Add(Text(title, "sc-bold"));
            if (!string.IsNullOrEmpty(body)) box.Add(Text(body, "sc-dim", "sc-wrap"));
            return box;
        }

        public static VisualElement Banner(string text, string kind = "info", string actionText = null, Action action = null)
        {
            var banner = new VisualElement();
            banner.AddToClassList("sc-banner");
            if (kind != "info") banner.AddToClassList("sc-banner-" + kind);
            var icon = Icon(kind == "error" ? "error" : kind == "warning" ? "warning" : kind == "success" ? "check" : "info");
            icon.style.marginRight = 8;
            banner.Add(icon);
            banner.Add(new Label(text));
            if (actionText != null && action != null)
            {
                var button = Button(actionText, action, null, "sc-btn-sm");
                button.style.marginLeft = 8;
                banner.Add(button);
            }
            return banner;
        }

        public static TextField Search(string placeholder, Action<string> changed)
        {
            var field = new TextField();
            field.AddToClassList("sc-search");
            field.textEdition.placeholder = placeholder;
            field.textEdition.hidePlaceholderOnFocus = false;
            var icon = Icon("search", "sc-search-icon");
            field.Add(icon);
            field.RegisterValueChangedCallback(e => changed(e.newValue));
            return field;
        }

        /// <summary>Opens a native context menu under an element.</summary>
        public static void Menu(VisualElement anchor, Action<GenericMenu> build)
        {
            var menu = new GenericMenu();
            build(menu);
            menu.DropDown(anchor.worldBound);
        }

        public static string Cu(double cu)
        {
            var rounded = Math.Round(cu, cu < 10 ? 1 : 0);
            return Core.Json.NumberText(rounded) + " CU";
        }

        public static string Ago(double unix)
        {
            var seconds = Core.Json.Now() - unix;
            if (seconds < 45) return "just now";
            if (seconds < 3600) return $"{Math.Round(seconds / 60)} min ago";
            if (seconds < 86400) return $"{Math.Round(seconds / 3600)} h ago";
            return DateTimeOffset.FromUnixTimeSeconds((long)unix).LocalDateTime.ToString("d MMM");
        }
    }
}
