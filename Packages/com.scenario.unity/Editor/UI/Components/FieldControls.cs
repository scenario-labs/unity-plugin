using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Scenario.Editor.Core;
using UnityEngine.UIElements;

namespace Scenario.Editor.UI
{
    /// <summary>
    /// One control per schema field (everything but files and the main prompt, which
    /// the Create view draws itself). Short enums become chips, ranges become sliders.
    /// </summary>
    public static class FieldControls
    {
        const int ChipLimit = 6;

        public static VisualElement Build(Field field, JObject values, Action changed)
        {
            var box = new VisualElement();
            box.AddToClassList("sc-field");
            box.name = "field-" + field.Name;
            if (field.Type != FieldType.Boolean) box.Add(Label(field));
            box.Add(Control(field, values, changed));
            return box;
        }

        static VisualElement Label(Field field)
        {
            var row = new VisualElement();
            row.AddToClassList("sc-field-label");
            row.Add(new Label(field.Label));
            if (field.CostImpact) row.Add(Ui.Text("changes the price", "sc-field-hint"));
            else if (field.Required) row.Add(Ui.Text("required", "sc-field-hint"));
            row.tooltip = field.Description;
            return row;
        }

        static VisualElement Control(Field field, JObject values, Action changed)
        {
            var value = values[field.Name];
            if (MotionLibrary.IsActionField(field) && MotionLibrary.All.Count > 0) return MotionPicker(field, values, changed);
            switch (field.Type)
            {
                case FieldType.Enum:
                    return field.Options.Count <= ChipLimit && field.Options.All(o => SchemaForm.OptionLabel(o).Length <= 14)
                        ? EnumChips(field, values, changed)
                        : EnumDropdown(field, values, changed);
                case FieldType.Boolean:
                {
                    var toggle = new Toggle(field.Label) { value = Json.Truthy(value) };
                    toggle.tooltip = field.Description;
                    toggle.RegisterValueChangedCallback(e => Set(values, field.Name, e.newValue, changed));
                    return toggle;
                }
                case FieldType.Integer:
                    if (field.Min.HasValue && field.Max.HasValue && field.Max.Value - field.Min.Value <= 1000)
                    {
                        var slider = new SliderInt((int)field.Min.Value, (int)field.Max.Value) { showInputField = true };
                        slider.SetValueWithoutNotify(value != null ? (int)Math.Round(Json.ToDouble(value)) : (int)field.Min.Value);
                        slider.RegisterValueChangedCallback(e => Set(values, field.Name, e.newValue, changed));
                        return slider;
                    }
                    else
                    {
                        var number = new IntegerField();
                        number.SetValueWithoutNotify(value != null ? (int)Math.Round(Json.ToDouble(value)) : 0);
                        number.RegisterValueChangedCallback(e => Set(values, field.Name, Clamp(field, e.newValue), changed));
                        return number;
                    }
                case FieldType.Number:
                    if (field.Min.HasValue && field.Max.HasValue)
                    {
                        var slider = new Slider((float)field.Min.Value, (float)field.Max.Value) { showInputField = true };
                        slider.SetValueWithoutNotify(value != null ? (float)Json.ToDouble(value) : (float)field.Min.Value);
                        slider.RegisterValueChangedCallback(e => Set(values, field.Name, Math.Round(e.newValue, 3), changed));
                        return slider;
                    }
                    else
                    {
                        var number = new FloatField();
                        number.SetValueWithoutNotify(value != null ? (float)Json.ToDouble(value) : 0f);
                        number.RegisterValueChangedCallback(e => Set(values, field.Name, Clamp(field, e.newValue), changed));
                        return number;
                    }
                case FieldType.Multi:
                    return field.Options.Count > 0 ? MultiChips(field, values, changed) : CommaText(field, values, changed);
                default:
                {
                    var text = new TextField { multiline = field.Type == FieldType.Prompt, value = value?.ToString() ?? "" };
                    if (field.MaxLength > 0) text.maxLength = field.MaxLength;
                    if (field.Type == FieldType.Prompt) text.AddToClassList("sc-prompt-small");
                    text.RegisterValueChangedCallback(e => Set(values, field.Name, e.newValue, changed));
                    return text;
                }
            }
        }

        static double Clamp(Field field, double value)
        {
            if (field.Min.HasValue) value = Math.Max(field.Min.Value, value);
            if (field.Max.HasValue) value = Math.Min(field.Max.Value, value);
            return value;
        }

        static void Set(JObject values, string name, JToken value, Action changed)
        {
            values[name] = value;
            changed();
        }

        static VisualElement EnumChips(Field field, JObject values, Action changed)
        {
            var row = new VisualElement();
            row.AddToClassList("sc-row-wrap");
            void Draw()
            {
                row.Clear();
                foreach (var option in field.Options)
                {
                    var o = option;
                    var selected = values[field.Name] != null && SchemaForm.InOptions(values[field.Name], new[] { o });
                    row.Add(Ui.Chip(SchemaForm.OptionLabel(o), null, selected, () =>
                    {
                        values[field.Name] = o.DeepClone();
                        Draw();
                        changed();
                    }, "sc-chip-sm"));
                }
            }
            Draw();
            return row;
        }

        static VisualElement EnumDropdown(Field field, JObject values, Action changed)
        {
            var labels = field.Options.Select(SchemaForm.OptionLabel).ToList();
            var dropdown = new DropdownField(labels, 0);
            var index = values[field.Name] == null ? -1 : field.Options.FindIndex(o => SchemaForm.InOptions(values[field.Name], new[] { o }));
            if (index >= 0) dropdown.SetValueWithoutNotify(labels[index]);
            else dropdown.SetValueWithoutNotify("Default");
            dropdown.RegisterValueChangedCallback(e =>
            {
                var i = labels.IndexOf(e.newValue);
                if (i >= 0) Set(values, field.Name, field.Options[i].DeepClone(), changed);
            });
            return dropdown;
        }

        static VisualElement MultiChips(Field field, JObject values, Action changed)
        {
            var row = new VisualElement();
            row.AddToClassList("sc-row-wrap");
            void Draw()
            {
                row.Clear();
                var current = values[field.Name] as JArray ?? new JArray();
                foreach (var option in field.Options)
                {
                    var o = option;
                    var selected = current.Any(v => JToken.DeepEquals(v, o));
                    row.Add(Ui.Chip(SchemaForm.OptionLabel(o), null, selected, () =>
                    {
                        var list = (values[field.Name] as JArray ?? new JArray()).ToList();
                        var existing = list.FirstOrDefault(v => JToken.DeepEquals(v, o));
                        if (existing != null) list.Remove(existing);
                        else list.Add(o.DeepClone());
                        values[field.Name] = new JArray(list);
                        Draw();
                        changed();
                    }, "sc-chip-sm"));
                }
            }
            Draw();
            return row;
        }

        static VisualElement CommaText(Field field, JObject values, Action changed)
        {
            var current = values[field.Name] as JArray;
            var text = new TextField { value = current != null ? string.Join(", ", current.Select(v => v.ToString())) : "" };
            text.tooltip = "Separate values with commas";
            text.RegisterValueChangedCallback(e =>
            {
                var items = e.newValue.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).Select(v => (JToken)v);
                Set(values, field.Name, new JArray(items), changed);
            });
            return text;
        }

        /// <summary>
        /// Meshy's motion library by name instead of by number: the current motion, and a
        /// search with category chips over the 656 motions.
        /// </summary>
        static VisualElement MotionPicker(Field field, JObject values, Action changed)
        {
            var box = new VisualElement { name = "motion-picker" };
            var current = Ui.Button("Motion", null, null, "sc-btn-outline", "sc-btn-sm");
            current.name = "motion-current";
            current.tooltip = "Meshy's motion library. The number sent is the motion's id.";
            current.Add(Ui.Icon("chevron-down", "sc-icon-sm"));
            current.style.alignSelf = Align.FlexStart;
            box.Add(current);
            var panel = new VisualElement { name = "motion-panel" };
            panel.style.display = DisplayStyle.None;
            panel.style.marginTop = 4;
            box.Add(panel);

            var category = "";
            var query = "";
            var results = new List<MotionLibrary.Motion>();
            void ShowCurrent()
            {
                var id = values[field.Name] != null ? (int)Math.Round(Json.ToDouble(values[field.Name])) : 0;
                var motion = MotionLibrary.Find(id);
                Ui.SetButtonText(current, motion != null ? $"{motion.Name} · {motion.Group}" : $"Motion #{id}");
            }

            var list = new ListView { name = "motion-list", fixedItemHeight = 26, selectionType = SelectionType.Single };
            list.style.height = 200;
            list.makeItem = () =>
            {
                var row = new VisualElement();
                row.AddToClassList("sc-row");
                row.style.paddingLeft = 6;
                var name = new Label { name = "name" };
                name.style.flexGrow = 1;
                row.Add(name);
                row.Add(Ui.Text("", "sc-dim", "sc-small"));
                return row;
            };
            list.bindItem = (element, i) =>
            {
                var motion = results[i];
                element.Q<Label>("name").text = motion.Name;
                ((Label)element[1]).text = motion.Group;
            };
            list.selectionChanged += picked =>
            {
                if (!(picked.FirstOrDefault() is MotionLibrary.Motion motion)) return;
                values[field.Name] = motion.Id;
                ShowCurrent();
                panel.style.display = DisplayStyle.None;
                changed();
            };
            void Filter()
            {
                results = MotionLibrary.Search(query, category);
                list.itemsSource = results;
                list.SetSelectionWithoutNotify(new int[0]);
                list.Rebuild();
            }

            var search = Ui.Search($"Search {MotionLibrary.All.Count} motions: walk, punch, dance", text =>
            {
                query = text;
                Filter();
            });
            search.name = "motion-search";
            panel.Add(search);
            var chips = new VisualElement();
            chips.AddToClassList("sc-row-wrap");
            chips.style.marginTop = 4;
            void DrawChips()
            {
                chips.Clear();
                foreach (var name in new[] { "" }.Concat(MotionLibrary.Categories))
                {
                    var c = name;
                    chips.Add(Ui.Chip(c.Length == 0 ? "All" : c, null, c == category, () =>
                    {
                        category = c;
                        DrawChips();
                        Filter();
                    }, "sc-chip-sm"));
                }
            }
            DrawChips();
            panel.Add(chips);
            panel.Add(list);

            current.clicked += () =>
            {
                var open = panel.style.display == DisplayStyle.None;
                panel.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
                if (open)
                {
                    Filter();
                    search.Focus();
                    // At the bottom of a long form the panel opens out of sight: bring it in once laid out.
                    panel.schedule.Execute(() => panel.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(panel)).ExecuteLater(50);
                }
            };
            ShowCurrent();
            return box;
        }

        /// <summary>Splits fields for the form: essentials and the rest ("More settings").</summary>
        public static (List<Field> essentials, List<Field> more) Split(IEnumerable<Field> fields, string promptName)
        {
            var settings = fields.Where(f => !f.IsFile && f.Name != promptName).ToList();
            return (settings.Where(f => f.IsEssential).ToList(), settings.Where(f => !f.IsEssential).ToList());
        }
    }
}
